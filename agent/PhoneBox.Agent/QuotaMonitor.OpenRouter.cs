using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// OpenRouter quota from openrouter.ai/api/v1 (credits + the key's own endpoint), with the API key pasted in
/// Settings › General › AI quotas (stored only in config.json on this computer), or failing that the
/// OPENROUTER_API_KEY environment variable or OpenCode's legacy auth.json. OpenCode's sessions and prompts come from
/// the same local API as the native OpenCode. Several caps can bind at once,
/// so the ring shows whichever leaves the least: the account's prepaid credits, the workspace's budget guardrail
/// (which resets — its reset date and pace show on the ring) or the key's own spending cap. The dollars still left
/// go into the card's footer. Only the balance numbers leave this computer, and only to openrouter.ai; the key
/// itself is never logged, stored, cached or sent anywhere else.
/// </summary>
public sealed partial class QuotaMonitor
{
    /// <summary>openrouter.ai serves the credits endpoint cached for up to a minute; a slower pace changes nothing.</summary>
    internal const long OpenRouterHealthyMs = 180_000;
    private const long OpenRouterRetryMs = 60_000;

    private volatile QuotaProvider? _openrouter;
    private long _nextOpenRouter;
    private string? _openrouterState; // the last situation announced to the log; announcements only on change

    /// <summary>Announces a change of situation once (finding nothing, an error, a reading), never every pass.</summary>
    private void Announce(string state, string message)
    {
        if (_openrouterState == state) return;
        _openrouterState = state;
        log.LogInformation("openrouter quota: {Message}", message);
    }

    /// <summary>The key lookup. Tests hand one of their own, so nothing here reads a real key file.</summary>
    internal Func<string?>? ReadOpenRouterKey { get; init; }

    /// <summary>The legacy OpenCode auth.json, for tests. Null means the real one under the user profile.</summary>
    internal string? AuthJsonPath { get; init; }

    /// <summary>OpenRouter is polled only when the settings' tracked services include it (it is off by default).</summary>
    private bool OpenRouterEnabled => config?.Get().CodexBar.Providers.Contains("openrouter") == true;

    /// <summary>One pass of the usage loop for OpenRouter, at wall-clock time <paramref name="nowMs"/>. Never throws.</summary>
    internal async Task OpenRouterTickAsync(long nowMs, CancellationToken ct)
    {
        if (!OpenRouterEnabled)
        {
            _openrouter = null;
            Announce("off", "off: not ticked under the tracked AI services");
            return;
        }
        if (nowMs < _nextOpenRouter) return;
        _nextOpenRouter = nowMs + OpenRouterRetryMs; // every path below leaves a sensible wait, success moves it ahead
        var key = ReadOpenRouterKey is { } read ? read() : FindApiKey();
        if (string.IsNullOrEmpty(key))
        {
            _openrouter = null; // not set up on this computer: quietly absent, like Claude without a sign-in
            _nextOpenRouter = nowMs + OpenRouterHealthyMs;
            Announce("nokey", $"no API key found (Settings › General › AI quotas, OPENROUTER_API_KEY, legacy auth.json)");
            return;
        }
        try
        {
            _openrouter = await FetchOpenRouterAsync(key, nowMs, ct);
            if (_openrouter is { Error: null, Limits.Count: > 0 })
            {
                _nextOpenRouter = nowMs + OpenRouterHealthyMs;
                Announce("ok", $"cap: {_openrouter.Extra}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            log.LogDebug("openrouter quota: {Msg}", ex.Message); // usually a computer that just woke up: try again soon
            Announce("unreachable", "cannot reach openrouter.ai: " + ex.Message);
        }
        catch (Exception ex)
        {
            log.LogDebug("openrouter quota: {Msg}", ex.Message);
            _openrouter = KeepOpenRouter("Could not fetch quota: " + ex.Message);
            Announce("error", _openrouter.Error!);
        }
    }

    /// <summary>
    /// One spend cap worth showing. The ring becomes the one that leaves the least: the account's prepaid credits,
    /// the workspace's budget guardrail (which resets), or the key's own spending cap.
    /// </summary>
    internal sealed record OpenRouterCap(string Label, double Total, double Remaining, long? ResetsAt, long? WindowSec);

    private async Task<QuotaProvider> FetchOpenRouterAsync(string key, long nowMs, CancellationToken ct)
    {
        var caps = new List<OpenRouterCap>();
        var problems = new List<string>();
        var creditsOk = false;

        // the account's prepaid credits: total top-ups minus everything spent with every key
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/credits");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await Http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var data = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d)
                    ? d : doc.RootElement;
                if (Num(data, "total_credits") is { } total)
                {
                    creditsOk = true;
                    if (total > 0) caps.Add(new OpenRouterCap("Credits", total, Math.Max(0, total - (Num(data, "total_usage") ?? 0)), null, null));
                }
            }
            else problems.Add($"credits HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { problems.Add("credits reply unrecognized"); }

        // the key's own endpoint: its spending cap (when it has one) and the workspace budget guardrail that governs it
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await Http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var data = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var d)
                    ? d : doc.RootElement;
                if (Num(data, "limit") is { } limit && limit > 0)
                {
                    var remaining = Num(data, "limit_remaining") ?? Math.Max(0, limit - (Num(data, "usage") ?? 0));
                    caps.Add(new OpenRouterCap("Key limit", limit, Math.Max(0, remaining), null, null));
                }
                if (data.TryGetProperty("effective_budget", out var b) && b.ValueKind == JsonValueKind.Object
                    && Num(b, "limit_usd") is { } budget && budget > 0)
                {
                    var remaining = Num(b, "remaining_usd") ?? Math.Max(0, budget - (Num(b, "spend_usd") ?? 0));
                    var interval = Str(b, "reset_interval");
                    caps.Add(new OpenRouterCap(BudgetLabel(interval), budget, Math.Max(0, remaining),
                        Iso(Str(b, "resets_at")), BudgetWindow(interval)));
                }
            }
            else problems.Add($"key HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { problems.Add("key reply unrecognized"); }

        if (caps.Count > 0) return OpenRouterReading(nowMs, caps.MinBy(c => c.Remaining)!);
        if (creditsOk) return new QuotaProvider("openrouter", "OpenRouter", "Prepaid", [], null, nowMs, "No credits on this account");
        return problems.Count > 0 ? KeepOpenRouter("Could not fetch quota: " + string.Join("; ", problems))
            : KeepOpenRouter("OpenRouter sent a quota reply this version doesn't recognize");
    }

    private static string BudgetLabel(string? interval) => interval?.ToLowerInvariant() switch
    {
        "daily" => "Daily budget",
        "weekly" => "Weekly budget",
        "monthly" => "Monthly budget",
        "yearly" or "annual" => "Yearly budget",
        _ => "Budget",
    };

    /// <summary>The budget's window length, so the ring can draw its pace marker against the reset.</summary>
    private static long? BudgetWindow(string? interval) => interval?.ToLowerInvariant() switch
    {
        "daily" => 86_400,
        "weekly" => 7 * 86_400,
        "monthly" => 30 * 86_400,
        "yearly" or "annual" => 365 * 86_400,
        _ => null,
    };

    /// <summary>The binding cap as the ring: the share of it spent, and what is still allowed, in the footer.</summary>
    private static QuotaProvider OpenRouterReading(long nowMs, OpenRouterCap cap)
    {
        var pct = Math.Clamp((cap.Total - cap.Remaining) / cap.Total * 100, 0, 100);
        var share = cap.Remaining / cap.Total;
        var severity = share <= 0.10 ? "critical" : share <= 0.25 ? "warning" : "normal";
        var limit = new QuotaLimit(cap.Label, pct, cap.ResetsAt, cap.WindowSec, severity);
        var extra = string.Create(CultureInfo.InvariantCulture, $"{Dollars(cap.Remaining)} of {Dollars(cap.Total)} left");
        return new QuotaProvider("openrouter", "OpenRouter", "Prepaid", [limit], extra, nowMs, null);
    }

    private static string Dollars(double value) => "$" + value.ToString("#,0.00", CultureInfo.InvariantCulture);

    /// <summary>On errors, keeps the last good values and attaches the error.</summary>
    private QuotaProvider KeepOpenRouter(string error) =>
        _openrouter is { Limits.Count: > 0 } prev ? prev with { Error = error }
            : Fail("openrouter", "OpenRouter", error);

    private static string AuthJson() => Path.Combine(Home, ".local", "share", "opencode", "auth.json");

    /// <summary>
    /// The OpenRouter API key of this computer, or null: the one pasted in Settings › General › AI quotas first, then
    /// the OPENROUTER_API_KEY environment variable, then the auth.json OpenCode kept before its v2.
    /// </summary>
    private string? FindApiKey()
    {
        var fromSettings = config?.Get().OpenRouter.ApiKey;
        if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings;
        var auth = AuthJsonPath ?? AuthJson();
        try
        {
            if (File.Exists(auth) && KeyFromCredential(File.ReadAllText(auth)) is { } fromFile) return fromFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug("openrouter key: {Msg}", ex.Message);
        }
        var env = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>
    /// The key inside an auth.json: {"type":"api","key":"sk-or-v1-…"} at the top, under an "openrouter" key, or a
    /// raw key string. A non-key credential (OAuth) is not an OpenRouter key.
    /// </summary>
    internal static string? KeyFromCredential(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var text = json.Trim();
        if (!text.StartsWith('{')) return text.StartsWith("sk-or-", StringComparison.Ordinal) ? text : null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var node = root.TryGetProperty("openrouter", out var or) && or.ValueKind == JsonValueKind.Object ? or : root;
            if (Str(node, "type") is { } type && type is not ("api" or "key")) return null;
            return Str(node, "key") is { Length: > 0 } key ? key.Trim() : null;
        }
        catch (JsonException) { return null; }
    }
}
