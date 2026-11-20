using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The OpenRouter quota check through its seams: a fake HTTP handler, a key supplier and a temp home of the test's
/// own, so nothing here reads the real key or reaches openrouter.ai.
/// </summary>
public sealed class OpenRouterQuotaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-openrouter-").FullName;
    private readonly FakeOpenRouter _api = new();
    private readonly long _t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private Func<string?>? _key = () => "sk-or-v1-test";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>An agent with OpenRouter in the tracked services, unless the test says otherwise.</summary>
    private QuotaMonitor Agent(params string[] providers)
    {
        var list = providers.Length == 0 ? new[] { "claude", "codex", "antigravity", "openrouter" } : providers;
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{\"codexBar\":{\"providers\":[" + string.Join(",", list.Select(p => "\"" + p + "\"")) + "]}}");
        return new QuotaMonitor(new ConfigStore(path), NullLogger<QuotaMonitor>.Instance)
        {
            HttpHandler = _api,
            ReadOpenRouterKey = _key,
        };
    }

    private static QuotaProvider? OpenRouter(QuotaMonitor agent) => agent.Snapshot().FirstOrDefault(p => p.Id == "openrouter");

    [Fact]
    public async Task CreditsBecomeOneRingWithTheDollarsInTheFooter()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":25.75}}""");
        var agent = Agent();

        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal(2, _api.Calls);      // the credits endpoint and the key's own endpoint, every pass
        Assert.Equal("sk-or-v1-test", _api.LastToken);

        var p = Assert.Single(agent.Snapshot(), x => x.Id == "openrouter");
        Assert.Equal("OpenRouter", p.Name);
        Assert.Equal("Prepaid", p.Plan);
        var l = Assert.Single(p.Limits);
        Assert.Equal("Credits", l.Label);
        Assert.Equal(25.75, l.Percent, 6);
        Assert.Null(l.ResetsAt);          // prepaid credits never reset: no reset line on the ring
        Assert.Null(l.WindowSec);         // and no pace marker
        Assert.Equal("normal", l.Severity);
        Assert.Equal("$74.25 of $100.00 left", p.Extra);
    }

    [Fact]
    public async Task AReadingInsideTheHealthyWindowIsNotFetchedAgain()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":25.0}}""");
        var agent = Agent();
        await agent.OpenRouterTickAsync(_t0, default);

        await agent.OpenRouterTickAsync(_t0 + 60_000, default);
        Assert.Equal(2, _api.Calls);      // openrouter.ai serves credits cached for a minute anyway
        Assert.Equal(25, Assert.Single(OpenRouter(agent)!.Limits).Percent, 6);

        await agent.OpenRouterTickAsync(_t0 + QuotaMonitor.OpenRouterHealthyMs + 1, default);
        Assert.Equal(4, _api.Calls);
    }

    [Fact]
    public async Task ALowBalanceTurnsAmberThenRed()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":80.0}}""");
        var agent = Agent();
        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal("warning", Assert.Single(OpenRouter(agent)!.Limits).Severity); // a fifth of the credits left

        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":95.0}}""");
        var other = Agent();
        await other.OpenRouterTickAsync(_t0, default);
        Assert.Equal("critical", Assert.Single(OpenRouter(other)!.Limits).Severity);
    }

    [Fact]
    public async Task AnAccountWithoutCreditsSaysSo()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":0,"total_usage":0}}""");
        var agent = Agent();
        await agent.OpenRouterTickAsync(_t0, default);
        var p = OpenRouter(agent);
        Assert.NotNull(p);
        Assert.Empty(p!.Limits);
        Assert.Equal("No credits on this account", p.Error);
    }

    [Fact]
    public async Task WithoutAKeyTheCardStaysAwayQuietly()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":1.0}}""");
        _key = () => null;
        var agent = Agent();

        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal(0, _api.Calls);
        Assert.Null(OpenRouter(agent));
    }

    [Fact]
    public async Task UntickedInTheSettingsItIsNeverPolled()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":1.0}}""");
        var agent = Agent("claude", "codex", "antigravity");

        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal(0, _api.Calls);
        Assert.Null(OpenRouter(agent));
    }

    [Fact]
    public async Task ARefusedBalanceFallsBackToTheKeysOwnLimit()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.Forbidden, """{"error":"Only management keys can perform this operation"}""");
        _api.Respond("/api/v1/key", HttpStatusCode.OK, """{"data":{"limit":50.0,"usage":10.0,"limit_remaining":40.0}}""");
        var agent = Agent();

        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal(2, _api.Calls);
        var p = OpenRouter(agent)!;
        Assert.Equal(20, Assert.Single(p.Limits).Percent, 6);
        Assert.Equal("$40.00 of $50.00 left", p.Extra);
    }

    [Fact]
    public async Task AKeyWithoutALimitCannotShowABalance()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.Forbidden, """{"error":"Only management keys can perform this operation"}""");
        _api.Respond("/api/v1/key", HttpStatusCode.OK, """{"data":{"limit":null,"usage":10.0}}""");
        var agent = Agent();

        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal(2, _api.Calls);
        Assert.NotEmpty(OpenRouter(agent)!.Error!);
    }

    [Fact]
    public async Task TheWorkspaceBudgetWinsWhenItLeavesTheLeast()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":20.0}}""");
        _api.KeyJson = """{"data":{"effective_budget":{"limit_usd":30.0,"spend_usd":10.0,"remaining_usd":20.0,"reset_interval":"monthly","resets_at":"2099-01-01T00:00:00Z"}}}""";
        var agent = Agent();

        await agent.OpenRouterTickAsync(_t0, default);
        var p = OpenRouter(agent)!;
        var l = Assert.Single(p.Limits);
        Assert.Equal("Monthly budget", l.Label);    // 20 left there vs 80 of credits: the guardrail binds
        Assert.Equal(33.33, l.Percent, 2);
        Assert.NotNull(l.ResetsAt);                 // it resets, so the ring gets a reset line and a pace marker
        Assert.Equal(30 * 86_400, l.WindowSec);
        Assert.Equal("$20.00 of $30.00 left", p.Extra);
    }

    [Fact]
    public async Task TheKeyCapWinsWhenItIsTheTightest()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":1.0}}""");
        _api.KeyJson = """{"data":{"limit":15.0,"limit_remaining":15.0,"usage":0.0,"effective_budget":{"limit_usd":30.0,"remaining_usd":25.0,"reset_interval":"monthly","resets_at":"2099-01-01T00:00:00Z"}}}""";
        var agent = Agent();

        await agent.OpenRouterTickAsync(_t0, default);
        var p = OpenRouter(agent)!;
        var l = Assert.Single(p.Limits);
        Assert.Equal("Key limit", l.Label);         // 15 left there vs 99 credits and 25 budget
        Assert.Equal(0, l.Percent, 6);
        Assert.Null(l.ResetsAt);
        Assert.Equal("$15.00 of $15.00 left", p.Extra);
    }

    [Fact]
    public async Task CreditsWinWhenTheyRunOutFirst()
    {
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":30.0,"total_usage":25.0}}""");
        _api.KeyJson = """{"data":{"limit":100.0,"limit_remaining":100.0,"usage":0.0,"effective_budget":{"limit_usd":30.0,"spend_usd":5.0,"remaining_usd":25.0,"reset_interval":"monthly","resets_at":"2099-01-01T00:00:00Z"}}}""";
        var agent = Agent();

        await agent.OpenRouterTickAsync(_t0, default);
        var p = OpenRouter(agent)!;
        Assert.Equal("Credits", Assert.Single(p.Limits).Label); // 5 credits left vs 25 budget and 100 key
        Assert.Equal("$5.00 of $30.00 left", p.Extra);
    }

    [Fact]
    public async Task TheKeyIsReadFromTheLegacyAuthJson()
    {
        var auth = Path.Combine(_dir, "auth.json");
        File.WriteAllText(auth, """{"openrouter":{"type":"api","key":"sk-or-v1-legacy"}}""");
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":1.0}}""");
        _key = null; // the lookup under test is the built-in one, pointed at the file above
        var agent = new QuotaMonitor(ConfigWithOpenRouter(), NullLogger<QuotaMonitor>.Instance)
        {
            HttpHandler = _api,
            AuthJsonPath = auth,
        };

        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal("sk-or-v1-legacy", _api.LastToken);
    }

    [Fact]
    public async Task TheKeyPastedInSettingsWins()
    {
        var path = Path.Combine(_dir, "cfg-key.json");
        File.WriteAllText(path, """{"codexBar":{"providers":["openrouter"]},"openRouter":{"apiKey":"sk-or-v1-settings"}}""");
        _api.Respond("/api/v1/credits", HttpStatusCode.OK, """{"data":{"total_credits":100.0,"total_usage":1.0}}""");
        _key = null; // no fake key, no auth.json, no env: the settings value is the only source
        var agent = new QuotaMonitor(new ConfigStore(path), NullLogger<QuotaMonitor>.Instance)
        {
            HttpHandler = _api,
            AuthJsonPath = Path.Combine(_dir, "missing-auth.json"),
        };

        await agent.OpenRouterTickAsync(_t0, default);
        Assert.Equal("sk-or-v1-settings", _api.LastToken);
        Assert.Equal("$99.00 of $100.00 left", OpenRouter(agent)!.Extra);
    }

    /// <summary>The key is a secret like the camera logins: config.json on this computer, never the public config.</summary>
    [Fact]
    public void TheApiKeyNeverReachesThePublicConfig()
    {
        var path = Path.Combine(_dir, "cfg-secret.json");
        File.WriteAllText(path, """{"openRouter":{"apiKey":"sk-or-v1-secret"},"codexBar":{"providers":["openrouter"]}}""");
        var json = System.Text.Json.JsonSerializer.Serialize(new ConfigStore(path).Get().ToPublic());
        Assert.DoesNotContain("sk-or-v1-secret", json);
    }

    [Theory]
    [InlineData("""{"type":"api","key":"sk-or-v1-a"}""", "sk-or-v1-a")]                // an auth.json credential value
    [InlineData("""{"type":"key","key":"sk-or-v1-a2"}""", "sk-or-v1-a2")]             // OpenCode's newer key shape
    [InlineData("""{"openrouter":{"type":"api","key":"sk-or-v1-b"}}""", "sk-or-v1-b")] // legacy auth.json wraps it
    [InlineData("""{"type":"oauth","access":"not-a-key"}""", null)]                   // an OAuth credential is not a key
    [InlineData("sk-or-v1-raw", "sk-or-v1-raw")]                                      // a bare key, just in case
    [InlineData("garbage", null)]
    [InlineData("{broken", null)]
    [InlineData("", null)]
    public void TheCredentialValueIsParsedConservatively(string? value, string? expected) =>
        Assert.Equal(expected, QuotaMonitor.KeyFromCredential(value));

    [Fact]
    public void TheSnapshotKeepsExactlyOneOpenRouterCard()
    {
        var agent = Agent();
        agent.SetStateForTest(
            external: [CodexBarCopy(_t0)],
            openrouter: LocalReading());
        // CodexBar's copy is as fresh as the local one: it wins, exactly as it does for Claude, Codex and Antigravity
        Assert.Equal("CodexBar", Assert.Single(agent.Snapshot(), x => x.Id == "openrouter").Plan);

        var other = Agent();
        other.SetStateForTest(
            external: [CodexBarCopy(_t0 - 660_000)],   // stale beyond the 10-minute freshness window
            openrouter: LocalReading());
        Assert.Equal("Prepaid", Assert.Single(other.Snapshot(), x => x.Id == "openrouter").Plan); // the local reading
    }

    private static QuotaProvider CodexBarCopy(long updatedAt) =>
        new("openrouter", "OpenRouter", "CodexBar", [new QuotaLimit("Credits", 99, null, null, "normal")],
            null, updatedAt, null, Source: "codexbar");

    private static QuotaProvider LocalReading() =>
        new("openrouter", "OpenRouter", "Prepaid", [new QuotaLimit("Credits", 42, null, null, "normal")],
            "$58.00 of $100.00 left", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);

    private ConfigStore ConfigWithOpenRouter()
    {
        var path = Path.Combine(_dir, "config.json");
        if (!File.Exists(path)) File.WriteAllText(path, """{"codexBar":{"providers":["claude","codex","antigravity","openrouter"]}}""");
        return new ConfigStore(path);
    }
}

/// <summary>Answers only the two openrouter.ai URLs the monitor may call; anything else fails the test.</summary>
public sealed class FakeOpenRouter : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();
    public int Calls { get; private set; }
    public string? LastToken { get; private set; }
    /// <summary>The /key answer when a test does not Respond() that route itself: an empty key endpoint.</summary>
    public string KeyJson = """{"data":{}}""";

    public FakeOpenRouter() => _routes["/api/v1/key"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(KeyJson, Encoding.UTF8, "application/json") };

    public void Respond(string pathAndQuery, HttpStatusCode status, string body) =>
        _routes[pathAndQuery] = () => new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        LastToken = request.Headers.Authorization?.Parameter;
        var url = request.RequestUri!.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
        Assert.True(_routes.TryGetValue(url, out var route), "unexpected request to " + url);
        return Task.FromResult(route());
    }
}
