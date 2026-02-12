using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimplyShare.Core.Crypto;
using SimplyShare.Models;

namespace SimplyShare.Core.Transfer;

/// <summary>
/// TCP 기반 전송 클라이언트 (전송 측)
/// </summary>
public sealed class TransferClient
{
    private readonly Services.ISettingsService _settingsService;

    /// <summary>진행률 변경 이벤트</summary>
    public event Action<TransferProgress>? ProgressChanged;

    public TransferClient(Services.ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// 대상 장치에 TCP Ping 전송 — 자신의 존재를 알림 (UDP 단방향 실패 대비)
    /// </summary>
    public async Task SendPingAsync(DeviceInfo target, CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.Ping,
            SenderNickname = _settingsService.Settings.Nickname,
            SenderDeviceId = _settingsService.Settings.DeviceId,
            SenderPort = _settingsService.Settings.TransferPort,
            TotalSize = 0
        };

        using var client = await ConnectAsync(target, cancellationToken);
        using var crypto = new CryptoService();
        var stream = client.GetStream();

        await PerformKeyExchangeAsync(stream, crypto, cancellationToken);
        await SendTransferRequestAsync(stream, crypto, request, cancellationToken);
        await WaitForResponseAsync(stream, crypto, cancellationToken);
    }

    /// <summary>
    /// 대상 장치와 지속 채팅 연결 수립 — Ping 후 연결을 유지하여 양방향 텍스트 통신에 사용.
    /// 반환된 ChatConnection의 소유권은 호출자에게 이전됨.
    /// </summary>
    public async Task<ChatConnection> ConnectChatAsync(DeviceInfo target, CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.Ping,
            SenderNickname = _settingsService.Settings.Nickname,
            SenderDeviceId = _settingsService.Settings.DeviceId,
            SenderPort = _settingsService.Settings.TransferPort,
            TotalSize = 0
        };

        var client = await ConnectAsync(target, cancellationToken);
        var crypto = new CryptoService();
        var stream = client.GetStream();

        try
        {
            await PerformKeyExchangeAsync(stream, crypto, cancellationToken);
            await SendTransferRequestAsync(stream, crypto, request, cancellationToken);
            await WaitForResponseAsync(stream, crypto, cancellationToken);

            // 연결을 유지 — ChatConnection이 소유권을 가짐
            return new ChatConnection(client, stream, crypto, target.DeviceId, target.Nickname);
        }
        catch
        {
            crypto.Dispose();
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 텍스트를 대상 장치에 전송
    /// </summary>
    public async Task SendTextAsync(DeviceInfo target, string text, CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.Text,
            SenderNickname = _settingsService.Settings.Nickname,
            SenderDeviceId = _settingsService.Settings.DeviceId,
            SenderPort = _settingsService.Settings.TransferPort,
            TextContent = text,
            TotalSize = Encoding.UTF8.GetByteCount(text)
        };

        using var client = await ConnectAsync(target, cancellationToken);
        using var crypto = new CryptoService();
        var stream = client.GetStream();

        await PerformKeyExchangeAsync(stream, crypto, cancellationToken);
        await SendTransferRequestAsync(stream, crypto, request, cancellationToken);

        var accepted = await WaitForResponseAsync(stream, crypto, cancellationToken);
        if (!accepted)
            throw new OperationCanceledException("Transfer was rejected by the receiver.");
    }

    /// <summary>
    /// 파일/폴더를 대상 장치에 전송
    /// </summary>
    public async Task SendFilesAsync(DeviceInfo target, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var files = BuildFileList(paths);
        var totalSize = files.Sum(f => f.Size);
        var transferId = Guid.NewGuid().ToString("N")[..8];

        var request = new TransferRequest
        {
            Type = files.Count is 1 ? TransferType.File : TransferType.Files,
            SenderNickname = _settingsService.Settings.Nickname,
            SenderDeviceId = _settingsService.Settings.DeviceId,
            SenderPort = _settingsService.Settings.TransferPort,
            Files = files,
            TotalSize = totalSize
        };

        using var client = await ConnectAsync(target, cancellationToken);
        using var crypto = new CryptoService();
        var stream = client.GetStream();

        await PerformKeyExchangeAsync(stream, crypto, cancellationToken);
        await SendTransferRequestAsync(stream, crypto, request, cancellationToken);

        var accepted = await WaitForResponseAsync(stream, crypto, cancellationToken);
        if (!accepted)
        {
            NotifyProgress(transferId, null, 0, totalSize, TransferStatus.Rejected, target.Nickname);
            return;
        }

        // 파일 전송
        long totalSent = 0;

        foreach (var fileInfo in files)
        {
            if (fileInfo.IsDirectory)
                continue;

            // 원본 파일 경로 찾기
            var sourcePath = FindSourcePath(paths, fileInfo.RelativePath);
            if (sourcePath is null)
                continue;

            await using var fileStream = File.OpenRead(sourcePath);
            var buffer = new byte[NetworkDefaults.FileChunkSize];
            int bytesRead;

            while ((bytesRead = await fileStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var chunk = buffer.AsSpan(0, bytesRead);
                var encrypted = crypto.Encrypt(chunk);
                await SendRawAsync(stream, encrypted, cancellationToken);

                totalSent += bytesRead;
                NotifyProgress(transferId, fileInfo.RelativePath, totalSent, totalSize,
                    TransferStatus.InProgress, target.Nickname);
            }
        }

        NotifyProgress(transferId, null, totalSize, totalSize, TransferStatus.Completed, target.Nickname);
    }

    /// <summary>
    /// 상대 장치에서 앱 업데이트를 다운로드
    /// </summary>
    /// <returns>다운로드된 EXE 파일 임시 경로</returns>
    public async Task<string?> RequestUpdateAsync(DeviceInfo source, CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.AppUpdate,
            SenderNickname = _settingsService.Settings.Nickname,
            SenderDeviceId = _settingsService.Settings.DeviceId,
            SenderPort = _settingsService.Settings.TransferPort,
            TotalSize = 0
        };

        using var client = await ConnectAsync(source, cancellationToken);
        using var crypto = new CryptoService();
        var stream = client.GetStream();

        await PerformKeyExchangeAsync(stream, crypto, cancellationToken);
        await SendTransferRequestAsync(stream, crypto, request, cancellationToken);

        var accepted = await WaitForResponseAsync(stream, crypto, cancellationToken);
        if (!accepted)
            return null;

        // 파일 크기 수신
        var sizeEncrypted = await ReadRawAsync(stream, cancellationToken);
        var sizeString = Encoding.UTF8.GetString(crypto.Decrypt(sizeEncrypted));
        if (!long.TryParse(sizeString, out var fileSize) || fileSize <= 0)
            return null;

        // 임시 파일에 다운로드
        var tempPath = Path.Combine(Path.GetTempPath(), $"SimplyShare_update_{Guid.NewGuid():N}.exe");
        await using var fileStream = File.Create(tempPath);
        long received = 0;

        while (received < fileSize)
        {
            var encrypted = await ReadRawAsync(stream, cancellationToken);
            var chunk = crypto.Decrypt(encrypted);
            await fileStream.WriteAsync(chunk, cancellationToken);
            received += chunk.Length;
        }

        return tempPath;
    }

    // --- 헬퍼 메서드 ---

    private static async Task<TcpClient> ConnectAsync(DeviceInfo target, CancellationToken cancellationToken)
    {
        var client = new TcpClient
        {
            NoDelay = true,
            SendBufferSize = NetworkDefaults.SocketBufferSize,
            ReceiveBufferSize = NetworkDefaults.SocketBufferSize
        };

        await client.ConnectAsync(target.IpAddress, target.Port, cancellationToken);
        return client;
    }

    private static async Task PerformKeyExchangeAsync(NetworkStream stream, CryptoService crypto, CancellationToken cancellationToken)
    {
        // 상대방 공개 키 수신 (서버가 먼저 보냄)
        var peerPublicKey = await ReadRawAsync(stream, cancellationToken);

        // 내 공개 키 전송
        var myPublicKey = crypto.ExportPublicKey();
        await SendRawAsync(stream, myPublicKey, cancellationToken);

        // 세션 키 유도
        crypto.DeriveSessionKey(peerPublicKey);
    }

    private static async Task SendTransferRequestAsync(NetworkStream stream, CryptoService crypto,
        TransferRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, AppJsonContext.Default.TransferRequest);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encrypted = crypto.Encrypt(plainBytes);
        await SendRawAsync(stream, encrypted, cancellationToken);
    }

    private static async Task<bool> WaitForResponseAsync(NetworkStream stream, CryptoService crypto,
        CancellationToken cancellationToken)
    {
        var encrypted = await ReadRawAsync(stream, cancellationToken);
        var decrypted = crypto.Decrypt(encrypted);
        var json = Encoding.UTF8.GetString(decrypted);
        var response = JsonSerializer.Deserialize(json, AppJsonContext.Default.TransferMessage);
        return response?.Type is TransferMessageType.TransferAccepted;
    }

    private static List<FileTransferInfo> BuildFileList(IReadOnlyList<string> paths)
    {
        var files = new List<FileTransferInfo>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var dirName = Path.GetFileName(path);
                files.Add(new FileTransferInfo { RelativePath = dirName, Size = 0, IsDirectory = true });

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.Combine(dirName, Path.GetRelativePath(path, file));
                    var info = new FileInfo(file);
                    files.Add(new FileTransferInfo { RelativePath = relativePath, Size = info.Length });
                }
            }
            else if (File.Exists(path))
            {
                var info = new FileInfo(path);
                files.Add(new FileTransferInfo { RelativePath = info.Name, Size = info.Length });
            }
        }

        return files;
    }

    private static string? FindSourcePath(IReadOnlyList<string> basePaths, string relativePath)
    {
        foreach (var basePath in basePaths)
        {
            if (Directory.Exists(basePath))
            {
                var candidate = Path.Combine(Path.GetDirectoryName(basePath)!, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
            else if (File.Exists(basePath) && Path.GetFileName(basePath) == relativePath)
            {
                return basePath;
            }
        }

        return null;
    }

    private void NotifyProgress(string transferId, string? currentFile, long sent, long total,
        TransferStatus status, string peerNickname)
    {
        ProgressChanged?.Invoke(new TransferProgress
        {
            TransferId = transferId,
            CurrentFileName = currentFile,
            BytesTransferred = sent,
            TotalBytes = total,
            Status = status,
            PeerNickname = peerNickname,
            Direction = TransferDirection.Send
        });
    }

    // --- 저수준 프레임 읽기/쓰기 ---

    private static async Task SendRawAsync(NetworkStream stream, byte[] data, CancellationToken cancellationToken)
    {
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
        await stream.WriteAsync(lengthBytes, cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadRawAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes));

        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return data;
    }
}
