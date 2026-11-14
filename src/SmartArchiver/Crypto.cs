using System.Security.Cryptography;
using System.Text;

namespace SmartArchiver;

public static class Crypto
{
    public const int Pbkdf2Iterations = 100000;

    /// <summary>PBKDF2-HMAC-SHA256, 64 байти: перші 32 – K_enc, останні 32 – K_mac. Один раз на архів.</summary>
    public static (byte[] Enc, byte[] Mac) DeriveKeys(string password, byte[] salt)
    {
        var k = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 64);
        return (k[..32], k[32..]);
    }

    /// <summary>AES-256-CTR: блок лічильника = nonce(12) || counter(u32 BE), лічильник починається з 1.</summary>
    public static byte[] AesCtr(byte[] key, byte[] nonce12, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var ctr = new byte[16]; nonce12.CopyTo(ctr, 0);
        var ks = new byte[16]; var res = new byte[data.Length];
        uint counter = 1;
        for (int off = 0; off < data.Length; off += 16, counter++)
        {
            ctr[12] = (byte)(counter >> 24); ctr[13] = (byte)(counter >> 16);
            ctr[14] = (byte)(counter >> 8); ctr[15] = (byte)counter;
            aes.EncryptEcb(ctr, ks, PaddingMode.None);
            int n = Math.Min(16, data.Length - off);
            for (int i = 0; i < n; i++) res[off + i] = (byte)(data[off + i] ^ ks[i]);
        }
        return res;
    }

    /// <summary>HMAC-SHA256 над заголовком і шифротекстом (encrypt-then-MAC).</summary>
    public static byte[] Mac(byte[] macKey, byte[] header, byte[] ciphertext)
    {
        using var h = new HMACSHA256(macKey);
        h.TransformBlock(header, 0, header.Length, null, 0);
        h.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return h.Hash!;
    }

    public static bool Equal(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
}
