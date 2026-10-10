using System;
using System.Security.Cryptography;
using System.Text;

namespace BannerlordMP.Core.Security
{
    /// <summary>
    /// Passwords never cross the network. Each password is stretched into a key with PBKDF2 and a per-password
    /// salt; the server stores only that key. To log in, the client proves it knows the key by answering a
    /// one-time challenge (HMAC of a random nonce), so a captured login cannot be replayed.
    /// </summary>
    public static class PasswordProof
    {
        public const int SaltSize = 16;
        public const int NonceSize = 16;
        public const int KeySize = 32;
        public const int Iterations = 100_000;

        public static byte[] NewSalt() => RandomBytes(SaltSize);

        public static byte[] NewNonce() => RandomBytes(NonceSize);

        public static byte[] DeriveKey(string password, byte[] salt)
        {
            if (salt == null || salt.Length == 0)
                throw new ArgumentException("A salt is required.", nameof(salt));
            using (var pbkdf2 = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password ?? string.Empty), salt, Iterations))
                return pbkdf2.GetBytes(KeySize);
        }

        public static byte[] Prove(byte[] key, byte[] nonce)
        {
            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(nonce);
        }

        public static byte[] Prove(string password, byte[] salt, byte[] nonce) => Prove(DeriveKey(password, salt), nonce);

        public static bool Verify(byte[] key, byte[] nonce, byte[] proof)
        {
            if (key == null || nonce == null || proof == null)
                return false;
            return FixedTimeEquals(Prove(key, nonce), proof);
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string NewToken() => ToHex(RandomBytes(16));

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }
    }
}
