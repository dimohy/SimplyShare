using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimplyShare.Core.Crypto;
using SimplyShare.Models;

namespace SimplyShare.Core.Transfer;

/// <summary>
/// 1:1 지속 TCP 채팅 연결 — 양방향 암호화 텍스트 통신.
/// 한 번 수립되면 양쪽 모두 동일한 TCP 스트림으로 메시지를 주고받는다.
/// </summary>
public sealed class ChatConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CryptoService _crypto;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>상대방 장치 ID</summary>
    public string PeerDeviceId { get; }

    /// <summary>상대방 닉네임</summary>
    public string PeerNickname { get; }

    /// <summary>연결 상태</summary>
    public bool IsConnected => !_disposed && _client.Connected && !_cts.IsCancellationRequested;

    /// <summary>텍스트 수신 이벤트 (senderNickname, senderDeviceId, text)</summary>
    public event Action<string, string, string>? TextReceived;

    /// <summary>클립보드 텍스트 수신 이벤트 (senderNickname, senderDeviceId, text)</summary>
    public event Action<string, string, string>? ClipboardTextReceived;

    /// <summary>상대방이 채팅창 종료를 요청함</summary>
    public event Action? ChatCloseRequested;

    /// <summary>내 채팅창 종료 요청에 대한 응답 (true=수락, false=거부)</summary>
    public event Action<bool>? ChatCloseResponded;

    /// <summary>원격 입력 이벤트 수신</summary>
    public event Action<InputEvent>? InputEventReceived;

    /// <summary>원격 제어 즉시 종료 신호 수신</summary>
    public event Action? RemoteControlStopReceived;

    /// <summary>채팅 설정 수신</summary>
    public event Action<ChatConfig>? ChatConfigReceived;

    /// <summary>공유 기능 동의 상태 수신</summary>
    public event Action<SharePreferences>? SharePreferencesReceived;

    /// <summary>연결 끊김 이벤트</summary>
    public event Action? Disconnected;

    public ChatConnection(
        TcpClient client,
        NetworkStream stream,
        CryptoService crypto,
        string peerDeviceId,
        string peerNickname)
    {
        _client = client;
        _stream = stream;
        _crypto = crypto;
        PeerDeviceId = peerDeviceId;
        PeerNickname = peerNickname;
    }

    /// <summary>수신 루프 시작 (백그라운드)</summary>
    public void StartReceiveLoop()
    {
        _ = ReceiveLoopAsync(_cts.Token);
    }

    /// <summary>암호화된 텍스트 전송</summary>
    public async Task SendTextAsync(
        string text,
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.Text,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TextContent = text,
            TotalSize = Encoding.UTF8.GetByteCount(text)
        };

        await SendRequestAsync(request, cancellationToken);
    }

    public Task SendClipboardTextAsync(
        string clipboardText,
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.ClipboardText,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TextContent = clipboardText,
            TotalSize = Encoding.UTF8.GetByteCount(clipboardText)
        };

        return SendRequestAsync(request, cancellationToken);
    }

    public Task SendInputEventAsync(
        InputEvent inputEvent,
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var jsonPayload = JsonSerializer.Serialize(inputEvent, AppJsonContext.Default.InputEvent);

        var request = new TransferRequest
        {
            Type = TransferType.InputEvent,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TextContent = jsonPayload,
            TotalSize = Encoding.UTF8.GetByteCount(jsonPayload)
        };

        return SendRequestAsync(request, cancellationToken);
    }

    public Task SendChatConfigAsync(
        ChatConfig config,
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var jsonPayload = JsonSerializer.Serialize(config, AppJsonContext.Default.ChatConfig);

        var request = new TransferRequest
        {
            Type = TransferType.ChatConfig,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TextContent = jsonPayload,
            TotalSize = Encoding.UTF8.GetByteCount(jsonPayload)
        };

        return SendRequestAsync(request, cancellationToken);
    }

    public Task SendChatCloseRequestAsync(
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.ChatCloseRequest,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TotalSize = 0
        };

        return SendRequestAsync(request, cancellationToken);
    }

    public Task SendChatCloseResponseAsync(
        bool accepted,
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = accepted ? TransferType.ChatCloseAccept : TransferType.ChatCloseReject,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TotalSize = 0
        };

        return SendRequestAsync(request, cancellationToken);
    }

    public Task SendRemoteControlStopAsync(
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var request = new TransferRequest
        {
            Type = TransferType.RemoteControlStop,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TotalSize = 0
        };

        return SendRequestAsync(request, cancellationToken);
    }

    public Task SendSharePreferencesAsync(
        SharePreferences preferences,
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        var jsonPayload = JsonSerializer.Serialize(preferences, AppJsonContext.Default.SharePreferences);

        var request = new TransferRequest
        {
            Type = TransferType.SharePreferences,
            SenderNickname = senderNickname,
            SenderDeviceId = senderDeviceId,
            SenderPort = senderPort,
            TextContent = jsonPayload,
            TotalSize = Encoding.UTF8.GetByteCount(jsonPayload)
        };

        return SendRequestAsync(request, cancellationToken);
    }

    private async Task SendRequestAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, AppJsonContext.Default.TransferRequest);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encrypted = _crypto.Encrypt(plainBytes);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await SendRawAsync(encrypted, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var encrypted = await ReadRawAsync(cancellationToken);
                var decrypted = _crypto.Decrypt(encrypted);
                var json = Encoding.UTF8.GetString(decrypted);
                var request = JsonSerializer.Deserialize(json, AppJsonContext.Default.TransferRequest);

                if (request is null)
                    continue;

                switch (request.Type)
                {
                    case TransferType.Text when request.TextContent is not null:
                        TextReceived?.Invoke(request.SenderNickname, request.SenderDeviceId, request.TextContent);
                        break;
                    case TransferType.ClipboardText when request.TextContent is not null:
                        ClipboardTextReceived?.Invoke(request.SenderNickname, request.SenderDeviceId, request.TextContent);
                        break;
                    case TransferType.ChatCloseRequest:
                        ChatCloseRequested?.Invoke();
                        break;
                    case TransferType.ChatCloseAccept:
                        ChatCloseResponded?.Invoke(true);
                        break;
                    case TransferType.ChatCloseReject:
                        ChatCloseResponded?.Invoke(false);
                        break;
                    case TransferType.RemoteControlStop:
                        RemoteControlStopReceived?.Invoke();
                        break;
                    case TransferType.InputEvent when request.TextContent is not null:
                        {
                            var inputEvent = JsonSerializer.Deserialize(request.TextContent, AppJsonContext.Default.InputEvent);
                            if (inputEvent is not null)
                                InputEventReceived?.Invoke(inputEvent);
                            break;
                        }
                    case TransferType.ChatConfig when request.TextContent is not null:
                        {
                            var config = JsonSerializer.Deserialize(request.TextContent, AppJsonContext.Default.ChatConfig);
                            if (config is not null)
                                ChatConfigReceived?.Invoke(config);
                            break;
                        }
                    case TransferType.SharePreferences when request.TextContent is not null:
                        {
                            var preferences = JsonSerializer.Deserialize(request.TextContent, AppJsonContext.Default.SharePreferences);
                            if (preferences is not null)
                                SharePreferencesReceived?.Invoke(preferences);
                            break;
                        }
                    default:
                        // 알 수 없는 타입은 무시
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatConnection", $"수신 루프 예외 ({PeerNickname}): {ex.Message}");
        }
        finally
        {
            AppLogger.Log("ChatConnection", $"연결 종료 — {PeerNickname}({PeerDeviceId})");
            Disconnected?.Invoke();
        }
    }

    // --- 저수준 프레임 읽기/쓰기 (4B 길이 프리픽스) ---

    private async Task SendRawAsync(byte[] data, CancellationToken cancellationToken)
    {
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
        await _stream.WriteAsync(lengthBytes, cancellationToken);
        await _stream.WriteAsync(data, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task<byte[]> ReadRawAsync(CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await _stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes));

        var data = new byte[length];
        await _stream.ReadExactlyAsync(data, cancellationToken);
        return data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
        _crypto.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }
}
