using System.Security.Cryptography;

namespace TokenVault.Services;

/// <summary>磁盘上的加密保险库文件格式（JSON）。</summary>
public class VaultFile
{
    public int Version { get; set; } = 1;
    public KdfInfo Kdf { get; set; } = new();
    public CipherInfo Cipher { get; set; } = new();
    /// <summary>Base64(AES-GCM(payload))</summary>
    public string Payload { get; set; } = "";
    public long SavedAtUnix { get; set; }

    public class KdfInfo
    {
        public string Algorithm { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; } = CryptoService.DefaultIterations;
        public string Salt { get; set; } = "";
    }

    public class CipherInfo
    {
        public string Algorithm { get; set; } = "AES-256-GCM";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
    }
}

public static class CryptoService
{
    public const int DefaultIterations = 200_000;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] GenerateSalt(int size = 16) => RandomNumberGenerator.GetBytes(size);

    public static byte[] DeriveKey(string password, byte[] salt, int iterations = DefaultIterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>用口令加密明文，返回可序列化的文件对象。</summary>
    public static VaultFile Encrypt(string password, string plaintext, int iterations = DefaultIterations)
    {
        byte[] salt = GenerateSalt();
        byte[] key = DeriveKey(password, salt, iterations);
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] plain = System.Text.Encoding.UTF8.GetBytes(plaintext);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plain, cipher, tag);
            return new VaultFile
            {
                Kdf = new VaultFile.KdfInfo { Iterations = iterations, Salt = Convert.ToBase64String(salt) },
                Cipher = new VaultFile.CipherInfo
                {
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag),
                },
                Payload = Convert.ToBase64String(cipher),
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>口令错误或文件损坏都会抛出 <see cref="CryptographicException"/>。</summary>
    public static string Decrypt(VaultFile file, string password)
    {
        byte[] salt = Convert.FromBase64String(file.Kdf.Salt);
        byte[] key = DeriveKey(password, salt, file.Kdf.Iterations);
        try
        {
            byte[] nonce = Convert.FromBase64String(file.Cipher.Nonce);
            byte[] tag = Convert.FromBase64String(file.Cipher.Tag);
            byte[] cipher = Convert.FromBase64String(file.Payload);
            byte[] plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
