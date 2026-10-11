using System.Globalization;
using System.Security.Cryptography;
using Kantar.Application.Abstractions;

namespace Kantar.Infrastructure.Security;

public sealed class PasswordHashingOptions
{
    /// <summary>PBKDF2-HMAC-SHA256 iteration count for new hashes. Lower values are for automated tests only.</summary>
    public int Iterations { get; set; } = 600_000;
}

/// <summary>
/// PBKDF2-HMAC-SHA256 with a per-password salt. The stored form carries its own iteration count
/// ("v1.{iterations}.{salt}.{hash}"), so the count can be raised without invalidating old hashes.
/// </summary>
public sealed class Pbkdf2PasswordHasher(PasswordHashingOptions options) : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MinIterations = 1_000;
    private const int MaxIterations = 5_000_000;

    public string Hash(string password)
    {
        var iterations = Math.Clamp(options.Iterations, MinIterations, MaxIterations);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return string.Create(CultureInfo.InvariantCulture, $"v1.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}");
    }

    public bool Verify(string hash, string password)
    {
        var parts = hash.Split('.');
        if (parts.Length != 4 || parts[0] != "v1"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < MinIterations or > MaxIterations)
        {
            return false;
        }

        Span<byte> salt = stackalloc byte[SaltSize];
        Span<byte> expected = stackalloc byte[HashSize];
        if (!Convert.TryFromBase64String(parts[2], salt, out var saltLength) || saltLength != SaltSize
            || !Convert.TryFromBase64String(parts[3], expected, out var hashLength) || hashLength != HashSize)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[HashSize];
        Rfc2898DeriveBytes.Pbkdf2(password, salt, actual, iterations, HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
