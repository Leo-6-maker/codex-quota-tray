using System.Text;
using System.Text.Json;

namespace CodexQuotaTray;

internal sealed record QuotaWindow(string Bucket, int Minutes, double Remaining, long? Reset)
{
    public string Label => Minutes == 10080 ? "W" : Minutes > 0 && Minutes % 60 == 0 ? $"{Minutes / 60}h" : Minutes > 0 ? $"{Minutes}m" : "窗口";
    public bool Expired => Reset is long value && value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public string Countdown => Reset is null ? "--" : FormatTime(Reset.Value, DateTimeOffset.UtcNow);
    public static string FormatTime(long reset, DateTimeOffset now)
    {
        var mins = Math.Max(0, (long)Math.Ceiling((reset - now.ToUnixTimeSeconds()) / 60d));
        return mins >= 1440 ? $"{mins / 1440}d{mins % 1440 / 60}h" : $"{mins / 60}h{mins % 60:00}m";
    }
    public static List<QuotaWindow> Parse(JsonElement result)
    {
        var windows = new List<QuotaWindow>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any())
        {
            foreach (var bucket in map.EnumerateObject()) ReadBucket(bucket.Name, bucket.Value, windows);
        }
        else if (result.TryGetProperty("rateLimits", out var fallback)) ReadBucket("codex", fallback, windows);
        return windows.OrderBy(w => w.Bucket != "codex").ThenBy(w => w.Bucket).ThenBy(w => w.Minutes).ToList();
    }
    static void ReadBucket(string id, JsonElement bucket, List<QuotaWindow> windows)
    {
        if (bucket.ValueKind != JsonValueKind.Object) return;
        foreach (var field in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(field, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
            var minutes = w.TryGetProperty("windowDurationMins", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var duration) && duration > 0 ? duration : 0;
            long? reset = w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var timestamp) && timestamp >= 0 && timestamp <= 253402300799 ? timestamp : null;
            windows.Add(new(id, minutes, Math.Clamp(100 - percent, 0, 100), reset));
        }
    }
}

internal sealed class AccountState
{
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Plan { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public List<QuotaWindow> Windows { get; set; } = [];
    public HashSet<string> Alerted { get; set; } = [];
    public string Status { get; set; } = "尚未登录";
    public bool Fresh { get; set; }
    public bool Stale => !Fresh || Updated is null || DateTimeOffset.UtcNow - Updated > TimeSpan.FromMinutes(5) || Windows.Any(w => w.Expired);
    public List<QuotaWindow> Summary => Windows.Where(w => w.Bucket == "codex").Any() ? Windows.Where(w => w.Bucket == "codex").ToList() : Windows.GroupBy(w => w.Bucket).FirstOrDefault()?.ToList() ?? [];
    public QuotaWindow? ShortWindow => Summary.FirstOrDefault(w => w.Minutes > 0 && w.Minutes < 10080);
    public QuotaWindow? WeekWindow => Summary.FirstOrDefault(w => w.Minutes == 10080);
    public string Compact(bool active)
    {
        var parts = Summary.Select(w => $"{w.Label}:{w.Remaining:0}%");
        var reset = Summary.FirstOrDefault(w => w.Minutes > 0 && w.Minutes < 10080)?.Countdown;
        var prefix = Name + (active ? "*" : "");
        return Windows.Count == 0 ? $"{prefix} {Status}" : $"{prefix} {string.Join(" ", parts)}" + (reset is null ? "" : $" ↻{reset}") + (Stale ? " [过期]" : "");
    }
}

internal sealed class SavedState
{
    public List<AccountState> Accounts { get; set; } = [new() { Name = "GPT-A" }, new() { Name = "GPT-B" }];
    public bool AlertsEnabled { get; set; } = true;
    public bool WidgetEnabled { get; set; } = true;
}

internal static class Display
{
    public static string Tooltip(IEnumerable<AccountState> accounts, string? active) => Limit("剩余额度\n" + string.Join("\n", accounts.Select(a => a.Compact(active is not null && a.Email == active))), 127);
    public static string Limit(string value, int max)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > max) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
    public static string? ReadActiveEmail()
    {
        // ponytail: read only the email claim; OS-keyring-only development logins show no active badge.
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var path = Path.Combine(home, "auth.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            using var auth = JsonDocument.Parse(File.ReadAllText(path));
            var payload = auth.RootElement.GetProperty("tokens").GetProperty("id_token").GetString()!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return claims.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch { return null; }
    }
}
