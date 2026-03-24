using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using System.Buffers;
using SimplyShare.Core.Crypto;
using SimplyShare.Models;

namespace SimplyShare.Core.Transfer;

/// <summary>
/// 1:1 지속 TCP 채팅 연결 — 양방향 암호화 텍스트 통신.
/// 한 번 수립되면 양쪽 모두 동일한 TCP 스트림으로 메시지를 주고받는다.
/// </summary>
public sealed class ChatConnection : IDisposable
{
    private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;

    private enum ChatFrameKind : byte
    {
        TransferRequestJson = 1,
        InputEventBinary = 2,
        InputEventBatchBinary = 3
    }

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
        _ = senderNickname;
        _ = senderDeviceId;
        _ = senderPort;

        var frame = new byte[1 + 1 + 4 + 4 + 4];
        frame[0] = (byte)ChatFrameKind.InputEventBinary;
        frame[1] = (byte)inputEvent.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(2, 4), inputEvent.Arg1);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(6, 4), inputEvent.Arg2);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(10, 4), inputEvent.Arg3);
        return SendFrameAsync(frame, cancellationToken);
    }

    public Task SendInputEventsAsync(
        IReadOnlyList<InputEvent> inputEvents,
        string senderNickname,
        string senderDeviceId,
        int senderPort,
        CancellationToken cancellationToken = default)
    {
        _ = senderNickname;
        _ = senderDeviceId;
        _ = senderPort;

        if (inputEvents.Count <= 0)
            return Task.CompletedTask;

        if (inputEvents.Count == 1)
            return SendInputEventAsync(inputEvents[0], senderNickname, senderDeviceId, senderPort, cancellationToken);

        var count = inputEvents.Count > byte.MaxValue ? byte.MaxValue : inputEvents.Count;
        var frame = new byte[1 + 1 + (count * (1 + 4 + 4 + 4))];
        frame[0] = (byte)ChatFrameKind.InputEventBatchBinary;
        frame[1] = (byte)count;

        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            var inputEvent = inputEvents[i];
            frame[offset] = (byte)inputEvent.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset + 1, 4), inputEvent.Arg1);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset + 5, 4), inputEvent.Arg2);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset + 9, 4), inputEvent.Arg3);
            offset += 13;
        }

        return SendFrameAsync(frame, cancellationToken);
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
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var frame = new byte[1 + jsonBytes.Length];
        frame[0] = (byte)ChatFrameKind.TransferRequestJson;
        Buffer.BlockCopy(jsonBytes, 0, frame, 1, jsonBytes.Length);
        await SendFrameAsync(frame, cancellationToken);
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var encryptedLength = _crypto.GetEncryptedLength(frame.Length);
        var encryptedBuffer = BufferPool.Rent(encryptedLength);

        try
        {
            _crypto.EncryptToBuffer(frame, encryptedBuffer.AsSpan(0, encryptedLength));

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await SendRawAsync(encryptedBuffer.AsMemory(0, encryptedLength), cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            BufferPool.Return(encryptedBuffer);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var encrypted = await ReadRawPooledAsync(cancellationToken);
                var encryptedBuffer = encrypted.Buffer;
                var encryptedLength = encrypted.Length;

                var decryptedLength = _crypto.GetDecryptedLength(encryptedLength);
                var decryptedBuffer = BufferPool.Rent(decryptedLength);

                try
                {
                    _ = _crypto.DecryptToBuffer(
                        encryptedBuffer.AsSpan(0, encryptedLength),
                        decryptedBuffer.AsSpan(0, decryptedLength));
                }
                finally
                {
                    BufferPool.Return(encryptedBuffer);
                }

                var decrypted = decryptedBuffer.AsSpan(0, decryptedLength);

                try
                {
                    if (decrypted.Length < 1)
                        continue;

                    var frameKind = (ChatFrameKind)decrypted[0];
                    switch (frameKind)
                    {
                        case ChatFrameKind.TransferRequestJson:
                            {
                                var requestPayload = Encoding.UTF8.GetString(decrypted[1..]);
                                var request = JsonSerializer.Deserialize(requestPayload, AppJsonContext.Default.TransferRequest);
                                if (request is null)
                                    break;

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
                                        break;
                                }

                                break;
                            }
                        case ChatFrameKind.InputEventBinary:
                            {
                                if (decrypted.Length < 14)
                                    break;

                                var inputEvent = new InputEvent
                                {
                                    Kind = (InputEventKind)decrypted[1],
                                    Arg1 = BinaryPrimitives.ReadInt32LittleEndian(decrypted.Slice(2, 4)),
                                    Arg2 = BinaryPrimitives.ReadInt32LittleEndian(decrypted.Slice(6, 4)),
                                    Arg3 = BinaryPrimitives.ReadInt32LittleEndian(decrypted.Slice(10, 4))
                                };

                                InputEventReceived?.Invoke(inputEvent);
                                break;
                            }
                        case ChatFrameKind.InputEventBatchBinary:
                            {
                                if (decrypted.Length < 2)
                                    break;

                                var count = decrypted[1];
                                var expectedLength = 2 + (count * 13);
                                if (count == 0 || decrypted.Length < expectedLength)
                                    break;

                                var offset = 2;
                                for (var i = 0; i < count; i++)
                                {
                                    var inputEvent = new InputEvent
                                    {
                                        Kind = (InputEventKind)decrypted[offset],
                                        Arg1 = BinaryPrimitives.ReadInt32LittleEndian(decrypted.Slice(offset + 1, 4)),
                                        Arg2 = BinaryPrimitives.ReadInt32LittleEndian(decrypted.Slice(offset + 5, 4)),
                                        Arg3 = BinaryPrimitives.ReadInt32LittleEndian(decrypted.Slice(offset + 9, 4))
                                    };

                                    InputEventReceived?.Invoke(inputEvent);
                                    offset += 13;
                                }

                                break;
                            }
                        default:
                            // 알 수 없는 프레임 타입은 무시
                            break;
                    }
                }
                finally
                {
                    BufferPool.Return(decryptedBuffer);
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

    private async Task SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
        await _stream.WriteAsync(lengthBytes, cancellationToken);
        await _stream.WriteAsync(data, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private readonly record struct PooledBuffer(byte[] Buffer, int Length);

    private async Task<PooledBuffer> ReadRawPooledAsync(CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await _stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes));

        var data = BufferPool.Rent(length);
        await _stream.ReadExactlyAsync(data.AsMemory(0, length), cancellationToken);
        return new PooledBuffer(data, length);
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
