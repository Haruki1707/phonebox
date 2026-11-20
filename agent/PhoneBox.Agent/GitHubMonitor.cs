using System.Diagnostics;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record GhPr(string Repo, int Number, string Title, string? Author, bool Draft, string? Review, string? Ci, long Updated);

/// <summary>One GitHub account as seen by one source (the Windows gh, or a WSL distro's).</summary>
public sealed record GhAccount(string Login, string Source, string Name);

public sealed record GhSnapshot(string? User, int Notifications, IReadOnlyList<GhPr> ReviewRequests, IReadOnlyList<GhPr> MyPrs,
    long? UpdatedAt, string? Error)
{
    /// <summary>Every account that answered; the same login may appear once per source.</summary>
    public IReadOnlyList<GhAccount> Accounts { get; init; } = [];
    /// <summary>Problems from individual sources while at least one still answered.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Collects a pull request and notification summary through the GitHub CLI (gh) every 3 minutes.
/// Settings › GitHub chooses Windows, WSL, or both: when both are available the answer is merged, with the same
/// pull request or notification thread counted once even though both installs signed in to the same account.</summary>
public sealed class GitHubMonitor(ILogger<GitHubMonitor> log, ConfigStore? config = null) : BackgroundService
{
    private const string Query = """
        query {
          viewer {
            login
            pullRequests(states: OPEN, first: 8, orderBy: {field: UPDATED_AT, direction: DESC}) {
              nodes {
                number title isDraft reviewDecision updatedAt
                repository { nameWithOwner }
                commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }
              }
            }
          }
          search(query: "is:pr is:open archived:false review-requested:@me", type: ISSUE, first: 8) {
            nodes {
              ... on PullRequest { number title isDraft updatedAt author { login } repository { nameWithOwner } }
            }
          }
        }
        """;

    private volatile GhSnapshot? _snap;
    public GhSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        while (!ct.IsCancellationRequested)
        {
            try { _snap = await Fetch(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogDebug("github: {Msg}", ex.Message);
                _snap = (_snap ?? new GhSnapshot(null, 0, [], [], null, null)) with { Error = ex.Message };
            }
            await Wake.Delay(TimeSpan.FromMinutes(3), ct);
        }
    }

    private async Task<GhSnapshot> Fetch(CancellationToken ct)
    {
        var cfg = config?.Get().GitHub ?? new GitHubConfig();
        var sources = await GitHubSources.DiscoverAsync(cfg, ct);
        if (sources.Count == 0)
            throw new InvalidOperationException(cfg.Mode == "wsl"
                ? "No running WSL distro has GitHub CLI; install gh and sign in inside WSL"
                : "Install and sign in to GitHub CLI, on Windows or in WSL");

        var results = await Task.WhenAll(sources.Select(source => FetchSource(source, ct)));
        return Merge(results, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static async Task<GhSourceResult> FetchSource(GhSource source, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await RunSourceGh(source, ["api", "graphql", "-f", "query=" + Query], ct));
            var data = doc.RootElement.GetProperty("data");
            var viewer = data.GetProperty("viewer");

            var mine = viewer.GetProperty("pullRequests").GetProperty("nodes").EnumerateArray().Select(n =>
            {
                string? ci = null;
                var commits = n.GetProperty("commits").GetProperty("nodes");
                if (commits.GetArrayLength() > 0 && commits[0].GetProperty("commit").GetProperty("statusCheckRollup") is { ValueKind: JsonValueKind.Object } r)
                    ci = r.GetProperty("state").GetString();
                return Pr(n, null, ci, Str(n, "reviewDecision"));
            }).ToList();

            var reviews = data.GetProperty("search").GetProperty("nodes").EnumerateArray()
                .Where(n => n.TryGetProperty("number", out _))
                .Select(n => Pr(n, n.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : null, null, null))
                .ToList();

            var ids = new List<string>();
            try
            {
                using var nd = JsonDocument.Parse(await RunSourceGh(source, ["api", "notifications?per_page=50"], ct));
                foreach (var n in nd.RootElement.EnumerateArray())
                {
                    if (n.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString()!);
                    else ids.Add(source.Id + ":" + ids.Count); // no id: at least keep this source's own items apart
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }

            return new(source, Str(viewer, "login"), ids, reviews, mine, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(source, null, [], [], [], ex.Message); }
    }

    /// <summary>Merges the sources' answers. Pull requests are keyed by repository and number, notification threads by
    /// their GitHub id, so the same account signed in on Windows and in WSL is not counted twice.</summary>
    internal static GhSnapshot Merge(IReadOnlyList<GhSourceResult> results, long updatedAt)
    {
        var answered = results.Where(r => r.Error is null).ToList();
        var warnings = results.Where(r => r.Error is not null)
            .Select(r => r.Source.Name + ": " + r.Error!.Trim().Split('\n')[0]).ToList();
        var accounts = answered.Where(r => !string.IsNullOrWhiteSpace(r.User))
            .Select(r => new GhAccount(r.User!, r.Source.Id, r.Source.Name)).ToList();

        if (answered.Count == 0)
            return new GhSnapshot(null, 0, [], [], updatedAt, warnings.Count > 0 ? string.Join(" · ", warnings) : "No GitHub CLI source is available")
            { Warnings = warnings };

        var notifications = answered.SelectMany(r => r.NotificationIds).Distinct(StringComparer.Ordinal).Count();
        var reviews = Dedupe(answered.SelectMany(r => r.ReviewRequests), review: true);
        var mine = Dedupe(answered.SelectMany(r => r.MyPrs), review: false);
        // a source that failed is a warning, not the widget's error line: whatever the other sources said still shows
        return new GhSnapshot(accounts.Select(a => a.Login).Distinct(StringComparer.Ordinal).FirstOrDefault(), notifications,
            reviews, mine, updatedAt, null)
        {
            Accounts = accounts,
            Warnings = warnings,
        };
    }

    private static List<GhPr> Dedupe(IEnumerable<GhPr> prs, bool review) => prs
        .GroupBy(p => p.Repo + "#" + p.Number, StringComparer.OrdinalIgnoreCase)
        .Select(g => review
            ? g.OrderByDescending(p => p.Author is not null).ThenByDescending(p => p.Updated).First()
            : g.OrderByDescending(p => p.Updated).ThenByDescending(p => p.Ci is not null).First())
        .OrderByDescending(p => p.Updated)
        .ToList();

    private static GhPr Pr(JsonElement n, string? author, string? ci, string? review) => new(
        n.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
        n.GetProperty("number").GetInt32(),
        Str(n, "title") ?? "",
        author,
        n.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
        review, ci,
        DateTimeOffset.TryParse(Str(n, "updatedAt"), out var u) ? u.ToUnixTimeMilliseconds() : 0);

    internal static Task<string> Gh(string[] args, CancellationToken ct) =>
        RunGh(StartWindows(args), ct);

    internal static Task<string> RunSourceGh(GhSource source, string[] args, CancellationToken ct) =>
        RunGh(GitHubSources.Start(source, args), ct);

    private static ProcessStartInfo StartWindows(string[] args)
    {
        var psi = new ProcessStartInfo("gh");
        // gh answers in UTF-8; pull request titles are not ASCII
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = new System.Text.UTF8Encoding(false);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    internal static async Task<string> RunGh(ProcessStartInfo psi, CancellationToken ct)
    {
        var wsl = psi.FileName.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase);
        var result = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(wsl ? 40 : 20), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("gh: " + result.Error.Trim().Split('\n')[0]);
        return result.Output;
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What one source answered, before the merge.</summary>
internal sealed record GhSourceResult(GhSource Source, string? User, IReadOnlyList<string> NotificationIds,
    IReadOnlyList<GhPr> ReviewRequests, IReadOnlyList<GhPr> MyPrs, string? Error)
{
    public int Notifications => NotificationIds.Count;
}
