using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace CodexQuotaTray;

internal static class ResidentLaunch
{
    delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int length);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint process);
    internal static readonly uint OpenPanelMessage = RegisterWindowMessage("CodexQuotaTray.OpenPanel");

    internal static bool RequestPanel(bool demo = false)
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("CodexQuotaTray"))
        {
            using (p) { try { if (string.Equals(p.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) ids.Add(p.Id); } catch { /* A process may exit during enumeration. */ } }
        }
        var sent = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (!ids.Contains((int)id)) return true;
            var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith("Codex 双账号额度 ·", StringComparison.Ordinal)) return true;
            if (title.ToString().EndsWith(" · 演示数据", StringComparison.Ordinal) != demo) return true;
            AllowSetForegroundWindow(id);
            sent = PostMessage(window, OpenPanelMessage, IntPtr.Zero, IntPtr.Zero);
            return !sent;
        }, IntPtr.Zero);
        return sent;
    }

    public static string TaskName => "CodexQuotaTray-" + WindowsIdentity.GetCurrent().User!.Value;
    static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect(); return service;
    }
    public static bool StartupEnabled
    {
        get
        {
            dynamic service = Connect();
            try { return service.GetFolder("\\").GetTask(TaskName).Definition.Triggers.Count > 0; }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return run?.GetValue("CodexQuotaTray") is not null;
            }
            finally { Marshal.FinalReleaseComObject(service); }
        }
    }
    public static void Install(bool startup)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位监控程序。");
        // Register only the actual installed executable, never temporary preview/test builds.
        if (!string.Equals(Path.GetDirectoryName(exe), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(exe)), "dist", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从 dist 中启动正式版本后部署常驻任务。");
        MonitorStorage.Migrate();
        dynamic service = Connect();
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "Codex 双账号额度监控；独立于 Codex，当前用户桌面常驻。";
            definition.Principal.UserId = sid;
            definition.Principal.LogonType = 3; // Interactive token; no password or elevation.
            definition.Principal.RunLevel = 0;
            definition.Settings.Enabled = true; definition.Settings.AllowDemandStart = true;
            definition.Settings.DisallowStartIfOnBatteries = false; definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S"; definition.Settings.MultipleInstances = 2;
            definition.Settings.RestartInterval = "PT1M"; definition.Settings.RestartCount = 3;
            if (startup)
            {
                dynamic trigger = definition.Triggers.Create(9); trigger.UserId = sid; trigger.Enabled = true; trigger.Delay = "PT10S";
            }
            dynamic action = definition.Actions.Create(0);
            action.Path = exe; action.Arguments = "--worker --background"; action.WorkingDirectory = AppContext.BaseDirectory;
            service.GetFolder("\\").RegisterTaskDefinition(TaskName, definition, 6, sid, null, 3, null);
            // Migrate only our own old autostart entry after successful task registration.
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("CodexQuotaTray", false);
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    public static void Start()
    {
        dynamic service = Connect();
        try { service.GetFolder("\\").GetTask(TaskName).Run(null); }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}
