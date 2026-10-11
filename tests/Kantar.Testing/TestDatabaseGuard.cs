namespace Kantar.Testing;

/// <summary>What the fixture believes it created. Compared with what the server actually reports.</summary>
public sealed record TestDatabaseManifest(string RunId, string Host, int Port, string DatabaseName, string ContainerId);

/// <summary>Read-only questions asked of the target server before anything is written to it.</summary>
public interface ITestDatabaseProbe
{
    string Host { get; }
    int Port { get; }
    Task<string> CurrentDatabaseAsync(CancellationToken cancellationToken);
    Task<string?> ReadRunMarkerAsync(CancellationToken cancellationToken);
}

public sealed class TestDatabaseGuardException(string message) : Exception(message);

/// <summary>
/// Destructive test setup (migrations, seeding, truncation, fault injection) is allowed only against a
/// database this test run created. A name prefix alone is not accepted as proof: the run must opt in,
/// the endpoint must be the fixture's own container, the server must report the expected database and
/// the database must carry this run's marker row. Every check is read-only; a failed check means the
/// fixture writes nothing.
/// </summary>
public static class TestDatabaseGuard
{
    public const string OptInValue = "owned";
    public const string DatabasePrefix = "kantar_test_";

    public static async Task EnsureOwnedAsync(string? optIn, TestDatabaseManifest manifest, ITestDatabaseProbe probe, CancellationToken cancellationToken)
    {
        if (!string.Equals(optIn, OptInValue, StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException("Test database use was not opted into for this run.");
        }

        if (manifest.RunId.Length != 32 || !manifest.RunId.All(Uri.IsHexDigit))
        {
            throw new TestDatabaseGuardException("The run id is not a 32-character hex value.");
        }

        if (!string.Equals(manifest.DatabaseName, DatabasePrefix + manifest.RunId[..12], StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException("The database name does not belong to this run.");
        }

        if (!string.Equals(probe.Host, manifest.Host, StringComparison.OrdinalIgnoreCase) || probe.Port != manifest.Port)
        {
            throw new TestDatabaseGuardException("The connection does not point at this run's container endpoint.");
        }

        if (!IsLoopback(probe.Host))
        {
            throw new TestDatabaseGuardException("The test database endpoint is not a loopback address.");
        }

        var actual = await probe.CurrentDatabaseAsync(cancellationToken);
        if (!string.Equals(actual, manifest.DatabaseName, StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException("The server reports a different current database than the manifest.");
        }

        var marker = await probe.ReadRunMarkerAsync(cancellationToken);
        if (!string.Equals(marker, manifest.RunId, StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException("The database does not carry this run's ownership marker.");
        }
    }

    private static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.StartsWith("127.", StringComparison.Ordinal);
}
