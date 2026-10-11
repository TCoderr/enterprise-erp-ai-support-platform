using Kantar.Infrastructure.Security;
using Xunit;

namespace Kantar.UnitTests;

public sealed class PasswordHasherTests
{
    private const string Password = "Doğru!Parola123";

    // The lowest count the hasher accepts keeps these tests fast; production uses the default.
    private static Pbkdf2PasswordHasher Hasher(int iterations = 1_000) => new(new PasswordHashingOptions { Iterations = iterations });

    [Fact]
    public void The_right_password_verifies_and_a_wrong_one_does_not()
    {
        var hasher = Hasher();
        var hash = hasher.Hash(Password);

        Assert.True(hasher.Verify(hash, Password));
        Assert.False(hasher.Verify(hash, "Yanlış!Parola123"));
        Assert.False(hasher.Verify(hash, Password + " "));
        Assert.False(hasher.Verify(hash, Password.ToUpperInvariant()));
        Assert.False(hasher.Verify(hash, string.Empty));
    }

    [Fact]
    public void The_stored_form_is_versioned_carries_the_iteration_count_and_never_the_password()
    {
        var hash = Hasher(1_234).Hash(Password);

        var parts = hash.Split('.');
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal("1234", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.DoesNotContain(Password, hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_hash_has_its_own_salt()
    {
        var hasher = Hasher();

        var (first, second) = (hasher.Hash(Password), hasher.Hash(Password));

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.Split('.')[2], second.Split('.')[2]);
        Assert.True(hasher.Verify(first, Password));
        Assert.True(hasher.Verify(second, Password));
    }

    [Fact]
    public void A_hash_made_with_another_iteration_count_still_verifies_because_the_count_is_embedded()
    {
        var old = Hasher(1_000).Hash(Password);
        var current = Hasher(3_000);

        Assert.True(current.Verify(old, Password));
        Assert.StartsWith("v1.3000.", current.Hash(Password), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    [InlineData(999)]
    public void A_configured_count_below_the_floor_is_raised_to_the_floor(int configured)
    {
        var hasher = Hasher(configured);

        var hash = hasher.Hash(Password);

        Assert.StartsWith("v1.1000.", hash, StringComparison.Ordinal);
        Assert.True(hasher.Verify(hash, Password));
    }

    [Fact]
    public void A_tampered_hash_does_not_verify()
    {
        var hasher = Hasher();
        var parts = hasher.Hash(Password).Split('.');

        static string Flip(string base64)
        {
            var bytes = Convert.FromBase64String(base64);
            bytes[0] ^= 0x01;
            return Convert.ToBase64String(bytes);
        }

        var tamperedDigest = string.Join('.', parts[0], parts[1], parts[2], Flip(parts[3]));
        var tamperedSalt = string.Join('.', parts[0], parts[1], Flip(parts[2]), parts[3]);
        var otherCount = string.Join('.', parts[0], "1001", parts[2], parts[3]);

        Assert.False(hasher.Verify(tamperedDigest, Password));
        Assert.False(hasher.Verify(tamperedSalt, Password));
        Assert.False(hasher.Verify(otherCount, Password));
    }

    [Fact]
    public void Malformed_stored_values_return_false_instead_of_throwing()
    {
        var hasher = Hasher();
        var parts = hasher.Hash(Password).Split('.');
        var (salt, digest) = (parts[2], parts[3]);

        string[] malformed =
        [
            string.Empty,
            "v1",
            Password,
            $"v1.1000.{salt}",
            $"v1.1000.{salt}.{digest}.extra",
            $"v2.1000.{salt}.{digest}",
            $"V1.1000.{salt}.{digest}",
            $"v1.abc.{salt}.{digest}",
            $"v1..{salt}.{digest}",
            $"v1.-1000.{salt}.{digest}",
            $"v1.+1000.{salt}.{digest}",
            $"v1. 1000.{salt}.{digest}",
            $"v1.999.{salt}.{digest}",
            $"v1.5000001.{salt}.{digest}",
            $"v1.99999999999999999999.{salt}.{digest}",
            $"v1.1000.not base64!.{digest}",
            $"v1.1000.{salt}.not base64!",
            $"v1.1000.{Convert.ToBase64String(new byte[15])}.{digest}",
            $"v1.1000.{Convert.ToBase64String(new byte[17])}.{digest}",
            $"v1.1000.{salt}.{Convert.ToBase64String(new byte[31])}",
            $"v1.1000.{salt}.{Convert.ToBase64String(new byte[33])}",
            $"v1.1000.{salt}.{Convert.ToBase64String(new byte[4096])}",
            $"v1.1000..{digest}",
            $"v1.1000.{salt}.",
        ];

        foreach (var stored in malformed)
        {
            Assert.False(hasher.Verify(stored, Password), $"Accepted malformed hash: {stored[..Math.Min(stored.Length, 40)]}");
        }
    }
}
