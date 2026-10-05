using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace CodexQuotaTray;

internal static class ResidentLaunch
{
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
