using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kantar.Application.Assistance;
using Kantar.Infrastructure.Assistance;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>
/// The local-model adapter against a fake transport. No model is involved: these tests pin what the
/// adapter sends, how it reads what comes back and that every failure becomes "no answer" rather
/// than an exception or an accepted answer. Two tests use a real loopback socket to exercise the
/// production transport (redirects, allowed address); nothing here leaves the machine.
/// </summary>
public sealed class LocalModelAnswerGeneratorTests
{
    private const string SourceCanary = "KANARYA-KAYNAK-41c7";
    private const string QuestionCanary = "KANARYA-SORU-e2a9";

    private static readonly AnswerRequest Request = new(
        $"Transferde rezerve stok taşınır mı? {QuestionCanary}",
        [
            new SourcePassage("S1", "Depolar arası transfer", "Dikkat edilecekler", $"Yalnızca kullanılabilir stok taşınabilir. {SourceCanary}"),
            new SourcePassage("S2", "STK-409", "Anlamı", "İstenen miktar kullanılabilir miktardan fazladır."),
        ]);

    private static AiOptions Options(string baseUrl = "http://127.0.0.1:11434", string? model = "deneme-modeli", int timeoutSeconds = 30) => new()
    {
        Provider = "LocalModel",
        LocalModel = new LocalModelOptions { BaseUrl = baseUrl, Model = model, TimeoutSeconds = timeoutSeconds, MaxOutputTokens = 321 },
    };

    private sealed record SentRequest(HttpMethod Method, Uri? Uri, string Body, string? MediaType, bool HasAuthorization);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(request.Method, request.RequestUri, body, request.Content?.Headers.ContentType?.MediaType, request.Headers.Authorization is not null));
            return await respond(request, cancellationToken);
        }
    }

    private static StubHandler Responding(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) }));

    /// <summary>An OpenAI-style completion whose assistant message is <paramref name="content"/>.</summary>
    private static string Completion(string? content) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test",
        @object = "chat.completion",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
    });

    private static async Task<GeneratedAnswer> GenerateAsync(StubHandler handler)
    {
        using var generator = new LocalModelAnswerGenerator(Options(), handler);
        return await generator.GenerateAsync(Request, CancellationToken.None);
    }

    private const string ValidAnswer = """{"answer": "Hayır; yalnızca kullanılabilir stok taşınır.", "citations": ["S1", "S2"], "insufficient": false, "limitations": "Yalnız iki kaynağa dayanır."}""";

    // ------------------------------------------------------------------ request

    [Fact]
    public async Task Request_goes_to_the_chat_completions_route_with_two_roles_and_the_sources_only_in_the_user_message()
    {
        var handler = Responding(HttpStatusCode.OK, Completion(ValidAnswer));

        var answer = await GenerateAsync(handler);

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(new Uri("http://127.0.0.1:11434/v1/chat/completions"), sent.Uri);
        Assert.Equal("application/json", sent.MediaType);
        Assert.False(sent.HasAuthorization);

        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;

        // Only the common subset: no tools, no functions, no response_format, no streaming.
        Assert.Equal(["max_tokens", "messages", "model", "stream", "temperature"], root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("deneme-modeli", root.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("stream").ValueKind);
        Assert.Equal(0, root.GetProperty("temperature").GetDouble());
        Assert.Equal(321, root.GetProperty("max_tokens").GetInt32());

        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()).ToArray());
        Assert.All(messages, m => Assert.Equal(["content", "role"], m.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()));

        var (system, user) = (messages[0].GetProperty("content").GetString()!, messages[1].GetProperty("content").GetString()!);
        Assert.Equal(PromptBuilder.SystemMessage, system);
        Assert.DoesNotContain("KANARYA", system, StringComparison.Ordinal);
        Assert.Contains(SourceCanary, user, StringComparison.Ordinal);
        Assert.Contains(QuestionCanary, user, StringComparison.Ordinal);
        Assert.Equal(PromptBuilder.Build(Request).User, user);
    }

    [Fact]
    public void Provider_info_names_the_configured_model_and_is_not_a_demo()
    {
        using var generator = new LocalModelAnswerGenerator(Options(), Responding(HttpStatusCode.OK, Completion(ValidAnswer)));

        Assert.Equal(new ProviderInfoDto("local-model", "deneme-modeli", IsDemo: false), generator.Info);
    }

    [Theory]
    [InlineData("https://api.openai.com", "deneme-modeli")]
    [InlineData("http://example.com", "deneme-modeli")]
    [InlineData("http://192.168.1.20:11434", "deneme-modeli")]
    [InlineData("http://127.0.0.1:11434/v1", "deneme-modeli")]
    [InlineData("http://127.0.0.1:11434", null)]
    [InlineData("http://127.0.0.1:11434", "  ")]
    public void An_endpoint_outside_the_policy_or_a_missing_model_fails_at_construction(string baseUrl, string? model)
    {
        var handler = Responding(HttpStatusCode.OK, Completion(ValidAnswer));

        Assert.Throws<InvalidOperationException>(() => new LocalModelAnswerGenerator(Options(baseUrl, model), handler));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public async Task A_valid_json_object_is_an_answer_with_its_citations()
    {
        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, Completion(ValidAnswer)));

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Equal("Hayır; yalnızca kullanılabilir stok taşınır.", answer.Text);
        Assert.Equal(["S1", "S2"], answer.CitedIds);
        Assert.Equal("Yalnız iki kaynağa dayanır.", answer.Limitations);
    }

    [Theory]
    [InlineData("İşte yanıtım:\n```json\n{0}\n```\nUmarım yardımcı olur.")]
    [InlineData("```\n{0}\n```")]
    [InlineData("   \n{0}\n\n")]
    [InlineData("Yanıt: {0} (kaynaklara göre)")]
    public async Task An_object_wrapped_in_prose_or_a_code_fence_is_still_parsed(string wrapper)
    {
        var content = wrapper.Replace("{0}", ValidAnswer, StringComparison.Ordinal);

        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, Completion(content)));

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Equal("Hayır; yalnızca kullanılabilir stok taşınır.", answer.Text);
        Assert.Equal(["S1", "S2"], answer.CitedIds);
    }

    [Fact]
    public async Task Citation_labels_are_returned_as_written_apart_from_brackets_and_spaces_so_the_server_can_validate_them()
    {
        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, Completion("""{"answer": "Metin", "citations": ["[S1]", " S2 ", "S9", "kaynak-1"]}""")));

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Equal(["S1", "S2", "S9", "kaynak-1"], answer.CitedIds);
        Assert.Null(answer.Limitations);
    }

    [Theory]
    [InlineData("""{"insufficient": true, "limitations": "Kaynaklar bu soruyu kapsamıyor."}""", "Kaynaklar bu soruyu kapsamıyor.")]
    [InlineData("""{"answer": "", "citations": [], "insufficient": true}""", null)]
    [InlineData("""{"answer": "Yine de bir şeyler yazdım.", "citations": ["S1"], "insufficient": true, "limitations": 5}""", null)]
    public async Task Insufficient_true_is_an_abstention_whatever_else_the_object_says(string content, string? limitations)
    {
        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, Completion(content)));

        Assert.Equal(GenerationOutcome.Insufficient, answer.Outcome);
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
        Assert.Equal(limitations, answer.Limitations);
    }

    public static TheoryData<string, string> MalformedContents => new()
    {
        { "missing answer", """{"citations": ["S1"], "insufficient": false}""" },
        { "missing citations", """{"answer": "Metin", "insufficient": false}""" },
        { "answer is not a string", """{"answer": 42, "citations": ["S1"]}""" },
        { "answer is null", """{"answer": null, "citations": ["S1"]}""" },
        { "citations is not an array", """{"answer": "Metin", "citations": "S1"}""" },
        { "citations is an object", """{"answer": "Metin", "citations": {"S1": true}}""" },
        { "non-string citation", """{"answer": "Metin", "citations": [1]}""" },
        { "mixed citations", """{"answer": "Metin", "citations": ["S1", null]}""" },
        { "nested citation", """{"answer": "Metin", "citations": [["S1"]]}""" },
        { "insufficient as text is not an abstention", """{"insufficient": "true"}""" },
        { "empty content", "" },
        { "blank content", "  \n\t " },
        { "plain prose", "Kaynaklara göre rezerve stok taşınamaz. [S1]" },
        { "an array instead of an object", """["S1", "S2"]""" },
        { "truncated json", """{"answer": "Yarım kalan bir yan""" },
        { "truncated after a nested brace", """{"answer": "Metin {yarım}", "citations": ["S1" """ },
        { "two objects", """{"answer": "Bir", "citations": ["S1"]} {"answer": "İki", "citations": ["S2"]}""" },
        { "html inside the content", "<html><body><h1>502 Bad Gateway</h1></body></html>" },
        { "single quotes", "{'answer': 'Metin', 'citations': ['S1']}" },
    };

    [Theory]
    [MemberData(nameof(MalformedContents))]
    public async Task Content_that_is_not_the_expected_object_is_malformed(string name, string content)
    {
        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, Completion(content)));

        Assert.True(answer.Outcome == GenerationOutcome.Malformed, $"{name}: expected Malformed but got {answer.Outcome}");
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
    }

    public static TheoryData<string, string, string> MalformedBodies => new()
    {
        { "html error page with status 200", "<!DOCTYPE html><html><head><title>Hata</title></head><body>Sunucu hatası</body></html>", "text/html" },
        { "empty body", "", "application/json" },
        { "not json", "upstream connect error or disconnect/reset before headers", "text/plain" },
        { "truncated envelope", """{"choices": [{"message": {"role": "assistant", "content": "{\"answer\": \"Met""", "application/json" },
        { "no choices", """{"id": "x", "choices": []}""", "application/json" },
        { "choices is not an array", """{"choices": {"message": {"content": "{}"}}}""", "application/json" },
        { "no message", """{"choices": [{"index": 0}]}""", "application/json" },
        { "null content", """{"choices": [{"message": {"role": "assistant", "content": null}}]}""", "application/json" },
        { "content is an object", """{"choices": [{"message": {"content": {"answer": "Metin", "citations": ["S1"]}}}]}""", "application/json" },
        { "error object", """{"error": {"message": "model not loaded", "type": "invalid_request_error"}}""", "application/json" },
        { "root is an array", """[{"message": {"content": "{}"}}]""", "application/json" },
        { "root is a string", "\"model is loading\"", "application/json" },
        { "root is null", "null", "application/json" },
        { "choice is not an object", """{"choices": ["{}"]}""", "application/json" },
        { "message is not an object", """{"choices": [{"message": "{}"}]}""", "application/json" },
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task A_success_status_with_an_unusable_body_is_malformed(string name, string body, string mediaType)
    {
        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, body, mediaType));

        Assert.True(answer.Outcome == GenerationOutcome.Malformed, $"{name}: expected Malformed but got {answer.Outcome}");
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
    }

    [Fact]
    public async Task More_citations_than_any_source_budget_allows_are_malformed()
    {
        var citations = string.Join(", ", Enumerable.Range(1, 17).Select(i => $"\"S{i}\""));

        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, Completion($$"""{"answer": "Metin", "citations": [{{citations}}]}""")));

        Assert.Equal(GenerationOutcome.Malformed, answer.Outcome);
    }

    // ------------------------------------------------------------------ failures

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task An_error_status_is_unavailable_even_when_the_body_looks_like_an_answer(HttpStatusCode status)
    {
        var handler = Responding(status, Completion(ValidAnswer));

        var answer = await GenerateAsync(handler);

        Assert.Equal(GenerationOutcome.Unavailable, answer.Outcome);
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task A_redirect_is_unavailable_and_no_second_request_is_made(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(Completion(ValidAnswer), Encoding.UTF8, "application/json") };
            response.Headers.Location = new Uri("https://example.com/v1/chat/completions");
            return Task.FromResult(response);
        });

        var answer = await GenerateAsync(handler);

        Assert.Equal(GenerationOutcome.Unavailable, answer.Outcome);
        Assert.Equal(new Uri("http://127.0.0.1:11434/v1/chat/completions"), Assert.Single(handler.Requests).Uri);
    }

    public static TheoryData<string> TransportFailures => ["connection refused", "socket", "io", "http client timeout"];

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task A_transport_failure_is_unavailable_not_an_exception(string kind)
    {
        Exception failure = kind switch
        {
            "connection refused" => new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused)),
            "socket" => new SocketException((int)SocketError.HostUnreachable),
            "io" => new IOException("The response ended prematurely."),
            _ => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        };

        var answer = await GenerateAsync(new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(failure)));

        Assert.Equal(GenerationOutcome.Unavailable, answer.Outcome);
        Assert.Null(answer.Text);
    }

    [Fact]
    public async Task A_server_that_never_answers_is_cut_off_by_the_configured_timeout()
    {
        // The adapter clamps the timeout to at least five seconds; the handler waits until it is cancelled.
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var generator = new LocalModelAnswerGenerator(Options(timeoutSeconds: 1), handler);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var answer = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.Equal(GenerationOutcome.Unavailable, answer.Outcome);
        Assert.InRange(System.Diagnostics.Stopwatch.GetElapsedTime(started), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Cancellation_by_the_caller_propagates_instead_of_becoming_unavailable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var generator = new LocalModelAnswerGenerator(Options(), handler);
        using var caller = new CancellationTokenSource();

        var pending = generator.GenerateAsync(Request, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_response_larger_than_the_body_limit_is_never_an_answer()
    {
        var huge = Completion("""{"answer": "Metin", "citations": ["S1"], "limitations": """ + JsonSerializer.Serialize(new string('x', 300 * 1024)) + "}");

        var answer = await GenerateAsync(Responding(HttpStatusCode.OK, huge));

        Assert.Contains(answer.Outcome, new[] { GenerationOutcome.Unavailable, GenerationOutcome.Malformed });
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
    }

    // ------------------------------------------------------------------ production transport on loopback

    /// <summary>A minimal HTTP/1.1 responder on 127.0.0.1 with an ephemeral port. Counts the requests it received.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<int, string> _response;
        private readonly Task _loop;
        private int _requests;

        public LoopbackServer(Func<int, string> response)
        {
            _response = response;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public int Requests => Volatile.Read(ref _requests);

        public static string Response(int status, string reason, string body, string extraHeaders = "") =>
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{extraHeaders}\r\n{body}";

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    await ReadRequestAsync(stream, _stop.Token);
                    var response = _response(Interlocked.Increment(ref _requests));
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The test is over or the client hung up.
            }
        }

        private static async Task ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var received = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    var lengthLine = text[..headerEnd].Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    if (received.Length >= headerEnd + 4 + length)
                    {
                        return;
                    }
                }

                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                received.Write(buffer, 0, read);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop.WaitAsync(TimeSpan.FromSeconds(10));
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task The_production_transport_reaches_a_loopback_server_and_parses_its_answer()
    {
        await using var server = new LoopbackServer(_ => LoopbackServer.Response(200, "OK", Completion(ValidAnswer)));
        using var generator = new LocalModelAnswerGenerator(Options($"http://127.0.0.1:{server.Port}"));

        var answer = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Equal(["S1", "S2"], answer.CitedIds);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task The_production_transport_does_not_follow_a_redirect()
    {
        await using var target = new LoopbackServer(_ => LoopbackServer.Response(200, "OK", Completion(ValidAnswer)));
        await using var redirecting = new LoopbackServer(_ => LoopbackServer.Response(
            307, "Temporary Redirect", string.Empty, $"Location: http://127.0.0.1:{target.Port}/v1/chat/completions\r\n"));
        using var generator = new LocalModelAnswerGenerator(Options($"http://127.0.0.1:{redirecting.Port}"));

        var answer = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.Equal(GenerationOutcome.Unavailable, answer.Outcome);
        Assert.Equal(1, redirecting.Requests);
        Assert.Equal(0, target.Requests);
    }

    [Fact]
    public async Task The_production_transport_reports_a_closed_port_as_unavailable()
    {
        int port;
        await using (var server = new LoopbackServer(_ => LoopbackServer.Response(200, "OK", Completion(ValidAnswer))))
        {
            port = server.Port;
        }

        using var generator = new LocalModelAnswerGenerator(Options($"http://127.0.0.1:{port}"));

        var answer = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.Equal(GenerationOutcome.Unavailable, answer.Outcome);
    }

    // ------------------------------------------------------------------ parser used directly

    [Fact]
    public void Parse_reads_only_the_first_choice()
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = ValidAnswer } },
                new { message = new { role = "assistant", content = """{"answer": "İkinci seçenek", "citations": ["S9"]}""" } },
            },
        });

        var answer = LocalModelAnswerGenerator.Parse(body);

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Equal(["S1", "S2"], answer.CitedIds);
    }

    [Fact]
    public void Parse_keeps_an_answered_object_with_an_empty_citation_list_for_the_server_to_reject()
    {
        // The adapter reports what the model said; refusing an uncited answer is the server's rule.
        var answer = LocalModelAnswerGenerator.Parse(Completion("""{"answer": "Atıfsız metin", "citations": []}"""));

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Empty(answer.CitedIds);
    }
}
