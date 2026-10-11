using Kantar.Testing;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>The guard itself is tested with a spy probe: a wrong target must be refused without a single write.</summary>
public sealed class TestDatabaseGuardTests
{
    private const string RunId = "0123456789abcdef0123456789abcdef";
    private static readonly TestDatabaseManifest Manifest = new(RunId, "127.0.0.1", 51433, "kantar_test_0123456789ab", "container-1");

    private sealed class SpyProbe : ITestDatabaseProbe
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 51433;
        public string Database { get; init; } = "kantar_test_0123456789ab";
        public string? Marker { get; init; } = RunId;
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public Task<string> CurrentDatabaseAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Database);
        }

        public Task<string?> ReadRunMarkerAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Marker);
        }

        /// <summary>What a fixture would do after the guard passes. Counted to prove it never happens on refusal.</summary>
        public void Write() => Writes++;
    }

    private static async Task<SpyProbe> RunAsync(SpyProbe probe, string? optIn = TestDatabaseGuard.OptInValue, TestDatabaseManifest? manifest = null)
    {
        await TestDatabaseGuard.EnsureOwnedAsync(optIn, manifest ?? Manifest, probe, CancellationToken.None);
        probe.Write();
        return probe;
    }

    [Fact]
    public async Task Owned_database_passes_and_only_then_is_written_to()
    {
        var probe = await RunAsync(new SpyProbe());

        Assert.Equal(1, probe.Writes);
        Assert.Equal(2, probe.Reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("Owned")]
    public async Task Missing_opt_in_is_refused_before_the_server_is_even_asked(string? optIn)
    {
        var probe = new SpyProbe();

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => RunAsync(probe, optIn));

        Assert.Equal(0, probe.Reads);
        Assert.Equal(0, probe.Writes);
    }

    [Fact]
    public async Task A_prefix_alone_is_not_ownership()
    {
        // Name looks right, but the marker belongs to another run.
        var probe = new SpyProbe { Marker = "ffffffffffffffffffffffffffffffff" };

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => RunAsync(probe));

        Assert.Equal(0, probe.Writes);
    }

    [Fact]
    public async Task Missing_marker_is_refused()
    {
        var probe = new SpyProbe { Marker = null };

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => RunAsync(probe));

        Assert.Equal(0, probe.Writes);
    }

    [Theory]
    [InlineData("kantar")]
    [InlineData("kulvar")]
    [InlineData("kantar_test_ffffffffffff")]
    [InlineData("master")]
    public async Task Server_reporting_another_database_is_refused(string actualDatabase)
    {
        var probe = new SpyProbe { Database = actualDatabase };

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => RunAsync(probe));

        Assert.Equal(0, probe.Writes);
    }

    [Theory]
    [InlineData("127.0.0.1", 1433)]
    [InlineData("127.0.0.1", 1434)]
    [InlineData("db.example.invalid", 51433)]
    public async Task Another_endpoint_is_refused_without_querying_it(string host, int port)
    {
        var probe = new SpyProbe { Host = host, Port = port };

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => RunAsync(probe));

        Assert.Equal(0, probe.Reads);
        Assert.Equal(0, probe.Writes);
    }

    [Fact]
    public async Task Non_loopback_endpoint_is_refused_even_when_it_matches_the_manifest()
    {
        var manifest = Manifest with { Host = "10.0.0.5" };
        var probe = new SpyProbe { Host = "10.0.0.5" };

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => RunAsync(probe, manifest: manifest));

        Assert.Equal(0, probe.Writes);
    }

    [Theory]
    [InlineData("kantar")]
    [InlineData("kantar_dev")]
    [InlineData("kantar_test_")]
    public async Task Manifest_naming_a_non_test_database_is_refused(string name)
    {
        var probe = new SpyProbe { Database = name };

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => RunAsync(probe, manifest: Manifest with { DatabaseName = name }));

        Assert.Equal(0, probe.Reads);
        Assert.Equal(0, probe.Writes);
    }
}
