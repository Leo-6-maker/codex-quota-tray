using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTray;

internal sealed class CredentialVault
{
    readonly string home;
    public string PlainPath => Path.Combine(home, "auth.json");
    public string EncryptedPath => Path.Combine(home, "auth.dpapi");
    public CredentialVault(string home)
    {
        this.home = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Directory.GetParent(this.home)!;
        var monitorRoot = Path.Combine(MonitorStorage.Root, "accounts");
        var testRoot = parent.Parent?.FullName.Equals(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true &&
            parent.Name.StartsWith("CodexQuotaTray-smoke-", StringComparison.Ordinal) && Guid.TryParse(parent.Name["CodexQuotaTray-smoke-".Length..], out _);
        if (!(Path.GetFileName(this.home) is "A" or "B") || !(parent.FullName.Equals(monitorRoot, StringComparison.OrdinalIgnoreCase) || testRoot))
            throw new InvalidOperationException("凭据目录不属于监控器，拒绝读写。");
    }

    void Prepare()
    {
        for (var dir = new DirectoryInfo(home); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("监控凭据目录包含链接，拒绝读写。");
        Directory.CreateDirectory(home);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(home).SetAccessControl(security);
        foreach (var path in new[] { PlainPath, EncryptedPath, EncryptedPath + ".tmp" })
        {
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("监控凭据文件包含链接，拒绝读写。");
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(fileSecurity);
        }
    }
    static byte[] ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("监控凭据文件过大，未修改原文件。");
        return File.ReadAllBytes(path);
    }
    public void ImportLegacy(string sourceHome)
    {
        var allowed = Path.Combine(MonitorStorage.LegacyRoot, "accounts", Path.GetFileName(home));
        if (!Path.GetFullPath(sourceHome).Equals(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("拒绝迁移非监控目录。");
        Prepare();
        if (File.Exists(EncryptedPath)) return;
        var source = Path.Combine(sourceHome, "auth.dpapi");
        if (!File.Exists(source)) return;
        for (var dir = new DirectoryInfo(sourceHome); dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("旧凭据目录包含链接，拒绝迁移。");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || File.Exists(Path.Combine(sourceHome,"auth.json")))
            throw new InvalidOperationException("旧监控凭据尚未封存，拒绝迁移。");
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(ReadBounded(source), Encoding.UTF8.GetBytes(sourceHome), DataProtectionScope.CurrentUser);
            Validate(plain);
            var encrypted = ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(home), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(EncryptedPath + ".tmp", encrypted); File.Move(EncryptedPath + ".tmp", EncryptedPath);
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    static void Validate(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !(doc.RootElement.TryGetProperty("tokens", out _) || doc.RootElement.TryGetProperty("OPENAI_API_KEY", out _)))
            throw new InvalidOperationException("监控凭据格式无效，未覆盖已加密的凭据。");
    }
    public void Restore()
    {
        Prepare();
        // Recover a restricted working file left by an interrupted login before opening a new session.
        Seal();
        if (!File.Exists(EncryptedPath)) return;
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(ReadBounded(EncryptedPath), Encoding.UTF8.GetBytes(home), DataProtectionScope.CurrentUser);
            Validate(plain);
            File.WriteAllBytes(PlainPath, plain);
        }
        catch (CryptographicException) { throw new InvalidOperationException("无法解密监控凭据，请使用原 Windows 用户或重新登录。"); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    public void Seal()
    {
        Prepare();
        if (!File.Exists(PlainPath)) return;
        var plain = ReadBounded(PlainPath);
        try
        {
            Validate(plain);
            var encrypted = ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(home), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(EncryptedPath + ".tmp", encrypted);
            File.Move(EncryptedPath + ".tmp", EncryptedPath, true);
            File.Delete(PlainPath);
        }
        catch (CryptographicException) { throw new InvalidOperationException("加密保存失败，已在仅本用户可访问的目录中保留凭据，请重试。"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Clear()
    {
        Prepare();
        foreach (var path in new[] { PlainPath, EncryptedPath, EncryptedPath + ".tmp" }) File.Delete(path);
    }
}
