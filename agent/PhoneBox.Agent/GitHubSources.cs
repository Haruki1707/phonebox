using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>One GitHub CLI install: the Windows one, or one inside a running WSL distro.</summary>
internal sealed record GhSource(string Id, string Name, string? Distro, string? GhPath)
{
    public bool IsWsl => Distro is not null;
}

/// <summary>A running distro and the Linux gh it has, if any.</summary>
internal sealed record GhWslInspect(string Distro, string? GhPath);

/// <summary>What the settings page shows: the Windows install and every running WSL distro it could find.</summary>
internal sealed record GhInspect(string? WindowsGh, IReadOnlyList<GhWslInspect> Distros);

/// <summary>
/// Finds the GitHub CLI where the widget may read it. WSL is only touched for distros that are already running (the
/// registry's Lxss state), so a stopped distro is never woken merely to look for gh. Inside a distro the user's login
/// shell resolves gh, then queries run the resolved Linux binary directly: no profile output can leak into the JSON.
/// </summary>
internal static class GitHubSources
{
    private const int MaxDistros = 8;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15); // a cold WSL VM takes a few seconds
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(false);
    private static readonly Lock CacheGate = new();
    private static (string Key, DateTime At, List<GhSource> Sources) _cache;

    /// <summary>Cheap enough for a control-deck description: no process is started, only PATH and the registry.</summary>
    internal static bool LikelyAvailable(GitHubConfig cfg)
    {
        if (cfg.Mode is not "wsl" && ToolProcess.Find("gh") is not null) return true;
        return cfg.Mode is not "windows" && OperatingSystem.IsWindows() && RunningWslDistros(cfg.WslDistro).Length > 0;
    }

    /// <summary>The sources the monitor should poll. <paramref name="all"/> is for the settings page: it ignores the
    /// mode and the chosen distro so the user can see what is installed before switching to it.</summary>
    internal static async Task<GhInspect> InspectAsync(GitHubConfig cfg, bool all, CancellationToken ct)
    {
        var windows = ToolProcess.Find("gh");
        IReadOnlyList<GhWslInspect> distros = [];
        if ((all || cfg.Mode is not "windows") && OperatingSystem.IsWindows() && WslExe() is { } wsl)
        {
            // a stopped distro is never in this list: WSL's registry keeps its state, and starting one to look for gh
            // would bring up the whole WSL VM just for the check
            var names = RunningWslDistros(all ? "" : cfg.WslDistro);
            distros = await Task.WhenAll(names.Select(async name => new GhWslInspect(name, await ProbeGh(wsl, name, ct))));
        }
        return new GhInspect(windows, distros);
    }

    internal static async Task<List<GhSource>> DiscoverAsync(GitHubConfig cfg, CancellationToken ct)
    {
        // Probing a distro starts a short-lived wsl.exe per running distro. The installs change rarely, so the answer is
        // kept for a few minutes: the three-minute poll then probes at most once per five minutes, and almost never.
        var key = cfg.Mode + "|" + cfg.WslDistro;
        lock (CacheGate)
            if (_cache.Key == key && DateTime.UtcNow - _cache.At < CacheFor) return _cache.Sources;

        var sources = await DiscoverUncachedAsync(cfg, ct);
        // only a real answer is cached: an empty one may be a distro that was still waking up, and retrying it on the
        // next poll (three minutes later) is cheaper than hiding gh for five
        if (sources.Count > 0) lock (CacheGate) _cache = (key, DateTime.UtcNow, sources);
        return sources;
    }

    private static async Task<List<GhSource>> DiscoverUncachedAsync(GitHubConfig cfg, CancellationToken ct)
    {
        var inspect = await InspectAsync(cfg, all: false, ct);
        var available = new List<GhSource>();
        if (inspect.WindowsGh is not null) available.Add(new GhSource("windows", "Windows", null, inspect.WindowsGh));
        foreach (var d in inspect.Distros.Where(d => d.GhPath is not null))
            available.Add(new GhSource("wsl:" + d.Distro, "WSL · " + d.Distro, d.Distro, d.GhPath));
        return Select(available, cfg);
    }

    /// <summary>What Settings › GitHub shows: the Windows install and every running distro with whether gh was found.</summary>
    internal static object Json(GhInspect inspect) => new
    {
        windows = new { name = "Windows", available = inspect.WindowsGh is not null },
        distros = inspect.Distros.OrderBy(d => d.Distro, StringComparer.OrdinalIgnoreCase)
            .Select(d => new { name = d.Distro, available = d.GhPath is not null }),
    };

    /// <summary>Applies Settings › GitHub to the installs that were found.</summary>
    internal static List<GhSource> Select(IEnumerable<GhSource> available, GitHubConfig cfg)
    {
        var sources = available.Where(s => cfg.Mode switch
        {
            "windows" => !s.IsWsl,
            "wsl" => s.IsWsl,
            _ => true,
        });
        if (cfg.Mode is not "windows" && !string.IsNullOrWhiteSpace(cfg.WslDistro))
            sources = sources.Where(s => !s.IsWsl || string.Equals(s.Distro, cfg.WslDistro, StringComparison.OrdinalIgnoreCase));
        return sources.ToList();
    }

    /// <summary>The Linux gh path from a login shell's <c>command -v gh</c>, or null when it is missing or is just the
    /// Windows gh reached through /mnt (which the Windows source already reads).</summary>
    internal static string? LinuxGhPath(string output)
    {
        foreach (var line in output.Split('\n').Reverse())
        {
            var path = line.Trim();
            if (path.Length == 0 || path[0] != '/' || path.StartsWith("/mnt/", StringComparison.Ordinal)) continue;
            if (path.EndsWith("/gh", StringComparison.Ordinal)) return path;
        }
        return null;
    }

    /// <summary>The \\wsl.localhost name of each running distro, optionally narrowed to one, from WSL's registry key.</summary>
    internal static string[] RunningWslDistros(string? only = null)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return [];
            var names = new List<string>();
            foreach (var guid in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(guid);
                if (key?.GetValue("DistributionName") is not string name || name.Length == 0) continue;
                if (key.GetValue("State") is not int state || state != 1) continue; // 1 = running
                if (!string.IsNullOrWhiteSpace(only) && !string.Equals(name, only, StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(name);
                if (names.Count >= MaxDistros) break;
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return [.. names];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
#else
        _ = only;
        return [];
#endif
    }

    internal static string? WslExe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (ToolProcess.Find("wsl") is { } found) return found;
        var system = Environment.SystemDirectory;
        if (string.IsNullOrEmpty(system)) return null;
        var path = Path.Combine(system, "wsl.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>A gh invocation: the Windows binary, or wsl.exe running the Linux binary without a shell.</summary>
    internal static ProcessStartInfo Start(GhSource source, string[] args)
    {
        ProcessStartInfo psi;
        if (!source.IsWsl)
        {
            psi = new ProcessStartInfo(source.GhPath ?? "gh");
        }
        else
        {
            var wsl = WslExe() ?? throw new InvalidOperationException("wsl.exe was not found");
            psi = new ProcessStartInfo(wsl);
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(source.Distro!);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(source.GhPath ?? "gh");
            // wsl.exe writes its own messages as UTF-8, like the Linux gh does.
            psi.Environment["WSL_UTF8"] = "1";
        }
        // gh answers in UTF-8 whatever the console code page is, and pull request titles are not ASCII
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Utf8;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    private static async Task<string?> ProbeGh(string wsl, string distro, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(wsl);
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add("command -v gh 2>/dev/null | tail -n 1");
            psi.Environment["WSL_UTF8"] = "1";
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = Utf8;
            var result = await ToolProcess.RunAsync(psi, ProbeTimeout, ct);
            return result.ExitCode == 0 ? LinuxGhPath(result.Output) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
