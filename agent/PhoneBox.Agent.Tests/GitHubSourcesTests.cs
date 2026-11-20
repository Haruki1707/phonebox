using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Settings › GitHub can read the GitHub CLI on Windows, in running WSL distros, or both. These tests cover the two
/// halves that do not need a real gh: choosing the installs from the setting, and merging what they answered.
/// </summary>
public sealed class GitHubSourcesTests
{
    private static GhSource Windows => new("windows", "Windows", null, "/usr/bin/gh");
    private static GhSource Ubuntu => new("wsl:Ubuntu", "WSL · Ubuntu", "Ubuntu", "/usr/bin/gh");
    private static GhSource Debian => new("wsl:Debian", "WSL · Debian", "Debian", "/usr/bin/gh");

    // ------------------------------------------------------------------ choosing the installs

    [Fact]
    public void AutoKeepsBothWindowsAndEveryDistro()
    {
        var picked = GitHubSources.Select([Windows, Ubuntu, Debian], new GitHubConfig { Mode = "auto" });
        Assert.Equal(["windows", "wsl:Ubuntu", "wsl:Debian"], picked.Select(s => s.Id));
    }

    [Fact]
    public void WindowsOnlyLeavesTheDistrosOut()
    {
        var picked = GitHubSources.Select([Windows, Ubuntu], new GitHubConfig { Mode = "windows" });
        Assert.Equal(["windows"], picked.Select(s => s.Id));
    }

    [Fact]
    public void WslOnlyLeavesWindowsOutEvenWhenItIsTheOnlyOneFound()
    {
        Assert.Empty(GitHubSources.Select([Windows], new GitHubConfig { Mode = "wsl" }));
        Assert.Equal(["wsl:Ubuntu"], GitHubSources.Select([Windows, Ubuntu], new GitHubConfig { Mode = "wsl" }).Select(s => s.Id));
    }

    [Fact]
    public void AChosenDistroNarrowsTheMergeToThatDistro()
    {
        var picked = GitHubSources.Select([Windows, Ubuntu, Debian], new GitHubConfig { Mode = "auto", WslDistro = "debian" });
        Assert.Equal(["windows", "wsl:Debian"], picked.Select(s => s.Id));
    }

    [Fact]
    public void AChosenDistroThatIsNotRunningLeavesOnlyWindows()
    {
        var picked = GitHubSources.Select([Windows, Ubuntu], new GitHubConfig { Mode = "auto", WslDistro = "Fedora" });
        Assert.Equal(["windows"], picked.Select(s => s.Id));
    }

    // ------------------------------------------------------------------ finding gh inside a distro

    [Fact]
    public void LoginShellPathInsideTheDistroIsUsed()
    {
        Assert.Equal("/home/me/.local/bin/gh", GitHubSources.LinuxGhPath("/home/me/.local/bin/gh\n"));
        Assert.Equal("/snap/bin/gh", GitHubSources.LinuxGhPath("/snap/bin/gh"));
    }

    [Fact]
    public void TheWindowsGhThroughInteropIsNotADistroInstall()
    {
        // /mnt/c/... is the Windows binary the Windows source already reads: counting it would double that install
        Assert.Null(GitHubSources.LinuxGhPath("/mnt/c/Program Files/GitHub CLI/gh\n"));
        Assert.Null(GitHubSources.LinuxGhPath("not found\n"));
        Assert.Null(GitHubSources.LinuxGhPath(""));
    }

    [Fact]
    public void ProfileChatterBeforeThePathIsIgnored()
    {
        // a login shell may print a message first; only the last absolute gh line counts
        Assert.Equal("/usr/bin/gh", GitHubSources.LinuxGhPath("Welcome to Ubuntu!\n$ command -v gh\n/usr/bin/gh\n"));
    }

    // ------------------------------------------------------------------ merging

    private static GhPr Pr(string repo, int number, string title, long updated, string? ci = null, string? author = null) =>
        new(repo, number, title, author, false, null, ci, updated);

    [Fact]
    public void TheSamePullRequestFromBothInstallsIsCountedOnce()
    {
        var snap = GitHubMonitor.Merge(
        [
            new GhSourceResult(Windows, "me", ["n1", "n2"], [], [Pr("org/app", 7, "Fix", 100, "SUCCESS")], null),
            new GhSourceResult(Ubuntu, "me", ["n1", "n2", "n3"], [], [Pr("org/app", 7, "Fix", 200, "FAILURE")], null),
        ], 5);

        Assert.Equal("me", snap.User);
        Assert.Equal(3, snap.Notifications);                                     // n1, n2, n3 across the two
        var pr = Assert.Single(snap.MyPrs);
        Assert.Equal(200, pr.Updated);                                           // the newer answer wins
        Assert.Equal("FAILURE", pr.Ci);
        Assert.Null(snap.Error);
        Assert.Empty(snap.Warnings);
        Assert.Equal(2, snap.Accounts.Count);
    }

    [Fact]
    public void DifferentAccountsKeepEveryoneWithTheirSource()
    {
        var snap = GitHubMonitor.Merge(
        [
            new GhSourceResult(Windows, "work", ["a"], [], [Pr("org/app", 1, "One", 10)], null),
            new GhSourceResult(Ubuntu, "home", ["b"], [], [Pr("org/app", 2, "Two", 20)], null),
        ], 5);

        Assert.Equal(["windows", "wsl:Ubuntu"], snap.Accounts.Select(a => a.Source).Order());
        Assert.Equal(["home", "work"], snap.Accounts.Select(a => a.Login).Order());
        Assert.Equal(2, snap.MyPrs.Count);
    }

    [Fact]
    public void AFailingDistroDoesNotHideTheWindowsAnswer()
    {
        var snap = GitHubMonitor.Merge(
        [
            new GhSourceResult(Windows, "me", [], [], [Pr("org/app", 3, "Mine", 30)], null),
            new GhSourceResult(Ubuntu, null, [], [], [], "gh: not logged in"),
        ], 5);

        Assert.Equal("me", snap.User);
        Assert.Single(snap.MyPrs);
        Assert.Single(snap.Warnings);
        Assert.Contains("WSL · Ubuntu", snap.Warnings[0]);
        Assert.Null(snap.Error);                                                 // the widget keeps showing data
    }

    [Fact]
    public void EverySourceFailingIsAnError()
    {
        var snap = GitHubMonitor.Merge([new GhSourceResult(Windows, null, [], [], [], "gh: not logged in")], 5);

        Assert.Null(snap.User);
        Assert.NotNull(snap.Error);
        Assert.Contains("gh: not logged in", snap.Error);
        Assert.Empty(snap.MyPrs);
    }

    [Fact]
    public void NoSourceAtAllIsAnError()
    {
        var snap = GitHubMonitor.Merge([], 5);
        Assert.NotNull(snap.Error);
        Assert.Empty(snap.Accounts);
        Assert.Empty(snap.Warnings);
    }

    [Fact]
    public void ReviewsFromBothInstallsShowEachRequestOnce()
    {
        var snap = GitHubMonitor.Merge(
        [
            new GhSourceResult(Windows, "me", [], [Pr("org/app", 9, "Please review", 40, author: "alice")], [], null),
            new GhSourceResult(Ubuntu, "me", [], [Pr("org/app", 9, "Please review", 10, author: "alice")], [], null),
        ], 5);

        var review = Assert.Single(snap.ReviewRequests);
        Assert.Equal("alice", review.Author);
        Assert.Equal(40, review.Updated);
    }

    [Fact]
    public void ThePhoneGetsOneRowPerAccountWithItsSource()
    {
        var snap = GitHubMonitor.Merge(
        [
            new GhSourceResult(Windows, "me", [], [], [], null),
            new GhSourceResult(Ubuntu, "me", [], [], [], null),
        ], 5);

        var json = System.Text.Json.JsonSerializer.Serialize(new { accounts = snap.Accounts },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("accounts").EnumerateArray().ToList();
        // both installs signed in as the same account: two rows, each naming where it came from, so the widget can
        // show "@me" once with both sources in its tooltip
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("me", r.GetProperty("login").GetString()));
        Assert.Equal(["windows", "wsl:Ubuntu"], rows.Select(r => r.GetProperty("source").GetString()!).Order());
        Assert.Contains("WSL", rows[1].GetProperty("name").GetString());
    }

    [Fact]
    public void TheSettingsCardListsEveryDistroWithWhetherItHasGh()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            GitHubSources.Json(new GhInspect("/usr/bin/gh", [new GhWslInspect("Ubuntu", "/usr/bin/gh"), new GhWslInspect("Debian", null)])),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("windows").GetProperty("available").GetBoolean());
        var distros = root.GetProperty("distros").EnumerateArray().ToList();
        Assert.Equal(["Debian", "Ubuntu"], distros.Select(d => d.GetProperty("name").GetString()!));
        Assert.False(distros[0].GetProperty("available").GetBoolean());
        Assert.True(distros[1].GetProperty("available").GetBoolean());
    }

    [Fact]
    public void WithoutAConfigStoreTheMonitorAsksForBothSources()
    {
        // the settings default is the merge, so an install that never saved Settings › GitHub behaves like the default
        Assert.Equal("auto", new GitHubConfig().Mode);
        Assert.Equal("", new GitHubConfig().WslDistro);
        Assert.NotNull(new GitHubMonitor(NullLogger<GitHubMonitor>.Instance));
    }

    [Fact]
    public void AnUnknownModeOrASpacedDistroIsNormalized()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-githubcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """{ "github": { "mode": "both", "wslDistro": "  Ubuntu " } }""");
            var cfg = new ConfigStore(path).Get().GitHub;
            Assert.Equal("auto", cfg.Mode);
            Assert.Equal("Ubuntu", cfg.WslDistro);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ThePhoneNeverGetsTheGitHubSourceSettings()
    {
        // the phone only needs the merged list: which installs feed it stays on the computer
        var cfg = new PhoneBox.Agent.AgentConfig
        {
            GitHub = new GitHubConfig { Mode = "wsl", WslDistro = "Ubuntu" },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg.ToPublic(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("wslDistro", json);
        Assert.DoesNotContain("\"github\"", json);
    }
}
