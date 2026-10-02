using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CodexQuotaTray;

internal static class SelfTest
{
    static JsonElement Json(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }
    static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
    public static void Run()
    {
        var old = QuotaWindow.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":66,"windowDurationMins":300,"resetsAt":2000000000},"secondary":{"usedPercent":39,"windowDurationMins":10080}}}"""));
        Check(old.Count == 2 && old[0].Remaining == 34 && old[1].Label == "W", "Remaining / legacy parser");
        var modern = QuotaWindow.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":52,"windowDurationMins":10080},"secondary":null},"other":{"primary":{"usedPercent":20,"windowDurationMins":60}}}}"""));
        Check(modern.Count == 2 && modern[0].Label == "W" && modern[1].Label == "1h", "Multi-bucket / weekly-only window");
        Check(QuotaWindow.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":null},"secondary":{"usedPercent":101,"windowDurationMins":300,"resetsAt":999999999999999}}}""")).Single() is { Remaining: 0, Reset: null }, "Missing value / invalid reset / clamp");
        Check(QuotaWindow.Parse(Json("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":20}}}""")).Single().Remaining == 80, "Empty map fallback");
        Check(QuotaWindow.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":null,"resetsAt":null}}}""")).Single() is { Minutes: 0, Reset: null }, "Nullable window fields");
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        Check(QuotaWindow.FormatTime(1000 + 102 * 60, now) == "1h42m" && QuotaWindow.FormatTime(900, now) == "0h00m", "Reset countdown");
        var state = TrayApp.Demo();
        var tooltip = Display.Tooltip(state.Accounts, "account-a@example.com");
        Check(tooltip.Contains("GPT-A*") && tooltip.Contains("5h:34% W:61%") && tooltip.Contains("GPT-B"), "Two-account tooltip");
        state.Accounts[0].Fresh = false;
        Check(state.Accounts[0].Compact(false).Contains("过期") && state.Accounts[0].Windows[0].Remaining == 34, "Stale data retained");
        state.Accounts[1].Windows = [new("codex", 10080, 48, null)];
        Check(!state.Accounts[1].Compact(false).Contains("5h") && !state.Accounts[1].Compact(false).Contains("↻"), "Weekly-only is not a 5h limit");
        Check(state.Accounts[1].ShortWindow is null && state.Accounts[1].WeekWindow?.Remaining == 48, "A missing short window cannot be replaced by the weekly ring");
        var rings = TrayApp.Demo().Accounts[0];
        Check(rings.ShortWindow?.Remaining == 34 && rings.WeekWindow?.Remaining == 61, "Concentric rings select independent remaining quotas");
        var outerColor = ResidentWidget.RingColor(rings, rings.ShortWindow, false, true);
        var innerColor = ResidentWidget.RingColor(rings, rings.WeekWindow, true, true);
        Check(outerColor != innerColor, "Outer and inner rings have distinct colors");
        var lowWeek = new QuotaWindow("codex", 10080, 9, null);
        Check(ResidentWidget.RingColor(rings, lowWeek, true, true) != innerColor && ResidentWidget.RingColor(rings, rings.ShortWindow, false, true) == outerColor, "Low weekly quota does not recolor a healthy short-period ring");
        rings.Fresh = false;
        Check(ResidentWidget.RingColor(rings, rings.ShortWindow, false, true) == ResidentWidget.RingColor(rings, rings.WeekWindow, true, true), "Stale quota rings both become neutral");
        var screen = new Rectangle(0, 0, 1920, 1080);
        var bar = new Rectangle(0, 1032, 1920, 48);
        var traffic = new Rectangle(1540, 1032, 130, 48);
        var busyBar = new BarSnapshot(bar, [new(0, 1032, 800, 48), traffic, new(1670, 1032, 250, 48)], [traffic], true);
        var capsule = TaskbarLayout.Place(busyBar, screen, new Size(196, 42), 8);
        Check(bar.Contains(capsule) && !busyBar.Occupied.Any(r => Rectangle.Inflate(r,8,8).IntersectsWith(capsule)), "Taskbar capsule avoids app buttons, TrafficMonitor and tray");
        var full = new BarSnapshot(bar, [bar], [traffic], true);
        var fallback = TaskbarLayout.Place(full, screen, new Size(196,42), 8);
        Check(fallback.IsEmpty, "Full taskbar waits instead of showing a desktop overlay");
        Check(TaskbarLayout.Place(busyBar with { Reliable = false }, screen, new Size(196,42),8).IsEmpty, "Unknown shell layout waits without covering controls");
        var shifted = new Rectangle(-1920, 0, 1920, 1080);
        Check(TaskbarLayout.Place(new(Rectangle.Empty, [], [], false), shifted, new Size(196,42),8).IsEmpty, "Missing shell never falls back to screen top");
        var negativeBar = new Rectangle(-1920, 1032, 1920, 48);
        Check(negativeBar.Contains(TaskbarLayout.Place(new(negativeBar, [new(-300,1032,300,48)], [], true), shifted, new Size(90,42),8)), "Taskbar placement supports negative screen coordinates");
        var vertical = new Rectangle(1872, 0, 48, 1080);
        Check(!vertical.IntersectsWith(TaskbarLayout.Place(new(vertical, [vertical], [], false), screen, new Size(196,42),8)), "Vertical taskbar fallback remains outside its controls");
        Check(Display.Limit(new string('a',126) + "🟢", 127).Length == 126, "UTF-16 tooltip limit");
        CodexClient.ValidateLoginUrl("https://auth.openai.com/oauth/authorize?state=test");
        foreach (var url in new[] { "http://auth.openai.com/", "https://auth.openai.com.evil.example/", "https://evil.example@auth.openai.com/", "https://auth.openai.com:444/" })
        {
            var rejected = false;
            try { CodexClient.ValidateLoginUrl(url); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Reject untrusted OAuth URL");
        }
    }
    public static async Task SmokeAsync()
    {
        // No login, model inference, or access to the development CODEX_HOME.
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-smoke-" + Guid.NewGuid());
        var homeA = Path.Combine(root, "A");
        var homeB = Path.Combine(root, "B");
        using var a = new CodexClient(homeA);
        using var b = new CodexClient(homeB);
        var replies = await Task.WhenAll(a.CallAsync("account/read", new { refreshToken = false }), b.CallAsync("account/read", new { refreshToken = false }));
        Check(replies.All(r => r.GetProperty("account").ValueKind == JsonValueKind.Null), "Isolated blank account homes");
        try
        {
            await a.CallAsync("account/login/start", new { type = "apiKey", apiKey = "self-test-not-a-real-key-" + new string('x', 12000) });
            a.Dispose();
            Check(!File.Exists(Path.Combine(homeA, "auth.json")) && File.Exists(Path.Combine(homeA, "auth.dpapi")), "Long credentials encrypted on suspension");
            using var reopened = new CodexClient(homeA);
            var restored = await reopened.CallAsync("account/read", new { refreshToken = false });
            var separate = await b.CallAsync("account/read", new { refreshToken = false });
            Check(restored.GetProperty("account").GetProperty("type").GetString() == "apiKey" && separate.GetProperty("account").ValueKind == JsonValueKind.Null, "Persistent encrypted credential isolation");
            await reopened.CallAsync("account/logout");
            Check(!File.Exists(Path.Combine(homeA, "auth.dpapi")), "Logout clears encrypted credentials");
        }
        finally
        {
            using var cleanup = new CodexClient(homeA);
            await cleanup.CallAsync("account/logout");
        }
        var disposedRejected = false;
        try { await a.CallAsync("account/read"); } catch (ObjectDisposedException) { disposedRejected = true; }
        Check(disposedRejected, "Disposed client cannot restart a background process");
        // Start and cancel OAuth without opening a browser or authorizing any real account.
        using var oauthA = new CodexClient(homeA);
        foreach (var client in new[] { oauthA, b })
        {
            var flow = await client.CallAsync("account/login/start", new { type = "chatgpt" });
            try { CodexClient.ValidateLoginUrl(flow.GetProperty("authUrl").GetString()!); }
            finally { await client.CallAsync("account/login/cancel", new { loginId = flow.GetProperty("loginId").GetString() }); }
        }
        oauthA.Suspend(); b.Suspend();
        Check(!File.Exists(Path.Combine(homeA, "auth.json")) && !File.Exists(Path.Combine(homeB, "auth.json")), "No working credential files between sessions");
        var vault = new CredentialVault(homeA);
        vault.Restore();
        var oauth = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tokens = new { access_token = new string('a', 16000), refresh_token = new string('b', 5000), id_token = "self-test-not-real", account_id = "test" } }));
        File.WriteAllBytes(vault.PlainPath, oauth);
        vault.Seal();
        Check(!File.Exists(vault.PlainPath) && !Encoding.UTF8.GetString(File.ReadAllBytes(vault.EncryptedPath)).Contains(new string('a',256)), "Large OAuth-shaped credentials sealed at rest");
        vault.Restore();
        Check(File.ReadAllBytes(vault.PlainPath).SequenceEqual(oauth), "Large OAuth DPAPI round trip");
        var permissions = new DirectoryInfo(homeA).GetAccessControl();
        var owner = WindowsIdentity.GetCurrent().User;
        Check(permissions.AreAccessRulesProtected && permissions.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().All(rule => rule.IdentityReference.Equals(owner)), "Only current-user ACL");
        vault.Seal();
        var otherVault = new CredentialVault(homeB);
        File.Copy(vault.EncryptedPath, otherVault.EncryptedPath);
        var wrongSlotRejected = false;
        try { otherVault.Restore(); } catch (InvalidOperationException) { wrongSlotRejected = true; }
        Check(wrongSlotRejected && !File.Exists(otherVault.PlainPath), "Ciphertext bound to the account directory");
        otherVault.Clear();
        // An invalid interrupted write must not overwrite the retained encrypted credentials.
        var retained = File.ReadAllBytes(vault.EncryptedPath);
        File.WriteAllText(vault.PlainPath, "not json");
        try { vault.Seal(); } catch (JsonException) { }
        Check(File.ReadAllBytes(vault.EncryptedPath).SequenceEqual(retained) && File.Exists(vault.PlainPath), "Preserve retained copy on interrupted write");
        File.Delete(vault.PlainPath);
        vault.Clear();
        var foreignRejected = false;
        try { _ = new CredentialVault(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")); }
        catch (InvalidOperationException) { foreignRejected = true; }
        Check(foreignRejected, "Refuse access to development authentication directory");
    }
}
