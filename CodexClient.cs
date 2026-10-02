using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTray;

internal sealed class CodexClient : IDisposable
{
    readonly string home;
    readonly CredentialVault vault;
    readonly SemaphoreSlim startLock = new(1);
    readonly SemaphoreSlim writeLock = new(1);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    Process? process;
    int nextId;
    TaskCompletionSource<bool>? login;
    bool healthy;
    bool disposed;
    public CodexClient(string home)
    {
        this.home = Path.GetFullPath(home);
        vault = new CredentialVault(this.home);
    }

    public static string FindExecutable()
    {
        // ponytail: use the installed CLI binary, no second Codex installation or shell-command construction.
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_QUOTA_CLI");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath) && Path.GetExtension(explicitPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return explicitPath;
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        if (Directory.Exists(npm))
        {
            var found = Directory.EnumerateFiles(npm, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("未找到 Codex CLI。请安装 CLI，或设置 CODEX_QUOTA_CLI 为 codex.exe 的完整路径。");
    }

    async Task EnsureStartedAsync()
    {
        await startLock.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (healthy && process is { HasExited: false }) return;
            Stop();
            vault.Restore();
            var info = new ProcessStartInfo(FindExecutable())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = home
            };
            foreach (var argument in new[] { "app-server", "--listen", "stdio://", "-c", "cli_auth_credentials_store=\"file\"", "-c", "analytics.enabled=false" }) info.ArgumentList.Add(argument);
            foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_ACCESS_TOKEN", "OPENAI_IDENTITY_TOKEN_FILE", "CODEX_SQLITE_HOME" }) info.Environment.Remove(key);
            info.Environment["CODEX_HOME"] = home;
            process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Codex 查询进程。");
            healthy = true;
            var child = process;
            _ = PumpAsync(child);
            _ = DrainErrorsAsync(child);
            try
            {
                await SendAsync("initialize", new { clientInfo = new { name = "codex_quota_tray", title = "Codex Quota Tray", version = typeof(CodexClient).Assembly.GetName().Version?.ToString(3) ?? "dev" }, capabilities = new { experimentalApi = false } });
                await WriteAsync(new { method = "initialized" });
            }
            catch { Suspend(); throw; }
        }
        finally { startLock.Release(); }
    }

    public async Task<JsonElement> CallAsync(string method, object? args = null)
    {
        await EnsureStartedAsync();
        var result = await SendAsync(method, args);
        if (method == "account/logout") vault.Clear();
        return result;
    }

    async Task<JsonElement> SendAsync(string method, object? args)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = args ?? new { } });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(35));
        }
        catch (TimeoutException) { healthy = false; throw new InvalidOperationException("查询超时，已保留上次数据。请稍后刷新。"); }
        finally { pending.TryRemove(id, out _); }
    }

    async Task WriteAsync(object message)
    {
        await writeLock.WaitAsync();
        try
        {
            if (process is null || process.HasExited) throw new InvalidOperationException("Codex 查询进程已退出，请重新刷新。");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync();
        }
        finally { writeLock.Release(); }
    }

    async Task PumpAsync(Process child)
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && pending.TryRemove(number, out var response))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var c) ? c.ToString() : "unknown";
                        // Server messages can contain URLs or credentials. Keep only the numeric error code.
                        response.TrySetException(new InvalidOperationException($"Codex 接口错误（{code}），请检查登录或稍后重试。"));
                    }
                    else if (root.TryGetProperty("result", out var result)) response.TrySetResult(result.Clone());
                    else response.TrySetException(new InvalidOperationException("Codex 返回了无效响应。"));
                }
                else if (root.TryGetProperty("method", out var method))
                {
                    if (method.GetString() == "account/login/completed")
                    {
                        var p = root.GetProperty("params");
                        if (p.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) login?.TrySetResult(true);
                        else login?.TrySetException(new InvalidOperationException("登录未完成，请重试并选择正确的账号。"));
                    }
                    else if (root.TryGetProperty("id", out var requestId))
                        await WriteAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "Monitor does not implement server requests" } });
                }
            }
        }
        catch { /* Never persist protocol payloads or bearer tokens. */ }
        finally
        {
            if (ReferenceEquals(process, child))
            {
                healthy = false;
                foreach (var item in pending) item.Value.TrySetException(new InvalidOperationException("Codex 查询连接中断，请重新刷新。"));
                login?.TrySetException(new InvalidOperationException("登录连接中断，请重试。"));
            }
        }
    }
    static async Task DrainErrorsAsync(Process child)
    {
        try { while (await child.StandardError.ReadLineAsync() is not null) { } } catch { }
    }

    public async Task LoginAsync(Action<string> showUrl, CancellationToken cancellation)
    {
        login = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string? loginId = null;
        try
        {
            var result = await CallAsync("account/login/start", new { type = "chatgpt" });
            loginId = result.GetProperty("loginId").GetString();
            var url = result.GetProperty("authUrl").GetString()!;
            ValidateLoginUrl(url);
            showUrl(url);
            await login.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellation);
        }
        finally
        {
            if (loginId is not null)
            {
                try { await CallAsync("account/login/cancel", new { loginId }); } catch { }
            }
            login = null;
        }
    }
    internal static void ValidateLoginUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !(uri.Host == "auth.openai.com" || uri.Host == "chatgpt.com" || uri.Host == "auth0.openai.com"))
            throw new InvalidOperationException("登录接口返回了未知地址，未打开浏览器。");
    }
    void Stop()
    {
        healthy = false;
        if (process is null) return;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(5000)) throw new InvalidOperationException("无法停止监控查询进程，暂未整理其工作凭据，请重试。");
        process.Dispose(); process = null;
        foreach (var item in pending) item.Value.TrySetException(new InvalidOperationException("查询已停止。"));
        pending.Clear();
    }
    public void Suspend() { Stop(); vault.Seal(); }
    public void Dispose()
    {
        disposed = true;
        try { Suspend(); } catch { /* Preserve restricted working credentials if encryption fails; never delete the only retained copy. */ }
    }
}
