namespace CodexQuotaTray;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--layout-check"))
        {
            var snapshot = TaskbarLayout.Read();
            var screen = Screen.PrimaryScreen!.Bounds;
            var scale = TaskbarLayout.PrimaryScale;
            var bounds = TaskbarLayout.Place(snapshot, screen, new Size((int)Math.Round(ResidentWidget.LogicalWidth * scale), (int)Math.Round(ResidentWidget.LogicalHeight * scale)), (int)Math.Ceiling(8 * scale));
            var residents = TaskbarLayout.ResidentBounds(out var taskbarAbove, out var embedded, out var rendering);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "layout-result.json"), System.Text.Json.JsonSerializer.Serialize(new { snapshot, bounds, residents, rendering, windows = TaskbarLayout.MonitorWindows(), shell = TaskbarLayout.WindowInfo(TaskbarLayout.Shell), screens = Screen.AllScreens.Select(s => new { s.Bounds, s.WorkingArea, s.Primary }), Embedded = embedded, TaskbarAboveWidget = taskbarAbove, ResidentCollisions = residents.Any(r => snapshot.Occupied.Any(o => o.IntersectsWith(r))) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Contains("--self-test") || args.Contains("--smoke-test"))
        {
            try
            {
                SelfTest.Run();
                if (args.Contains("--smoke-test")) SelfTest.SmokeAsync().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-result.txt"), "PASS\n");
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-result.txt"), "FAIL: " + e.Message + "\n" + e.GetType().Name + "\n" + e.StackTrace);
                Environment.ExitCode = 1;
            }
            return;
        }
        var demo = args.Contains("--demo") || args.Contains("--render-preview") || args.Contains("--render-widget") || args.Contains("--display-check");
        if (!demo && !args.Contains("--worker"))
        {
            try
            {
                ResidentLaunch.Install(ResidentLaunch.StartupEnabled); ResidentLaunch.Start();
                if (args.Contains("--open"))
                {
                    var opened = false;
                    for (var attempt = 0; attempt < 100 && !opened; attempt++)
                    {
                        opened = ResidentLaunch.RequestPanel();
                        if (!opened) Thread.Sleep(100);
                    }
                    if (!opened) throw new InvalidOperationException("面板尚未准备好，请点击托盘图标重试。");
                }
                if (args.Contains("--deploy")) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "deployment-result.txt"), "PASS\n" + ResidentLaunch.TaskName);
            }
            catch (Exception ex)
            {
                if (args.Contains("--deploy")) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "deployment-result.txt"), "FAIL\n" + ex);
                else MessageBox.Show("无法部署独立常驻任务：" + ex.Message, "Codex 额度");
                Environment.ExitCode = 1;
            }
            return;
        }
        using var singleton = new Mutex(true, "Local\\CodexQuotaTray-" + (demo ? "demo" : Environment.UserName), out var first);
        if (!first) { MessageBox.Show("程序已在运行，请点击任务栏右侧的托盘图标。", "Codex 额度"); return; }
        var previewAt = Array.IndexOf(args, "--render-preview");
        var widgetAt = Array.IndexOf(args, "--render-widget");
        var displayAt = Array.IndexOf(args, "--display-check");
        using var app = new TrayApp(demo, previewAt >= 0 && previewAt + 1 < args.Length ? args[previewAt + 1] : null, args.Contains("--background"), widgetAt >= 0 && widgetAt + 1 < args.Length ? args[widgetAt + 1] : null, displayAt >= 0 && displayAt + 1 < args.Length ? args[displayAt + 1] : null);
        Application.Run(app);
    }
}
