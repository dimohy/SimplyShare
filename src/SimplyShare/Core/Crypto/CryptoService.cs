using System.Buffers;
using System.Security.Cryptography;

namespace SimplyShare.Core.Crypto;

/// <summary>
/// ECDH 키 교환 + AES-256-GCM 암호화 서비스
/// </summary>
public sealed class CryptoService : Services.ICryptoService, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // AES-256

    private ECDiffieHellman? _ecdh;
    private byte[]? _sessionKey;

    public CryptoService()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>
    /// ECDH 공개 키를 내보내기 (상대방에게 전송용)
    /// </summary>
    public byte[] ExportPublicKey()
    {
        if (_ecdh is null)
            throw new InvalidOperationException("CryptoService has been reset.");

        return _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// 상대방 공개 키로 세션 키 유도 (HKDF)
    /// </summary>
    public void DeriveSessionKey(byte[] peerPublicKey)
    {
        if (_ecdh is null)
            throw new InvalidOperationException("CryptoService has been reset.");

        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(peerPublicKey, out _);

        // 공유 비밀 생성
        var sharedSecret = _ecdh.DeriveRawSecretAgreement(peerKey.PublicKey);

        // HKDF로 AES 키 유도
        _sessionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            KeySize,
            info: "SimplyShare-AES256"u8.ToArray());

        // 공유 비밀 즉시 제거
        CryptographicOperations.ZeroMemory(sharedSecret);
    }

    /// <summary>
    /// AES-256-GCM 암호화
    /// 출력 형식: [12B Nonce][16B Tag][N bytes 암호문]
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (_sessionKey is null)
            throw new InvalidOperationException("Session key not derived. Call DeriveSessionKey first.");

        var ciphertext = new byte[plaintext.Length];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_sessionKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // [Nonce][Tag][Ciphertext]
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(result.AsSpan());
        tag.CopyTo(result.AsSpan(NonceSize));
        ciphertext.CopyTo(result.AsSpan(NonceSize + TagSize));

        return result;
    }

    /// <summary>
    /// 암호화 결과 길이 계산
    /// </summary>
    public int GetEncryptedLength(int plaintextLength)
        => NonceSize + TagSize + plaintextLength;

    /// <summary>
    /// AES-256-GCM 암호화 (호출자가 제공한 버퍼에 직접 기록)
    /// 출력 형식: [12B Nonce][16B Tag][N bytes 암호문]
    /// </summary>
    public void EncryptToBuffer(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        if (_sessionKey is null)
            throw new InvalidOperationException("Session key not derived. Call DeriveSessionKey first.");

        var requiredLength = GetEncryptedLength(plaintext.Length);
        if (destination.Length < requiredLength)
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        var nonce = destination[..NonceSize];
        var tag = destination.Slice(NonceSize, TagSize);
        var ciphertext = destination.Slice(NonceSize + TagSize, plaintext.Length);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_sessionKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
    }

    /// <summary>
    /// AES-256-GCM 복호화
    /// 입력 형식: [12B Nonce][16B Tag][N bytes 암호문]
    /// </summary>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        if (_sessionKey is null)
            throw new InvalidOperationException("Session key not derived. Call DeriveSessionKey first.");

        if (data.Length < NonceSize + TagSize)
            throw new ArgumentException("Data too short to contain nonce and tag.");

        var nonce = data[..NonceSize];
        var tag = data.Slice(NonceSize, TagSize);
        var ciphertext = data[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_sessionKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// 복호화 결과 길이 계산
    /// </summary>
    public int GetDecryptedLength(int encryptedLength)
    {
        if (encryptedLength < NonceSize + TagSize)
            throw new ArgumentException("Data too short to contain nonce and tag.", nameof(encryptedLength));

        return encryptedLength - NonceSize - TagSize;
    }

    /// <summary>
    /// AES-256-GCM 복호화 (호출자가 제공한 버퍼에 직접 기록)
    /// 입력 형식: [12B Nonce][16B Tag][N bytes 암호문]
    /// </summary>
    public int DecryptToBuffer(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (_sessionKey is null)
            throw new InvalidOperationException("Session key not derived. Call DeriveSessionKey first.");

        if (data.Length < NonceSize + TagSize)
            throw new ArgumentException("Data too short to contain nonce and tag.", nameof(data));

        var plaintextLength = data.Length - NonceSize - TagSize;
        if (destination.Length < plaintextLength)
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        var nonce = data[..NonceSize];
        var tag = data.Slice(NonceSize, TagSize);
        var ciphertext = data[(NonceSize + TagSize)..];
        var plaintext = destination[..plaintextLength];

        using var aes = new AesGcm(_sessionKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintextLength;
    }

    /// <summary>
    /// 세션 키 초기화 (연결 종료 시)
    /// </summary>
    public void Reset()
    {
        if (_sessionKey is not null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            _sessionKey = null;
        }

        _ecdh?.Dispose();
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    public void Dispose()
    {
        if (_sessionKey is not null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            _sessionKey = null;
        }

        _ecdh?.Dispose();
        _ecdh = null;
    }
}
