using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexQuotaTray;

internal static class MonitorStorage
{
    // Keep monitor data outside AppData's packaged-app filesystem redirection.
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "data"));
    public static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray");
    public static void Migrate()
    {
        for (var dir = new DirectoryInfo(Root); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("监控数据目录包含链接，拒绝迁移。");
        Directory.CreateDirectory(Root);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(security);
        foreach (var slot in new[] { "A", "B" })
        {
            // No plaintext is written; source encrypted credentials remain untouched.
            var vault = new CredentialVault(Path.Combine(Root, "accounts", slot));
            vault.ImportLegacy(Path.Combine(LegacyRoot, "accounts", slot));
        }
        var cache = Path.Combine(Root, "state.json");
        var oldCache = Path.Combine(LegacyRoot, "state.json");
        if (!File.Exists(cache) && File.Exists(oldCache) && new FileInfo(oldCache).Length <= 1024 * 1024)
        {
            // Quota snapshots are optional; keep malformed caches out of the new store.
            var contents = File.ReadAllText(oldCache);
            if (JsonSerializer.Deserialize<SavedState>(contents)?.Accounts is { Count: 2 })
            {
                File.WriteAllText(cache + ".tmp", contents); File.Move(cache + ".tmp", cache);
            }
        }
    }
}
