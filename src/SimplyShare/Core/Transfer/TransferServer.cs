using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimplyShare.Core.Crypto;
using SimplyShare.Models;

namespace SimplyShare.Core.Transfer;

/// <summary>
/// TCP 기반 전송 서버 (수신 대기)
/// </summary>
public sealed class TransferServer : IDisposable
{
    private readonly Services.ISettingsService _settingsService;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>전송 요청 수신 이벤트</summary>
    public event Func<TransferRequest, Task<bool>>? TransferRequested;

    /// <summary>진행률 변경 이벤트</summary>
    public event Action<TransferProgress>? ProgressChanged;

    /// <summary>텍스트 수신 이벤트</summary>
    public event Action<string, string, string>? TextReceived; // (senderNickname, senderDeviceId, text)

    /// <summary>파일 수신 완료 이벤트</summary>
    public event Action<string, string, IReadOnlyList<string>>? FilesReceived; // (senderNickname, senderDeviceId, filePaths)

    /// <summary>TCP 연결로 인한 피어 발견 이벤트 (UDP 발견 실패 대비)</summary>
    public event Action<DeviceInfo>? PeerConnected;

    /// <summary>Ping으로 지속 채팅 연결 수립됨 이벤트</summary>
    public event Action<ChatConnection>? ChatEstablished;

    public TransferServer(Services.ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var port = _settingsService.Settings.TransferPort;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _ = AcceptClientsAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        // Ping → ChatConnection으로 소유권 이전 시 자동 해제 방지용 플래그
        var ownedByChat = false;
        var crypto = new CryptoService();

        try
        {
            client.NoDelay = true;
            client.SendBufferSize = NetworkDefaults.SocketBufferSize;
            client.ReceiveBufferSize = NetworkDefaults.SocketBufferSize;

            var stream = client.GetStream();

            // 1. 키 교환
            await PerformKeyExchangeAsync(stream, crypto, cancellationToken);

            // 2. 전송 요청 수신 (암호화된)
            var requestData = await ReadEncryptedMessageAsync(stream, crypto, cancellationToken);
            var request = JsonSerializer.Deserialize(requestData, AppJsonContext.Default.TransferRequest);
            if (request is null)
                return;

            // ★ 모든 TCP 연결에서 피어 정보 알림 (UDP 단방향 실패 대비)
            var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            var peerDevice = new DeviceInfo
            {
                DeviceId = request.SenderDeviceId,
                Nickname = request.SenderNickname,
                IpAddress = remoteIp,
                Port = request.SenderPort > 0 ? request.SenderPort : _settingsService.Settings.TransferPort,
                LastSeen = DateTime.UtcNow,
                IsOnline = true
            };
            PeerConnected?.Invoke(peerDevice);

            // 3-A. Ping → 지속 채팅 연결로 전환 (소유권을 ChatConnection에 이전)
            if (request.Type is TransferType.Ping)
            {
                await SendEncryptedMessageAsync(stream, crypto,
                    JsonSerializer.Serialize(new TransferMessage { Type = TransferMessageType.TransferAccepted },
                        AppJsonContext.Default.TransferMessage),
                    cancellationToken);

                var chatConn = new ChatConnection(client, stream, crypto,
                    request.SenderDeviceId, request.SenderNickname);
                ownedByChat = true; // 이 시점부터 ChatConnection이 리소스를 소유
                ChatEstablished?.Invoke(chatConn);
                // ★ StartReceiveLoop은 ChatEstablished 수신자가 호출

                AppLogger.Log("TransferServer", $"★ ChatConnection 수립 ← {request.SenderNickname}({remoteIp})");
                return;
            }

            // 3-B. 타입별 처리
            if (request.Type is TransferType.AppUpdate)
            {
                // 앱 업데이트 요청 → 자동 수락, 자신의 EXE를 전송
                await SendEncryptedMessageAsync(stream, crypto,
                    JsonSerializer.Serialize(new TransferMessage { Type = TransferMessageType.TransferAccepted },
                        AppJsonContext.Default.TransferMessage),
                    cancellationToken);

                var exePath = Environment.ProcessPath;
                if (exePath is not null && System.IO.File.Exists(exePath))
                {
                    var exeSize = new System.IO.FileInfo(exePath).Length;

                    // 파일 크기를 먼저 전송 (암호화된 메시지로)
                    await SendEncryptedMessageAsync(stream, crypto, exeSize.ToString(), cancellationToken);

                    // EXE 파일을 청크 단위로 전송
                    await using var fileStream = System.IO.File.OpenRead(exePath);
                    var buffer = new byte[NetworkDefaults.FileChunkSize];
                    int bytesRead;
                    while ((bytesRead = await fileStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        var chunk = buffer.AsSpan(0, bytesRead);
                        var encrypted = crypto.Encrypt(chunk);
                        await SendRawAsync(stream, encrypted, cancellationToken);
                    }
                }
                return;
            }

            // 4. 수락/거부 확인
            var accepted = TransferRequested is not null && await TransferRequested.Invoke(request);
            var responseType = accepted
                ? TransferMessageType.TransferAccepted
                : TransferMessageType.TransferRejected;

            await SendEncryptedMessageAsync(stream, crypto,
                JsonSerializer.Serialize(new TransferMessage { Type = responseType },
                    AppJsonContext.Default.TransferMessage),
                cancellationToken);

            if (!accepted)
                return;

            // 4. 데이터 수신
            var transferId = Guid.NewGuid().ToString("N")[..8];

            if (request.Type is TransferType.Text)
            {
                TextReceived?.Invoke(request.SenderNickname, request.SenderDeviceId, request.TextContent ?? string.Empty);
            }
            else
            {
                var receivedFiles = await ReceiveFilesAsync(stream, crypto, request, transferId, cancellationToken);
                FilesReceived?.Invoke(request.SenderNickname, request.SenderDeviceId, receivedFiles);
            }

            // 5. 완료 확인
            var progress = new TransferProgress
            {
                TransferId = transferId,
                TotalBytes = request.TotalSize,
                BytesTransferred = request.TotalSize,
                Status = TransferStatus.Completed,
                PeerNickname = request.SenderNickname,
                Direction = TransferDirection.Receive
            };
            ProgressChanged?.Invoke(progress);
        }
        catch (Exception)
        {
            // 에러 시 조용히 정리
        }
        finally
        {
            // ChatConnection으로 소유권이 이전되지 않은 경우에만 리소스 해제
            if (!ownedByChat)
            {
                crypto.Dispose();
                client.Dispose();
            }
        }
    }

    private async Task PerformKeyExchangeAsync(NetworkStream stream, CryptoService crypto, CancellationToken cancellationToken)
    {
        // 내 공개 키 전송
        var myPublicKey = crypto.ExportPublicKey();
        await SendRawAsync(stream, myPublicKey, cancellationToken);

        // 상대방 공개 키 수신
        var peerPublicKey = await ReadRawAsync(stream, cancellationToken);
        crypto.DeriveSessionKey(peerPublicKey);
    }

    private async Task<List<string>> ReceiveFilesAsync(NetworkStream stream, CryptoService crypto,
        TransferRequest request, string transferId, CancellationToken cancellationToken)
    {
        var downloadPath = _settingsService.Settings.DownloadPath;
        Directory.CreateDirectory(downloadPath);

        long totalReceived = 0;
        var receivedPaths = new List<string>();

        foreach (var fileInfo in request.Files)
        {
            var targetPath = Path.Combine(downloadPath, fileInfo.RelativePath);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (targetDir is not null)
                Directory.CreateDirectory(targetDir);

            if (fileInfo.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            // 동일 파일명 처리
            targetPath = GetUniqueFilePath(targetPath);
            receivedPaths.Add(targetPath);

            await using var fileStream = File.Create(targetPath);
            long fileReceived = 0;

            while (fileReceived < fileInfo.Size)
            {
                var chunkData = await ReadEncryptedChunkAsync(stream, crypto, cancellationToken);
                await fileStream.WriteAsync(chunkData, cancellationToken);
                fileReceived += chunkData.Length;
                totalReceived += chunkData.Length;

                var progress = new TransferProgress
                {
                    TransferId = transferId,
                    CurrentFileName = fileInfo.RelativePath,
                    BytesTransferred = totalReceived,
                    TotalBytes = request.TotalSize,
                    Status = TransferStatus.InProgress,
                    PeerNickname = request.SenderNickname,
                    Direction = TransferDirection.Receive
                };
                ProgressChanged?.Invoke(progress);
            }
        }

        return receivedPaths;
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var counter = 1;

        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name}({counter}){ext}");
            counter++;
        } while (File.Exists(newPath));

        return newPath;
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

    private static async Task SendEncryptedMessageAsync(NetworkStream stream, CryptoService crypto,
        string json, CancellationToken cancellationToken)
    {
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encrypted = crypto.Encrypt(plainBytes);
        await SendRawAsync(stream, encrypted, cancellationToken);
    }

    private static async Task<string> ReadEncryptedMessageAsync(NetworkStream stream, CryptoService crypto,
        CancellationToken cancellationToken)
    {
        var encrypted = await ReadRawAsync(stream, cancellationToken);
        var decrypted = crypto.Decrypt(encrypted);
        return Encoding.UTF8.GetString(decrypted);
    }

    private static async Task<byte[]> ReadEncryptedChunkAsync(NetworkStream stream, CryptoService crypto,
        CancellationToken cancellationToken)
    {
        var encrypted = await ReadRawAsync(stream, cancellationToken);
        return crypto.Decrypt(encrypted);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Stop();
    }
}
