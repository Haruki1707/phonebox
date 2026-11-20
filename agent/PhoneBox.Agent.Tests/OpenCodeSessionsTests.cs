using System.Net;
using System.Text;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// OpenCode sessions through the background service's local API: a fake handler answers /api/session and the mapping
/// to the widget's sessions (name, project, busy/idle, outcome, tokens) is checked, on the native service and a WSL
/// one alike. Nothing here — or in the reader — ever opens a database.
/// </summary>
public sealed class OpenCodeSessionsTests : IDisposable
{
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-opencode-sessions-").FullName;
    private readonly FakeService _fake = new();

    public OpenCodeSessionsTests() => OpenCodeSessions.HttpHandler = _fake;

    public void Dispose()
    {
        OpenCodeSessions.HttpHandler = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The part of OpenCode the reader talks to: one session list per service url.</summary>
    private sealed class FakeService : HttpMessageHandler
    {
        public string SessionsJson = """{"data":[]}""";
        public string SessionsJsonB = """{"data":[]}""";
        public readonly List<string> Calls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls.Add(req.Method.Method + " " + req.RequestUri!.PathAndQuery);
            var body = req.RequestUri.PathAndQuery.Contains("/api/session")
                ? req.RequestUri.Authority switch
                {
                    var a when a.Contains(":28999") => SessionsJsonB,
                    var a when a.Contains(":404") => null, // nothing listens there
                    _ => SessionsJson,
                }
                : null;
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>One /api/session item with the shape OpenCode 2.x sends; the token numbers are fixed.</summary>
    private string Session(string id, string directory, string title, long updated, long? busy = null, long? idle = null,
        string? outcome = "succeeded")
    {
        var time = $"{{\"created\":{_now - 600_000},\"updated\":{updated}"
            + (busy is { } b ? $",\"busy\":{b}" : "") + (idle is { } i ? $",\"idle\":{i}" : "") + "}";
        var oc = outcome is null ? "null" : $"\"{outcome}\"";
        return "{\"id\":\"" + id + "\",\"title\":\"" + title + "\",\"agent\":\"build\","
            + "\"model\":{\"id\":\"z-ai/glm-5.3-flash\",\"providerID\":\"openrouter\"},"
            + "\"outcome\":" + oc + ",\"location\":{\"directory\":\"" + directory + "\"},"
            + "\"time\":" + time + ",\"tokens\":{\"input\":100,\"output\":20,\"reasoning\":5,"
            + "\"cache\":{\"read\":400,\"write\":10}}}";
    }

    private Task<List<AgentSession>> Read(string url = "http://127.0.0.1:28894") =>
        OpenCodeSessions.ReadAsync(services: [new OpenCodeApprovals.ServiceInfo(url, "pw", "2.3.2")], nowMs: _now);

    [Fact]
    public async Task SessionsAreMappedForTheWidget()
    {
        _fake.SessionsJson = """{"data":["""
            + Session("ses_a", "/home/haruki/GitHub/wolfi-os", "Always allow test", _now - 300_000, busy: _now - 320_000, idle: _now - 300_000)
            + "," + Session("ses_b", "C:/proj", "Busy right now", _now - 5_000, busy: _now - 10_000)
            + """]}""";

        var list = await Read();
        Assert.Equal(2, list.Count);

        var idle = Assert.Single(list, s => s.Id == "opencode:ses_a");
        Assert.Equal("opencode", idle.Tool);
        Assert.Equal("wolfi-os-always-allow-test", idle.Name);  // the title as a kebab slug, like OpenCode's own
        Assert.Equal("wolfi-os", idle.Project);
        Assert.Equal("idle", idle.Status);
        Assert.Equal("completed", idle.Outcome);                // succeeded, and no turn since
        Assert.Equal("z-ai/glm-5.3-flash", idle.Model);
        Assert.Equal(530, idle.TotalTokens);                    // in + out + cache read + cache write
        Assert.Equal(100, idle.InputTokens);
        Assert.Equal(20, idle.OutputTokens);
        Assert.Equal(400, idle.CachedInputTokens);
        Assert.Equal(5, idle.ReasoningTokens);
        Assert.Null(idle.TurnStartedAt);
        Assert.Null(idle.LastTool);                             // tool names live in the messages, never fetched
        Assert.Equal(_now - 300_000, idle.Since);
        Assert.Equal(_now - 600_000, idle.StartedAt);

        var busy = Assert.Single(list, s => s.Id == "opencode:ses_b");
        Assert.Equal("busy", busy.Status);
        Assert.Null(busy.Outcome);                              // a busy session has not finished yet
        Assert.Equal(_now - 10_000, busy.TurnStartedAt);        // the turn's start is shown as "working for …"
        Assert.Equal("proj", busy.Project);
    }

    [Fact]
    public async Task SessionsOlderThanThreeHoursAreDropped()
    {
        _fake.SessionsJson = """{"data":["""
            + Session("ses_old", "/home/haruki/x", "Old", _now - 4 * 3600_000)
            + "," + Session("ses_new", "/home/haruki/y", "New", _now - 60_000)
            + """]}""";
        var list = await Read();
        Assert.Single(list);
        Assert.Equal("opencode:ses_new", list[0].Id);
    }

    [Fact]
    public async Task TheNativeServiceAndTheWslOneMergeIntoOneList()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", "Native", _now - 120_000) + """]}""";
        _fake.SessionsJsonB = """{"data":[""" + Session("ses_b", "/home/haruki/w", "In WSL", _now - 60_000) + """]}""";
        var list = await OpenCodeSessions.ReadAsync(services:
        [
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28894", "pw", "2.3.2"),
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28999", "pw2", "2.3.2"),
        ], nowMs: _now);
        Assert.Equal(2, list.Count);
        Assert.Equal("opencode:ses_b", list[0].Id); // newest first across services
        Assert.Equal("opencode:ses_a", list[1].Id);
    }

    [Fact]
    public async Task AServiceThatIsNotRunningContributesNothing()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", "Native", _now - 60_000) + """]}""";
        var list = await OpenCodeSessions.ReadAsync(services:
        [
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28894", "pw", "2.3.2"),
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:404", "pw", "2.3.2"), // nothing listens there
        ], nowMs: _now);
        Assert.Single(list);
    }

    /// <summary>The privacy contract: the reader speaks to the session list and to nothing else.</summary>
    [Fact]
    public async Task OnlyTheSessionListIsEverFetched()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", "Native", _now - 60_000) + """]}""";
        await Read();
        Assert.All(_fake.Calls, c => Assert.Equal("GET /api/session", c));
    }

    [Theory]
    [InlineData("Always allow test", "always-allow-test")]
    [InlineData("PHP 8.6 YAML generation!", "php-8-6-yaml-generation")]
    [InlineData("  Trimmed  title  ", "trimmed-title")]
    [InlineData("你好世界", null)]                       // non-ASCII titles have no kebab form: the short id takes over
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TitlesBecomeKebabSlugs(string? title, string? expected) =>
        Assert.Equal(expected, OpenCodeSessions.Slug(title));

    [Fact]
    public void DiscoveryCombinesTheNativePathWithRunningWslDistros()
    {
        var native = Path.Combine(_dir, ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, """{"url":"http://127.0.0.1:28894","password":"pw","version":"2.3.2"}""");
        var wslRoot = Path.Combine(_dir, "wsl.localhost", "Ubuntu"); // a \\wsl.localhost stand-in
        var wslService = Path.Combine(wslRoot, "home", "haruki", ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(wslService)!);
        File.WriteAllText(wslService, """{"url":"http://127.0.0.1:49374","password":"pw","version":"2.3.2"}""");
        Directory.CreateDirectory(Path.Combine(wslRoot, "home", "emptyuser")); // a user without OpenCode

        var paths = OpenCodeServices.Discover(homeDir: _dir, wslDistroRoots: [wslRoot]);
        Assert.Equal(2, paths.Count);
        Assert.Equal(native, paths[0]);                     // the native one first
        Assert.Equal(wslService, paths[1]);                 // then the distro's user's

        var nativeOnly = OpenCodeServices.Discover(homeDir: _dir, wslDistroRoots: [wslRoot], wslEnabled: false);
        Assert.Equal([native], nativeOnly);
    }
}
