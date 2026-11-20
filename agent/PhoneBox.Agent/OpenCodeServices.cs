using System.Diagnostics;
using System.IO;

namespace PhoneBox.Agent;

/// <summary>
/// Where OpenCode's background-service announcements (~/.local/state/opencode/service.json) are watched. Besides the
/// native one, OpenCode inside WSL is served exactly like the native one (sessions, permission prompts, agent
/// questions) — but only for distros that are already running, and only while the user turned WSL watching on: a
/// stopped distro must never be started for a check. Every WSL discovery round therefore asks `wsl.exe --list
/// --running` first — a query that does not touch the VM — and enumerates the shares only for the roots it just
/// returned; no cached membership ever authorizes touching a share. Callers rate-limit how often a round runs.
/// </summary>
internal static class OpenCodeServices
{
    private const int MaxDistros = 8;   // more WSL distros than anyone runs OpenCode in
    private const int MaxUsers = 8;     // per distro

    /// <summary>
    /// Every service.json path worth watching: the native one first (the file may not exist yet — OpenCode writes it
    /// when it starts), then each running distro's users'. The WSL roots come from the caller in tests; null means
    /// a fresh <see cref="ScanRunningDistroRoots"/> round, and <paramref name="wslEnabled"/> turns the WSL half off.
    /// </summary>
    internal static List<string> Discover(string? homeDir = null, string? xdgStateHome = null, string? localAppData = null,
        IReadOnlyList<string>? wslDistroRoots = null, bool wslEnabled = true)
    {
        var paths = new List<string>();
        var native = OpenCodeApprovals.ServicePath(homeDir, xdgStateHome, localAppData);
        if (native is not null) paths.Add(native);
        if (!wslEnabled) return paths;
        var roots = wslDistroRoots ?? ScanRunningDistroRoots();
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

    /// <summary>
    /// The \\wsl.localhost root of each distro that is running right now, from a fresh `wsl.exe --list --running`:
    /// the answer is only good for the share accesses this same round makes immediately after it, so callers keep
    /// their own cadence instead of reusing roots from an earlier round — a distro may have stopped since.
    /// </summary>
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
            // Drained asynchronously, so a wsl.exe that hangs with stdout open cannot block past the deadline
            // below — a synchronous ReadToEnd would wait forever before the timeout even runs. A complete
            // --list --running answer is a few hundred bytes and lands long before the deadline; whatever has
            // arrived by then is what gets parsed.
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                read.Wait(1000); // let the reader finish so the handles close; the output is not used
                return null;
            }
            return read.GetAwaiter().GetResult(); // the process has exited: the pipe closes and the read completes
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
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
