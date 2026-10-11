using System.Net;
using Kantar.Infrastructure.Assistance;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>
/// Where the local-model adapter may connect. Loopback always; a private address only when the
/// operator listed that host; a public address never, whatever is configured.
/// </summary>
public sealed class LocalEndpointPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434", "127.0.0.1", 11434)]
    [InlineData("http://127.0.0.1:11434/", "127.0.0.1", 11434)]
    [InlineData("http://localhost:1234", "localhost", 1234)]
    [InlineData("HTTP://LOCALHOST:1234", "localhost", 1234)]
    [InlineData("http://[::1]:8080", "[::1]", 8080)]
    [InlineData("https://localhost:8443", "localhost", 8443)]
    [InlineData("http://127.0.0.2", "127.0.0.2", 80)]
    public void Loopback_origins_are_accepted_without_any_allow_list(string baseUrl, string host, int port)
    {
        var uri = LocalEndpointPolicy.Validate(baseUrl, []);

        Assert.Equal(host, uri.Host);
        Assert.Equal(port, uri.Port);
    }

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("http://example.com")]
    [InlineData("http://example.com:11434")]
    [InlineData("http://localhost.example.com")]
    [InlineData("http://127.0.0.1.example.com")]
    [InlineData("http://8.8.8.8:11434")]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://[2001:4860:4860::8888]")]
    public void Public_and_non_loopback_hosts_are_rejected(string baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate(baseUrl, []));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11434/?model=x")]
    [InlineData("http://127.0.0.1:11434?x=1")]
    [InlineData("http://127.0.0.1:11434/#parca")]
    [InlineData("http://kullanici:parola@127.0.0.1:11434")]
    [InlineData("http://kullanici@localhost:1234")]
    public void Urls_with_path_query_fragment_or_credentials_are_rejected(string baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate(baseUrl, []));
    }

    [Theory]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("ws://127.0.0.1:11434")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://localhost")]
    [InlineData("javascript:alert(1)")]
    [InlineData("localhost:1234")]
    [InlineData("/v1/chat/completions")]
    [InlineData("bir adres değil")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Non_http_schemes_and_non_urls_are_rejected(string? baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate(baseUrl, []));
    }

    [Theory]
    [InlineData("http://192.168.1.20:11434")]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("http://172.16.4.4:1234")]
    [InlineData("http://model-kutusu:8080")]
    public void A_private_host_is_rejected_unless_it_is_listed(string baseUrl)
    {
        var host = new Uri(baseUrl).Host;

        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate(baseUrl, []));
        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate(baseUrl, ["192.168.99.99", "baska-kutu", host + ".example"]));

        Assert.Equal(host, LocalEndpointPolicy.Validate(baseUrl, [host]).Host);
        Assert.Equal(host, LocalEndpointPolicy.Validate(baseUrl, ["baska-kutu", host.ToUpperInvariant()]).Host);
    }

    [Fact]
    public void Listing_a_host_does_not_relax_the_url_shape_rules()
    {
        string[] allowed = ["192.168.1.20"];

        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate("http://192.168.1.20:11434/v1", allowed));
        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate("http://kullanici:parola@192.168.1.20:11434", allowed));
        Assert.Throws<InvalidOperationException>(() => LocalEndpointPolicy.Validate("ftp://192.168.1.20", allowed));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.9.10", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost.example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("", false)]
    public void Loopback_host_names_are_recognised(string host, bool expected)
    {
        Assert.Equal(expected, LocalEndpointPolicy.IsLoopbackHost(host));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.254", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("172.15.255.254", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("11.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    public void Private_ranges_are_the_rfc1918_blocks_and_unique_local_ipv6(string address, bool expected)
    {
        Assert.Equal(expected, LocalEndpointPolicy.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void A_resolved_loopback_address_is_allowed_with_or_without_an_explicit_host(string address)
    {
        Assert.True(LocalEndpointPolicy.IsAllowedAddress(IPAddress.Parse(address), hostExplicitlyAllowed: false));
        Assert.True(LocalEndpointPolicy.IsAllowedAddress(IPAddress.Parse(address), hostExplicitlyAllowed: true));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.1.1")]
    [InlineData("192.168.1.20")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("fd00::20")]
    public void A_resolved_private_address_is_allowed_only_for_an_explicitly_listed_host(string address)
    {
        Assert.False(LocalEndpointPolicy.IsAllowedAddress(IPAddress.Parse(address), hostExplicitlyAllowed: false));
        Assert.True(LocalEndpointPolicy.IsAllowedAddress(IPAddress.Parse(address), hostExplicitlyAllowed: true));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("fe80::1")]
    public void A_resolved_public_address_is_refused_even_when_its_host_was_listed(string address)
    {
        // The allow list names hosts; it cannot turn a name that resolves to a public address into a local one.
        Assert.False(LocalEndpointPolicy.IsAllowedAddress(IPAddress.Parse(address), hostExplicitlyAllowed: true));
        Assert.False(LocalEndpointPolicy.IsAllowedAddress(IPAddress.Parse(address), hostExplicitlyAllowed: false));
    }
}
