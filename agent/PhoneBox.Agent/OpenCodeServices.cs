using System.Diagnostics;
using System.IO;

namespace PhoneBox.Agent;

/// <summary>
/// Where OpenCode's background-service announcements (~/.local/state/opencode/service.json) are watched. Besides the
/// native one, OpenCode inside WSL is served exactly like the native one (sessions, permission prompts, agent
/// questions) — but only for distros that are already running, and only while the user turned WSL watching on: a
/// stopped distro must never be started for a file check, and merely statting a path under \\wsl.localhost would do
/// exactly that. Which distros are running is therefore asked with `wsl.exe --list --running`, a query that does not
/// touch the VM, and the answer is cached for a minute — the registry's State flag would not do, since it says
/// "installed", not "running".
/// </summary>
internal static class OpenCodeServices
{
    private const int MaxDistros = 8;   // more WSL distros than anyone runs OpenCode in
    private const int MaxUsers = 8;     // per distro
    private const int CacheMs = 60_000; // how long the running-distro list is trusted

    private static long _nextScan = -CacheMs; // the first ask goes out at once
    private static string[] _cachedRoots = [];

    /// <summary>
    /// Every service.json path worth watching: the native one first (the file may not exist yet — OpenCode writes it
    /// when it starts), then each running distro's users'. The WSL roots come from the caller in tests; null means
    /// <see cref="RunningWslDistroRoots"/>, and <paramref name="wslEnabled"/> turns the whole WSL half off.
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
                // the distro's users' homes; a half-awake share throws, which just means "not this round"
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

    /// <summary>The \\wsl.localhost root of each running distro, cached for a minute so the query stays rare.</summary>
    internal static string[] RunningWslDistroRoots()
    {
        if (Environment.TickCount64 < _nextScan) return _cachedRoots;
        _nextScan = Environment.TickCount64 + CacheMs;
        _cachedRoots = ScanRunningDistroRoots();
        return _cachedRoots;
    }

    internal static string[] ScanRunningDistroRoots()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var roots = new List<string>();
        foreach (var name in ParseRunningDistros(RunningListOutput()).Take(MaxDistros))
            roots.Add(@"\\wsl.localhost\" + name);
        return [.. roots];
    }

    /// <summary>stdout of `wsl.exe --list --running`, or null when the query could not run (no WSL, no wsl.exe).</summary>
    private static string? RunningListOutput()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                ArgumentList = { "--list", "--running" }, // a query: it never starts the VM or a distro
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd(); // wsl.exe pipes UTF-16; ParseRunningDistros drops the NULs
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (Exception) { } return null; }
            return output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // no WSL installed, or the query could not start
        }
    }

    /// <summary>The distro names in `wsl.exe` list output: NULs (piped UTF-16) and header/footer lines dropped,
    /// a " (Default)" marker stripped. Anything unparseable may survive as a name — the share lookup for it simply
    /// finds nothing, which is handled like any other distro without OpenCode.</summary>
    internal static List<string> ParseRunningDistros(string? output)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(output)) return names;
        foreach (var raw in output.Replace("\0", "").Split('\r', '\n'))
        {
            var name = raw.Trim();
            if (name.Length == 0 || name.Contains(':')) continue; // "Windows Subsystem for Linux Distributions:"
            var cut = name.IndexOf(" (Default)", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) name = name[..cut].Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }
}
