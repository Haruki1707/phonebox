using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// OpenCode permission prompts: the queue is polled, offered to the phone through ApprovalStore, and the answer is
/// posted back as once / always / reject. A prompt answered on the desktop first is withdrawn without a reply.
/// </summary>
[Collection("Approval request expiry")] // shares the static PhoneLastSeen with the other approval tests
public sealed class OpenCodeApprovalsTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public long Now = 100_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    /// <summary>The part of OpenCode the monitor talks to: known directories (from the session list), one queue, and a reply route.</summary>
    private sealed class FakeOpenCode : HttpMessageHandler
    {
        /// <summary>The queue as /api/permission/request answers it; null turns every read into a 404.</summary>
        public string? QueueJson = """{"data":[]}""";
        /// <summary>A second service's queue (the "b" port): a second OpenCode, native or inside WSL.</summary>
        public string? QueueJsonB = """{"data":[]}""";
        /// <summary>The pending forms as /api/form answers them; null turns every read into a 404.</summary>
        public string? FormsJson = """{"data":[]}""";
        /// <summary>The session list /api/session answers; its directories are what the queues get polled under.</summary>
        public string SessionsJson = """{"data":[{"id":"ses_1","location":{"directory":"C:\\proj"}}]}""";
        public readonly List<string> Replies = [];
        public readonly List<string> ReplyUrls = [];
        public readonly List<string> FormReplies = [];
        public readonly List<string> Calls = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Calls.Add(req.Method.Method + " " + url);
            if (req.Method == HttpMethod.Post && url.Contains("/permission/") && url.EndsWith("/reply"))
            {
                using var reader = new StreamReader(req.Content!.ReadAsStream(ct), Encoding.UTF8);
                Replies.Add(await reader.ReadToEndAsync(ct));
                ReplyUrls.Add(url);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (req.Method == HttpMethod.Post && url.Contains("/form/"))
            {
                using var reader = new StreamReader(req.Content!.ReadAsStream(ct), Encoding.UTF8);
                FormReplies.Add(await reader.ReadToEndAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var body = url.Contains("/api/session")
                ? SessionsJson
                : url.Contains("/api/form") ? FormsJson
                : url.Contains("/api/permission/request") ? (url.Contains(":28999") ? QueueJsonB : QueueJson) : null;
            return new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") };
        }
    }

    private const string QueueOne = """{"data":[{"id":"per_1","sessionID":"ses_1","action":"shell","resources":["git push -f"],"message":"Push the branch?"}]}""";
    private const string FormOne = """{"data":[{"id":"frm_1","sessionID":"ses_1","title":"Questions","metadata":{"kind":"question"},"fields":[{"key":"q0","title":"Route","description":"Which route should we take?","type":"string","custom":true,"options":[{"value":"a","label":"Route A - fast","description":"Fastest"},{"value":"b","label":"Route B - safe","description":"Safest"}]}]}]}""";

    private readonly Clock _clock = new();
    private readonly long _phone = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-opencode-approvals-").FullName;
    private readonly PushStore _events;
    private readonly string _servicePath;

    public OpenCodeApprovalsTests()
    {
        _events = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        _servicePath = Path.Combine(_dir, ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_servicePath)!);
        File.WriteAllText(_servicePath, """{"url":"http://127.0.0.1:28894","password":"pw","version":"2.3.2"}""");
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, _phone);
        _events.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private OpenCodeApprovals Monitor(ApprovalStore store, FakeOpenCode fake, string? servicePath = null) =>
        new(store, NullLogger<OpenCodeApprovals>.Instance,
            config: null, http: fake, clock: _clock, servicePaths: [servicePath ?? _servicePath]);

    private static async Task WaitUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 500 && !done(); i++) await Task.Delay(20);
        Assert.True(done(), what);
    }

    [Fact]
    public void TheServiceFileGivesTheUrlAndPassword()
    {
        Assert.Equal(_servicePath, OpenCodeApprovals.ServicePath(homeDir: _dir));
        var s = OpenCodeApprovals.ReadService(_servicePath);
        Assert.Equal("http://127.0.0.1:28894", s!.Url);
        Assert.Equal("pw", s.Password);
        Assert.Equal("2.3.2", s.Version);
        Assert.Null(OpenCodeApprovals.ServicePath(homeDir: Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void AServiceFileWithoutAPasswordIsNotAService() =>
        Assert.Null(OpenCodeApprovals.ReadService(WriteRaw("""{"url":"http://127.0.0.1:1"}""")));

    private string WriteRaw(string json)
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task APendingPromptIsOfferedAndAllowAnswersOnce()
    {
        var fake = new FakeOpenCode { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);

        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.Equal("per_1", a.Id);
        Assert.Equal("OpenCode", a.Source);
        Assert.Equal("proj", a.Project);
        Assert.Equal("ses_1", a.Session);
        Assert.Equal("Run a command", a.Tool);
        Assert.Equal("Push the branch?\n\ngit push -f", a.Detail);
        Assert.Equal("permission", a.Kind);
        Assert.True(a.Always); // OpenCode prompts offer Allow always (it saves the pattern for the project)

        Assert.True(store.Decide("per_1", "allow"));
        await WaitUntil(() => fake.Replies.Count == 1, "the reply never reached OpenCode");
        Assert.Equal("""{"decision":"once"}""", fake.Replies[0]);
    }

    [Fact]
    public async Task DenyRepliesRejectAndAlwaysRepliesAlways()
    {
        foreach (var (decision, expected) in new[] { ("deny", "reject"), ("always", "always") })
        {
            var fake = new FakeOpenCode { QueueJson = QueueOne };
            var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
            var monitor = Monitor(store, fake);
            await monitor.PollOnceAsync(CancellationToken.None);
            Assert.True(store.Decide("per_1", decision));
            await WaitUntil(() => fake.Replies.Count == 1, $"{decision} never reached OpenCode");
            Assert.Equal($$"""{"decision":"{{expected}}"}""", fake.Replies[0]);
        }
    }

    [Fact]
    public async Task APromptAnsweredOnTheDesktopIsWithdrawnWithoutAReply()
    {
        var fake = new FakeOpenCode { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Single(store.Snapshot());

        fake.QueueJson = """{"data":[]}"""; // the desktop answered while the phone was thinking
        await monitor.PollOnceAsync(CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 0, "the withdrawn prompt stayed on the phone");
        Assert.Empty(fake.Replies);
    }

    [Fact]
    public async Task APromptArrivingWithoutAPhoneIsOfferedOnceOneConnects()
    {
        var fake = new FakeOpenCode { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, 0); // no phone has polled in a while
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Empty(store.Snapshot()); // nothing offered yet, so nothing could be replied either
        Assert.Empty(fake.Replies);     // OpenCode's own prompt stays in charge

        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue); // the phone shows up later
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Single(store.Snapshot()); // the still-pending prompt reaches it now
    }

    [Fact]
    public async Task WithoutAServiceNothingIsOffered()
    {
        var fake = new FakeOpenCode { QueueJson = QueueOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake, servicePath: Path.Combine(_dir, "missing.json"));
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Empty(store.Snapshot());
        Assert.Empty(monitor.LastServices);
    }

    [Fact]
    public async Task TheQueueIsPolledPerKnownDirectory()
    {
        var fake = new FakeOpenCode { QueueJson = QueueOne };
        var monitor = Monitor(new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock), fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        // the directories come from the session list (every session knows where it lives — that works for WSL too)
        Assert.Contains(fake.Calls, c => c.Contains("/api/session") && !c.Contains("/reply"));
        Assert.Contains(fake.Calls, c => c.Contains("/api/permission/request?location%5Bdirectory%5D=C%3A%5Cproj"));
    }

    /// <summary>Native OpenCode and OpenCode inside WSL are two services; both queues are offered, and each answer
    /// goes back to the service that asked.</summary>
    [Fact]
    public async Task TwoServicesArePolledIndependentlyAndAnsweredSeparately()
    {
        var native = Path.Combine(_dir, ".local", "state", "opencode", "service.json");
        var wsl = Path.Combine(_dir, "wsl", ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        File.WriteAllText(wsl, """{"url":"http://127.0.0.1:28999","password":"pw2","version":"2.3.2"}""");
        var fake = new FakeOpenCode { QueueJson = QueueOne, QueueJsonB = """{"data":[{"id":"per_2","sessionID":"ses_9","action":"edit","resources":["main.rs"]}]}""" };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = new OpenCodeApprovals(store, NullLogger<OpenCodeApprovals>.Instance,
            config: null, http: fake, clock: _clock, servicePaths: [native, wsl]);

        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, store.Snapshot().Count);
        Assert.Equal(2, monitor.LastServices.Count);

        Assert.True(store.Decide("per_1", "allow"));
        await WaitUntil(() => fake.Replies.Count == 1, "the native reply never reached OpenCode");
        Assert.True(store.Decide("per_2", "deny"));
        await WaitUntil(() => fake.Replies.Count == 2, "the WSL reply never reached OpenCode");
        Assert.Contains("28894", fake.ReplyUrls[0]);
        Assert.Contains("28999", fake.ReplyUrls[1]);
        Assert.Equal("""{"decision":"once"}""", fake.Replies[0]);
        Assert.Equal("""{"decision":"reject"}""", fake.Replies[1]);
    }

    /// <summary>The Claude hook has no "always": it allows, once. Real clock: the hook stamps expiry from UtcNow.</summary>
    [Fact]
    public async Task TheClaudeHookTakesAlwaysAsAnAllow()
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, "{}");
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, TimeProvider.System);
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var input = JsonDocument.Parse("""{"tool_name":"Bash","tool_input":{"command":"ls"},"cwd":"C:\\p","session_id":"s1"}""").RootElement.Clone();

        var hook = ClaudeHook.HandleAsync(input, new ConfigStore(cfgPath), store, sessions, CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 1, "the hook never offered the prompt");
        Assert.True(store.Decide(store.Snapshot()[0].Id, "always"));
        var output = await hook;
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("allow", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString());
    }

    [Fact]
    public void ActionsBecomeCardHeadlines()
    {
        string[] expected = ["Run a command", "Edit a file", "Fetch a web page", "Use myserver › mytool"];
        string[] actual = [OpenCodeApprovals.ToolTitle("shell"), OpenCodeApprovals.ToolTitle("edit"),
            OpenCodeApprovals.ToolTitle("webfetch"), OpenCodeApprovals.ToolTitle("myserver_mytool")];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AnAgentQuestionIsOfferedWithItsChoicesAndTheAnswerReachesTheForm()
    {
        var fake = new FakeOpenCode { QueueJson = """{"data":[]}""", FormsJson = FormOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.Equal("frm_1", a.Id);
        Assert.Equal("question", a.Kind);
        Assert.Equal("Route", a.Tool);              // the field's title is the headline
        Assert.Equal("Which route should we take?", a.Detail);
        Assert.Equal(2, a.Options!.Length);
        Assert.Equal("Route A - fast", a.Options[0].Label);
        Assert.Equal("Fastest", a.Options[0].Description);
        Assert.True(a.Custom);                      // "type your own answer" is allowed

        Assert.True(store.Decide("frm_1", "answer", "b"));
        await WaitUntil(() => fake.FormReplies.Count == 1, "the answer never reached the form");
        Assert.Equal("""{"answer":{"q0":"b"}}""", fake.FormReplies[0]);
    }

    [Fact]
    public async Task AQuestionAnsweredOnTheDesktopIsWithdrawnWithoutAReply()
    {
        var fake = new FakeOpenCode { QueueJson = """{"data":[]}""", FormsJson = FormOne };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Single(store.Snapshot());

        fake.FormsJson = """{"data":[]}"""; // the OpenCode app answered while the phone was thinking
        await monitor.PollOnceAsync(CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 0, "the withdrawn question stayed on the phone");
        Assert.Empty(fake.FormReplies);
    }

    [Fact]
    public async Task AFormWithoutTapAnswersIsShownAsTextOnly()
    {
        var multi = """{"data":[{"id":"frm_2","sessionID":"ses_1","title":"Details","fields":[{"key":"a","title":"Name","description":"Your name?","type":"string"},{"key":"b","title":"OK?","type":"boolean"}]}]}""";
        var fake = new FakeOpenCode { QueueJson = """{"data":[]}""", FormsJson = multi };
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, _clock);
        var monitor = Monitor(store, fake);
        await monitor.PollOnceAsync(CancellationToken.None);

        var a = Assert.Single(store.Snapshot());
        Assert.Equal("question", a.Kind);
        Assert.Equal("OpenCode has a question", a.Tool);
        Assert.Contains("Name: Your name?", a.Detail);
        Assert.Null(a.Options);   // nothing to tap: answered at the PC
        Assert.False(a.Custom);
        Assert.True(store.Decide(a.Id, "answer", "whatever")); // the phone could still send something…
        await WaitUntil(() => fake.FormReplies.Count == 0, "a text-only form was answered after all");
    }

    [Fact]
    public void TheCountdownIsClampedAndReachesThePhone()
    {
        var path = Path.Combine(_dir, "cfg-countdown.json");
        File.WriteAllText(path, """{"opencode": {"alwaysCountdownSec": 500}}""");
        var cfg = new ConfigStore(path).Get();
        Assert.Equal(60, cfg.OpenCode.AlwaysCountdownSec);
        Assert.True(cfg.OpenCode.WatchWsl); // WSL watching is on by default
    }

    /// <summary>A Claude hook has nowhere to put a typed answer: it must not count as a decision.</summary>
    [Fact]
    public async Task TheClaudeHookIgnoresAnswerValues()    {
        var cfgPath = Path.Combine(_dir, "config2.json");
        File.WriteAllText(cfgPath, "{}");
        var store = new ApprovalStore(_events, NullLogger<ApprovalStore>.Instance, TimeProvider.System);
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var input = JsonDocument.Parse("""{"tool_name":"AskUserQuestion","tool_input":{"questions":[]},"cwd":"C:\\p","session_id":"s2"}""").RootElement.Clone();

        var hook = ClaudeHook.HandleAsync(input, new ConfigStore(cfgPath), store, sessions, CancellationToken.None);
        await WaitUntil(() => store.Snapshot().Count == 1, "the hook never offered the question");
        Assert.True(store.Decide(store.Snapshot()[0].Id, "answer", "Route A"));
        Assert.Null(await hook); // no output: Claude Code asks in the terminal as usual
    }
}
