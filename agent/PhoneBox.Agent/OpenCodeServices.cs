using System.IO;

namespace PhoneBox.Agent;

/// <summary>
/// Where OpenCode's background-service announcements (~/.local/state/opencode/service.json) are watched. Besides the
/// native one, every RUNNING WSL distro's user homes are probed over the \\wsl.localhost share, so OpenCode inside
/// WSL is served exactly like the native one (sessions, permission prompts, agent questions). Stopped distros are
/// never probed: merely statting a path under \\wsl.localhost would start the whole WSL VM just for a file check.
/// </summary>
internal static class OpenCodeServices
{
    private const int MaxDistros = 8;   // more WSL distros than anyone runs OpenCode in
    private const int MaxUsers = 8;     // per distro

    /// <summary>
    /// Every service.json path worth watching: the native one first (the file may not exist yet — OpenCode writes it
    /// when it starts), then each running distro's users'. The WSL roots come from the caller in tests; null means
    /// the registry, and <paramref name="wslEnabled"/> turns the whole WSL half off.
    /// </summary>
    internal static List<string> Discover(string? homeDir = null, string? xdgStateHome = null, string? localAppData = null,
        IReadOnlyList<string>? wslDistroRoots = null, bool wslEnabled = true)
    {
        var paths = new List<string>();
        var native = OpenCodeApprovals.ServicePath(homeDir, xdgStateHome, localAppData);
        if (native is not null) paths.Add(native);
        if (!wslEnabled) return paths;
        var roots = wslDistroRoots ?? RunningWslDistroRoots();
        foreach (var root in roots.Take(MaxDistros))
        {
            try
            {
                // the distro's users' homes; a stopped or half-awake share throws, which just means "not this round"
                foreach (var user in Directory.EnumerateDirectories(Path.Combine(root, "home")).Take(MaxUsers))
                {
                    var path = Path.Combine(user, ".local", "state", "opencode", "service.json");
                    if (File.Exists(path)) paths.Add(path);
                }
                var rootUser = Path.Combine(root, "root", ".local", "state", "opencode", "service.json");
                if (File.Exists(rootUser)) paths.Add(rootUser);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return paths;
    }

    /// <summary>The \\wsl.localhost root of each RUNNING distro (WSL keeps that in the registry's Lxss key).</summary>
    internal static string[] RunningWslDistroRoots()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return [];
            var roots = new List<string>();
            foreach (var guid in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(guid);
                var name = key?.GetValue("DistributionName") as string;
                var state = key?.GetValue("State") as int? ?? 0;
                if (string.IsNullOrEmpty(name) || state != 1) continue; // 1 = running; stopped distros stay asleep
                roots.Add(@"\\wsl.localhost\" + name);
                if (roots.Count >= MaxDistros) break;
            }
            return [.. roots];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return []; // no WSL, or the registry said no
        }
#else
        return [];
#endif
    }
}
