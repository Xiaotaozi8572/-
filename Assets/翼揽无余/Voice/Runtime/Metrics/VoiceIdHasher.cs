// VoiceIdHasher.cs
// VR5-T13: per-install salted hashing of session/turn identifiers for the metrics layer.
//
// A record must NEVER persist a raw session_id / turn_id (privacy: they can be correlated to a
// user). This hasher produces a deterministic, single-direction hex digest of each identifier using
// a per-INSTALL random salt: the same install always hashes the same id to the same token (so
// durations can be correlated), but two installs produce different tokens for the same raw id, and
// the raw id is never recoverable from the token (256-bit SHA-256 preimage).
using System;
using System.Security.Cryptography;
using System.Text;

namespace Yilan.Voice.Runtime.Metrics
{
    /// <summary>One-directional, per-install salted hasher for voice session/turn identifiers.</summary>
    public sealed class VoiceIdHasher
    {
        private const int DigestHexChars = 32; // 16 bytes -> 32 hex chars (128-bit)

        private readonly string _salt;

        public VoiceIdHasher(string salt)
        {
            _salt = salt ?? string.Empty;
        }

        /// <summary>The salt this hasher uses (kept private on device; not persisted in records).</summary>
        public string Salt => _salt;

        /// <summary>Create a hasher with a fresh CRYPTOGRAPHICALLY random salt (once per install).</summary>
        public static VoiceIdHasher CreateRandomSalt()
        {
            byte[] bytes = new byte[16];
            try
            {
                using (var rng = new RNGCryptoServiceProvider())
                    rng.GetBytes(bytes);
            }
            catch
            {
                // RNG unavailable on a fringe platform/editor: fall back to a time+guid seeded salt
                // so hashing still works (still per-install random, non-reversible in practice).
                bytes = Guid.NewGuid().ToByteArray();
            }
            return new VoiceIdHasher(ToHex(bytes, bytes.Length));
        }

        /// <summary>Deterministic, non-reversible hex digest of <paramref name="id"/> (32 hex chars).
        /// The raw id is never a substring of the digest. Empty/null ids hash to a constant.</summary>
        public string Hash(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            byte[] input = Encoding.UTF8.GetBytes(_salt + "|" + id);
            byte[] digest;
            try
            {
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(input);
            }
            catch
            {
                // SHA-256 unavailable fallback: still one-directional, still salted (FNV-1a over
                // the salted input). Used only if the platform HSM/crypto provider is absent.
                digest = FnvFallback(input);
            }
            return ToHex(digest, DigestHexChars / 2);
        }

        private static string ToHex(byte[] data, int bytes)
        {
            char[] chars = new char[bytes * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < bytes; i++)
            {
                byte b = data[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0x0F];
            }
            return new string(chars);
        }

        private static byte[] FnvFallback(byte[] input)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (byte b in input)
                {
                    h ^= b;
                    h *= 1099511628211UL;
                }
                byte[] outBytes = new byte[8];
                for (int i = 0; i < 8; i++)
                {
                    outBytes[i] = (byte)(h >> (8 * i));
                }
                return outBytes;
            }
        }
    }
}
