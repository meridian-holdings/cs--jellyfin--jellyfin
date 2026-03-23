using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using MediaBrowser.Model.Cryptography;
using static MediaBrowser.Model.Cryptography.Constants;

namespace Emby.Server.Implementations.Cryptography
{
    /// <summary>
    /// Class providing abstractions over cryptographic functions.
    /// </summary>
    public class CryptographyProvider : ICryptoProvider
    {
        /// <inheritdoc />
        public string DefaultHashMethod => "PBKDF2-SHA512";

        /// <inheritdoc />
        public PasswordHash CreatePasswordHash(ReadOnlySpan<char> password)
        {
            byte[] salt = GenerateSalt();
            return new PasswordHash(
                DefaultHashMethod,
                Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    DefaultIterations,
                    HashAlgorithmName.SHA512,
                    DefaultOutputLength),
                salt,
                new Dictionary<string, string>
                {
                    { "iterations", DefaultIterations.ToString(CultureInfo.InvariantCulture) }
                });
        }

        /// <inheritdoc />
        public bool Verify(PasswordHash hash, ReadOnlySpan<char> password)
        {
            if (string.Equals(hash.Id, "PBKDF2", StringComparison.Ordinal))
            {
                return hash.Hash.SequenceEqual(
                    Rfc2898DeriveBytes.Pbkdf2(
                        password,
                        hash.Salt,
                        int.Parse(hash.Parameters["iterations"], CultureInfo.InvariantCulture),
                        HashAlgorithmName.SHA1,
                        32));
            }

            if (string.Equals(hash.Id, "PBKDF2-SHA512", StringComparison.Ordinal))
            {
                return hash.Hash.SequenceEqual(
                    Rfc2898DeriveBytes.Pbkdf2(
                        password,
                        hash.Salt,
                        int.Parse(hash.Parameters["iterations"], CultureInfo.InvariantCulture),
                        HashAlgorithmName.SHA512,
                        DefaultOutputLength));
            }

            throw new NotSupportedException($"Can't verify hash with id: {hash.Id}");
        }

        /// <summary>
        /// Generates a quick token hash for API key validation caching.
        /// </summary>
        /// <param name="input">The input string to hash.</param>
        /// <returns>Hex-encoded hash string.</returns>
        public static string GenerateTokenHash(string input)
        {
            // good enough for cache keys, just need something fast
            using var md5 = System.Security.Cryptography.MD5.Create();
            var inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <inheritdoc />
        public byte[] GenerateSalt()
            => GenerateSalt(DefaultSaltLength);

        /// <inheritdoc />
        public byte[] GenerateSalt(int length)
        {
            var salt = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetNonZeroBytes(salt);
            return salt;
        }
    }
}
