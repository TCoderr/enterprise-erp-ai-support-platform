using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kantar.Application.Assistance;

namespace Kantar.Infrastructure.Assistance;

public sealed class AiOptions
{
    /// <summary>Demo (default), LocalModel or Disabled. Server configuration only; never taken from a request.</summary>
    public string Provider { get; set; } = "Demo";

    public LocalModelOptions LocalModel { get; set; } = new();
}

public sealed class LocalModelOptions
{
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxOutputTokens { get; set; } = 600;

    /// <summary>Optional file holding a bearer token for a local server that asks for one. Never committed.</summary>
    public string? ApiKeyFile { get; set; }

    /// <summary>Private-network host names or addresses that are allowed besides loopback. Empty by default.</summary>
    public string[] AllowedPrivateHosts { get; set; } = [];
}

/// <summary>
/// Where the local-model adapter may connect: loopback always, and private-range addresses only for
/// hosts the operator listed explicitly. Public addresses are never allowed, whatever is configured.
/// </summary>
public static class LocalEndpointPolicy
{
    public static Uri Validate(string? baseUrl, IReadOnlyCollection<string> allowedPrivateHosts)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Trim('/').Length != 0)
        {
            throw new InvalidOperationException("Ai:LocalModel:BaseUrl must be an http(s) origin without path, query, fragment or credentials.");
        }

        if (!IsLoopbackHost(uri.Host) && !allowedPrivateHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ai:LocalModel:BaseUrl must be a loopback address or a host listed in Ai:LocalModel:AllowedPrivateHosts.");
        }

        return uri;
    }

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    /// <summary>Checked on the address actually being connected to, after DNS resolution.</summary>
    public static bool IsAllowedAddress(IPAddress address, bool hostExplicitlyAllowed)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return hostExplicitlyAllowed && IsPrivate(address);
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        // fc00::/7 unique local addresses.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}

/// <summary>
/// Adapter for a local server that exposes the OpenAI-style <c>POST /v1/chat/completions</c> route
/// (Ollama documents this route and the <c>choices[0].message.content</c> response path; LM Studio
/// documents the route). Only the common subset is used: model, messages, temperature, max_tokens,
/// stream=false. <c>response_format</c> is not relied on because support differs between servers;
/// the JSON object is parsed out of the returned text and anything else is treated as malformed.
/// </summary>
public sealed class LocalModelAnswerGenerator : IAnswerGenerator, IDisposable
{
    public const string Kind = "local-model";
    private const int MaxResponseBytes = 256 * 1024;
    private readonly HttpClient _client;
    private readonly LocalModelOptions _options;

    public LocalModelAnswerGenerator(AiOptions options) : this(options, handler: null)
    {
    }

    /// <summary>Test seam: a handler replaces the socket-level transport, the endpoint policy still applies to the URL.</summary>
    public LocalModelAnswerGenerator(AiOptions options, HttpMessageHandler? handler)
    {
        _options = options.LocalModel;
        var baseUri = LocalEndpointPolicy.Validate(_options.BaseUrl, _options.AllowedPrivateHosts);
        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new InvalidOperationException("Ai:LocalModel:Model is not configured.");
        }

        var explicitlyAllowed = !LocalEndpointPolicy.IsLoopbackHost(baseUri.Host);
        _client = new HttpClient(handler ?? CreateHandler(explicitlyAllowed), disposeHandler: true)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 300)),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKeyFile) && File.Exists(_options.ApiKeyFile))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(_options.ApiKeyFile).Trim());
        }

        Info = new ProviderInfoDto(Kind, _options.Model, IsDemo: false);
    }

    public ProviderInfoDto Info { get; }

    private static SocketsHttpHandler CreateHandler(bool hostExplicitlyAllowed) => new()
    {
        // No redirects and no proxy: the request goes to the configured origin or nowhere.
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            // Resolve here and check the address that is really used, so a name that starts resolving
            // to a public address cannot move the connection outside the allowed range.
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            foreach (var address in addresses)
            {
                if (!LocalEndpointPolicy.IsAllowedAddress(address, hostExplicitlyAllowed))
                {
                    continue;
                }

                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            throw new HttpRequestException("The model endpoint did not resolve to an allowed address.");
        },
    };

    public async Task<GeneratedAnswer> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken)
    {
        var prompt = PromptBuilder.Build(request);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User },
            },
            temperature = 0,
            max_tokens = Math.Clamp(_options.MaxOutputTokens, 64, 4096),
            stream = false,
        });

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _client.PostAsync("/v1/chat/completions", content, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Unreachable, refused, timed out. The caller's own cancellation is not swallowed.
            return GeneratedAnswer.Unavailable;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 429, 5xx, unknown model (404), redirects (3xx are not followed) — all "no answer now".
                return GeneratedAnswer.Unavailable;
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                return GeneratedAnswer.Malformed;
            }

            return Parse(body);
        }
    }

    /// <summary>Extracts the assistant text and the single JSON object it must contain.</summary>
    public static GeneratedAnswer Parse(string responseBody)
    {
        string? text;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            // TryGetProperty throws on anything that is not an object, so every level is checked first:
            // a body such as "null", a bare string or an array is malformed output, not a server error.
            text = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return GeneratedAnswer.Malformed;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return GeneratedAnswer.Malformed;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return GeneratedAnswer.Malformed;
        }

        try
        {
            using var answer = JsonDocument.Parse(text.AsMemory(start, end - start + 1));
            var root = answer.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return GeneratedAnswer.Malformed;
            }

            var limitations = root.TryGetProperty("limitations", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (root.TryGetProperty("insufficient", out var insufficient) && insufficient.ValueKind == JsonValueKind.True)
            {
                return GeneratedAnswer.Insufficient(limitations);
            }

            if (!root.TryGetProperty("answer", out var answerText) || answerText.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
            {
                return GeneratedAnswer.Malformed;
            }

            var cited = new List<string>();
            foreach (var item in citations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || cited.Count >= 16)
                {
                    return GeneratedAnswer.Malformed;
                }

                cited.Add(item.GetString()!.Trim().Trim('[', ']'));
            }

            return new GeneratedAnswer(GenerationOutcome.Answered, answerText.GetString(), cited, limitations);
        }
        catch (JsonException)
        {
            return GeneratedAnswer.Malformed;
        }
    }

    public void Dispose() => _client.Dispose();
}
