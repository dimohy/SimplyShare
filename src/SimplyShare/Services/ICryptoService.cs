namespace SimplyShare.Services;

/// <summary>
/// 암호화 서비스
/// </summary>
public interface ICryptoService
{
    /// <summary>ECDH 공개 키 생성/내보내기</summary>
    byte[] ExportPublicKey();

    /// <summary>상대방 공개 키로 세션 키 유도</summary>
    void DeriveSessionKey(byte[] peerPublicKey);

    /// <summary>데이터 암호화</summary>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext);

    /// <summary>데이터 복호화</summary>
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext);

    /// <summary>세션 키 초기화 (연결 종료 시)</summary>
    void Reset();
}
