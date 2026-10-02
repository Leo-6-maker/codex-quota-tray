using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexQuotaTray;

internal sealed class TrayApp : ApplicationContext
{
    readonly bool demo;
    readonly string data = MonitorStorage.Root;
    readonly Form form = new();
    readonly NotifyIcon tray = new();
    ResidentWidget widget;
    readonly ContextMenuStrip menu = new();
    readonly EventHandler displayChanged;
    int widgetRecoveries;
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    readonly CodexClient[] clients;
    readonly Label[] identities = new Label[2], statuses = new Label[2];
    readonly QuotaBars[] summaries = new QuotaBars[2];
    readonly TextBox[] details = new TextBox[2];
    readonly Button[] loginButtons = new Button[2], logoutButtons = new Button[2];
    readonly Label footer = new();
    readonly Button refresh = new();
    readonly CheckBox alerts = new(), startup = new();
    SavedState state;
    CancellationTokenSource? loginCancellation;
    Icon? ownedIcon;
    bool busy, exiting;
    string? cacheWarning;
    string? activeEmail;
    DateTimeOffset nextRefresh = DateTimeOffset.UtcNow;

    public TrayApp(bool demo, string? preview, bool background = false, string? widgetPreview = null, string? displayCheck = null)
    {
        this.demo = demo;
        state = demo ? Demo() : Load();
        clients = demo ? [] : Enumerable.Range(0, 2).Select(i => new CodexClient(Path.Combine(data, "accounts", i == 0 ? "A" : "B"))).ToArray();
        form.SuspendLayout();
        form.Text = $"Codex 双账号额度 · v{typeof(TrayApp).Assembly.GetName().Version?.ToString(3)}" + (demo ? " · 演示数据" : "");
        form.Font = new Font("Microsoft YaHei UI", 9.5f);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.ClientSize = new Size(980, 680);
        form.MinimumSize = new Size(640, 480);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.BackColor = Color.FromArgb(245, 247, 250);
        form.FormClosing += (_, e) => { if (!exiting) { e.Cancel = true; form.Hide(); } };
        BuildUi();
        form.ResumeLayout(true);
        menu.Items.Add("查看两个账号", null, (_, _) => Show());
        menu.Items.Add("立即刷新", null, async (_, _) => await RefreshAllAsync());
        var resident = new ToolStripMenuItem("显示任务栏圆环") { Checked = state.WidgetEnabled, CheckOnClick = true };
        menu.Items.Add(resident);
        widget = new ResidentWidget(state, demo, menu, Show, () => _ = RefreshAllAsync());
        displayChanged = (_, _) =>
        {
            if (!exiting && form.IsHandleCreated) form.BeginInvoke(async () => { FitPanel(); await EnsureWidgetAsync(true); });
        };
        SystemEvents.DisplaySettingsChanged += displayChanged;
        form.DpiChanged += (_, _) => form.BeginInvoke(FitPanel);
        menu.Items.Add("重新寻找任务栏空位", null, async (_, _) => await widget.RepositionAsync());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        resident.CheckedChanged += async (_, _) => { state.WidgetEnabled = resident.Checked; Save(); await EnsureWidgetAsync(true); };
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Show(); };
        tray.Visible = true;
        clock.Tick += async (_, _) =>
        {
            await EnsureWidgetAsync();
            UpdateUi();
            if (!demo && !busy && DateTimeOffset.UtcNow >= nextRefresh) await RefreshAllAsync();
        };
        UpdateUi();
        form.Shown += async (_, _) =>
        {
            FitPanel();
            if (preview is not null)
            {
                form.BeginInvoke(() =>
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.GetFullPath(preview));
                    ExitThread();
                });
            }
            else
            {
                RecordHealth("starting");
                if (background || !demo && state.Accounts.Any(a => a.Email is not null)) form.Hide();
                await EnsureWidgetAsync(true);
                RecordHealth("widget-ready");
                if (displayCheck is not null)
                {
                    try { await CheckDisplayRecoveryAsync(displayCheck); File.WriteAllText(displayCheck + "-result.txt", "PASS"); }
                    catch (Exception ex) { File.WriteAllText(displayCheck + "-result.txt", "FAIL: " + ex); Environment.ExitCode = 1; }
                    ExitThread(); return;
                }
                if (widgetPreview is not null)
                {
                    try
                    {
                        widget.RefreshView(false, "account-a@example.com");
                        await widget.RenderPreviewAsync(Path.GetFullPath(widgetPreview));
                        File.WriteAllText(widgetPreview + "-result.txt", "PASS");
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(widgetPreview + "-result.txt", "FAIL: " + ex);
                        Environment.ExitCode = 1;
                    }
                    ExitThread(); return;
                }
                clock.Start(); if (!demo) await RefreshAllAsync();
                RecordHealth("ready");
            }
        };
        form.Show();
    }

    void RecordHealth(string phase)
    {
        if (demo) return;
        try
        {
            Directory.CreateDirectory(data);
            var path = Path.Combine(data, "state.json");
            File.WriteAllText(Path.Combine(data, "health.json"), JsonSerializer.Serialize(new { phase, time = DateTimeOffset.UtcNow, pid = Environment.ProcessId, data, cacheWarning, exiting, widgetRecoveries, widgetDisposed = widget.IsDisposed, widgetVisible = !widget.IsDisposed && widget.Visible, layoutError = widget.LayoutError, widgetWindow = widget.LiveHandle ? TaskbarLayout.WindowInfo(widget.Handle) : null, panel = new { form.Bounds, form.DeviceDpi, form.AutoScaleDimensions }, cacheExists = File.Exists(path), cacheSize = File.Exists(path) ? new FileInfo(path).Length : 0, encrypted = Enumerable.Range(0,2).Select(i => File.Exists(Path.Combine(data,"accounts",i == 0 ? "A" : "B", "auth.dpapi"))), accounts = state.Accounts.Select(a => new { a.Name, a.Fresh, a.Updated, a.Status }) }));
        }
        catch { /* Health metadata is optional; never include credentials. */ }
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(root);
        root.Controls.Add(new Label { Text = "两个账号，一眼看清剩余额度", Font = new Font(form.Font.FontFamily, 17, FontStyle.Bold), Dock = DockStyle.Fill, Height = 40, AutoEllipsis = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        root.Controls.Add(new Label { Text = "百分比为剩余 · ↻ 为短周期重置倒计时 · 每 2 分钟刷新", Dock = DockStyle.Fill, Height = 24, AutoEllipsis = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 16) }, 0, 1);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.Controls.Add(cards, 0, 2);
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            var card = new TableLayoutPanel { BackColor = Color.White, Dock = DockStyle.Fill, Padding = new Padding(16), Margin = new Padding(i == 0 ? 0 : 7, 0, i == 0 ? 7 : 0, 8), ColumnCount = 1, RowCount = 6 };
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.Controls.Add(card, i, 0);
            card.Controls.Add(new Label { Text = state.Accounts[i].Name, AutoSize = true, Font = new Font(form.Font.FontFamily, 15, FontStyle.Bold) }, 0, 0);
            identities[i] = new Label { Dock = DockStyle.Fill, Height = 48, AutoEllipsis = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 10) };
            card.Controls.Add(identities[i], 0, 1);
            summaries[i] = new QuotaBars { Dock = DockStyle.Fill, Height = 72, Margin = new Padding(0, 0, 0, 12) };
            card.Controls.Add(summaries[i], 0, 2);
            details[i] = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ScrollBars = ScrollBars.Vertical, TabStop = true };
            card.Controls.Add(details[i], 0, 3);
            statuses[i] = new Label { Dock = DockStyle.Fill, Height = 46, AutoEllipsis = true, ForeColor = Color.DimGray, Padding = new Padding(0, 8, 0, 8) };
            card.Controls.Add(statuses[i], 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            loginButtons[i] = new Button { Text = "登录 / 更换账号", AutoSize = true, Enabled = !demo };
            loginButtons[i].Click += async (_, _) => await LoginAsync(index);
            logoutButtons[i] = new Button { Text = "移除登录", AutoSize = true, Enabled = !demo };
            logoutButtons[i].Click += async (_, _) => await LogoutAsync(index);
            buttons.Controls.Add(loginButtons[i]); buttons.Controls.Add(logoutButtons[i]); card.Controls.Add(buttons, 0, 5);
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 12, 0, 10) };
        refresh.Text = "立即刷新"; refresh.AutoSize = true; refresh.Enabled = !demo;
        refresh.Click += async (_, _) => await RefreshAllAsync();
        var cancel = new Button { Text = "取消登录", AutoSize = true, Enabled = !demo };
        cancel.Click += (_, _) => loginCancellation?.Cancel();
        alerts.Text = "低于 20% 提醒"; alerts.AutoSize = true; alerts.Checked = state.AlertsEnabled; alerts.Enabled = !demo;
        alerts.CheckedChanged += (_, _) => { state.AlertsEnabled = alerts.Checked; Save(); };
        startup.Text = "开机启动"; startup.AutoSize = true; startup.Enabled = !demo;
        startup.Checked = !demo && ResidentLaunch.StartupEnabled;
        startup.CheckedChanged += (_, _) =>
        {
            try
            {
                ResidentLaunch.Install(startup.Checked);
            }
            catch { MessageBox.Show(form, "无法修改开机启动设置。", "Codex 额度"); }
        };
        actions.Controls.AddRange([refresh, cancel, alerts, startup]); root.Controls.Add(actions, 0, 3);
        footer.Dock = DockStyle.Fill; footer.Height = 48; footer.AutoEllipsis = true; footer.ForeColor = Color.DimGray; root.Controls.Add(footer, 0, 4);
    }

    async Task EnsureWidgetAsync(bool force = false)
    {
        if (exiting) return;
        var recovered = false;
        if (widget.IsDisposed || !widget.LiveHandle)
        {
            widget.Dispose();
            widget = new ResidentWidget(state, demo, menu, Show, () => _ = RefreshAllAsync());
            widgetRecoveries++; force = true; recovered = true;
        }
        if (force || state.WidgetEnabled && !widget.Visible) await widget.SetEnabledAsync(state.WidgetEnabled);
        if (recovered) RecordHealth("ready");
    }
    void FitPanel()
    {
        if (exiting || form.IsDisposed || form.WindowState != FormWindowState.Normal) return;
        var area = Screen.FromRectangle(form.Bounds).WorkingArea;
        var scale = form.DeviceDpi / 96f;
        form.MinimumSize = new Size(Math.Min((int)(640 * scale), area.Width), Math.Min((int)(480 * scale), area.Height));
        var size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
        form.Bounds = new Rectangle(Math.Clamp(form.Left, area.Left, area.Right - size.Width), Math.Clamp(form.Top, area.Top, area.Bottom - size.Height), size.Width, size.Height);
    }
    void Show() { form.Show(); form.WindowState = FormWindowState.Normal; FitPanel(); form.Activate(); }
    async Task CheckDisplayRecoveryAsync(string prefix)
    {
        var transitions = new List<object>();
        var stable = widget.Snapshot!;
        var visibilityChanges = 0;
        EventHandler visibleChanged = (_, _) => visibilityChanges++;
        widget.VisibleChanged += visibleChanged;
        var retainedHandle = widget.Handle;
        var retainedBounds = widget.ScreenBounds;
        for (var i = 0; i < 3; i++) await widget.RepositionAsync(stable);
        if (visibilityChanges != 0 || widget.Handle != retainedHandle || widget.ScreenBounds != retainedBounds)
            throw new InvalidOperationException("Unchanged layout must not hide, show, or recreate the child");
        foreach (var dpi in new[] { 120, 168, 192, 96 })
        {
            var memory = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(new[] { 0, 0, 126, 60 }, 0, memory, 4);
                SendMessage(widget.Handle, 0x02E0, (IntPtr)(dpi | dpi << 16), memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
            if (!widget.Embedded || widget.ScreenBounds != retainedBounds || visibilityChanges != 0)
                throw new InvalidOperationException("Top-level DPI suggestions must not move taskbar children to screen top");
            transitions.Add(new { RequestedDpi = dpi, IgnoredTopLevelRectangle = true, Bounds = widget.ScreenBounds });
        }
        foreach (var invalid in new[] { stable with { ShellHandle = 0 }, stable with { Bar = Rectangle.Empty }, stable with { Dpi = stable.Dpi + 24 }, stable with { Reliable = false } })
        {
            await widget.RepositionAsync(invalid);
            var changes = visibilityChanges;
            for (var i = 0; i < 3; i++) await widget.RepositionAsync(invalid);
            if (widget.Visible || visibilityChanges != changes) throw new InvalidOperationException("Unavailable taskbar must remain hidden without flickering");
            transitions.Add(new { Hidden = true, NoRepeatedShow = true, widget.LayoutError });
            await widget.RepositionAsync(stable);
            if (!widget.Visible || !widget.Embedded || widget.ScreenBounds != retainedBounds)
                throw new InvalidOperationException("Stable taskbar must restore its child after an invalid snapshot");
        }
        widget.VisibleChanged -= visibleChanged;
        var losses = new List<object>();
        for (var i = 0; i < 3; i++)
        {
            widget.SimulateParentLoss();
            losses.Add(new { widget.IsDisposed, widget.LiveHandle });
            await EnsureWidgetAsync(true);
            if (widget.IsDisposed || !widget.LiveHandle || !widget.Visible) throw new InvalidOperationException("Taskbar parent loss must recreate the resident window");
        }
        await widget.RenderPreviewAsync(prefix);
        form.Show(); FitPanel();
        var sizes = new List<object>();
        var area = Screen.FromControl(form).WorkingArea;
        foreach (var width in new[] { (int)(640 * form.DeviceDpi / 96f), area.Width, (int)(980 * form.DeviceDpi / 96f) })
        {
            form.Width = Math.Min(width, area.Width); FitPanel(); form.PerformLayout();
            CheckPanel();
            sizes.Add(new { form.Bounds, form.DeviceDpi, form.AutoScaleDimensions, Accounts = summaries.Select(s => s.Bounds).ToArray() });
        }
        var dpiMessages = new List<object>();
        var actualDpi = form.DeviceDpi;
        foreach (var dpi in new[] { 168, 120, 192, 96, actualDpi })
        {
            var suggested = new[] { area.Left, area.Top, area.Left + Math.Min((int)(980 * dpi / 96f), area.Width), area.Top + Math.Min((int)(680 * dpi / 96f), area.Height) };
            var memory = Marshal.AllocHGlobal(16);
            try { Marshal.Copy(suggested, 0, memory, 4); SendMessage(form.Handle, 0x02E0, (IntPtr)(dpi | dpi << 16), memory); }
            finally { Marshal.FreeHGlobal(memory); }
            await Task.Delay(50); FitPanel(); form.PerformLayout(); CheckPanel();
            dpiMessages.Add(new { RequestedDpi = dpi, form.DeviceDpi, form.AutoScaleDimensions, form.Bounds });
        }
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(prefix + "-panel.png");
        File.WriteAllText(prefix + "-display.json", JsonSerializer.Serialize(new { TaskbarTransitions = transitions, StableLayoutPreservesHandleAndVisibility = true, ParentLosses = losses, widgetRecoveries, PanelSizes = sizes, InjectedDpiMessages = dpiMessages, ActualHardwareSwitchTested = false }, new JsonSerializerOptions { WriteIndented = true }));
        void CheckPanel()
        {
            var client = form.RectangleToScreen(form.ClientRectangle);
            foreach (var control in summaries.Cast<Control>().Concat(loginButtons).Concat(logoutButtons))
                if (!client.Contains(control.RectangleToScreen(control.ClientRectangle)))
                {
                    using var failed = new Bitmap(form.Width, form.Height); form.DrawToBitmap(failed, new Rectangle(Point.Empty, form.Size)); failed.Save(prefix + "-failed-panel.png");
                    var container = control.Parent?.Parent as TableLayoutPanel;
                    throw new InvalidOperationException($"Account control must fit: DPI {form.DeviceDpi}, {control.GetType().Name}, bounds {control.RectangleToScreen(control.ClientRectangle)}, client {client}, formClient {form.ClientSize}, root {form.Controls[0].Bounds}, cards {container?.Parent?.Bounds}, container {container?.Bounds}, rows {string.Join(',', container?.GetRowHeights() ?? [])}");
                }
            if (summaries.Any(s => s.Width < 200 * form.DeviceDpi / 96f)) throw new InvalidOperationException("Account columns must remain readable");
        }
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    void UpdateUi()
    {
        activeEmail = demo ? "account-a@example.com" : Display.ReadActiveEmail();
        var duplicate = state.Accounts[0].Email is not null && state.Accounts[0].Email == state.Accounts[1].Email;
        for (var i = 0; i < 2; i++)
        {
            var a = state.Accounts[i];
            var active = activeEmail is not null && activeEmail == a.Email;
            identities[i].Text = a.Email is null ? "首次使用请分别登录两个 Plus 账号" : $"{a.Email}\n{a.Plan}" + (active ? " · 当前开发账号" : "");
            summaries[i].Account = a; summaries[i].Invalidate();
            var extra = a.Windows.Except(a.Summary).ToList();
            var text = a.Windows.Count == 0 ? "授权后显示短周期与周额度。\r\n关闭窗口后程序继续在托盘运行。" : extra.Count == 0 ? string.Join("\r\n", a.Summary.Where(w => w.Reset is not null).Select(w => $"{w.Label} 重置于 {DateTimeOffset.FromUnixTimeSeconds(w.Reset!.Value).ToLocalTime():MM-dd HH:mm}")) : string.Join("\r\n", extra.Select(w =>
                $"{(w.Bucket == "codex" ? "" : w.Bucket + " · ")}{w.Label}  {w.Remaining:0}%  ↻{w.Countdown}" + (w.Reset is long reset ? $"\r\n重置：{DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime():MM-dd HH:mm}" : "")));
            if (details[i].Text != text) details[i].Text = text;
            statuses[i].Text = duplicate ? "两个位置登录了同一账号，请更换其中一个。" : a.Status + (a.Updated is { } updated ? $" · 更新于 {updated.ToLocalTime():HH:mm:ss}" : "");
            loginButtons[i].Enabled = logoutButtons[i].Enabled = !demo && !busy;
        }
        refresh.Enabled = !demo && !busy;
        footer.Text = cacheWarning ?? (demo ? "演示模式：以上为示例数据，未读取登录凭据、未查询网络。" : busy && loginCancellation is not null ? "请在浏览器选择对应的 ChatGPT 账号。登录结果会显示在账号卡片中。" :
            "仅监控额度，切换开发账号请在 Codex 中手动操作。\r\n" + (activeEmail is null ? "当前开发账号暂无法识别；双账号监控不受影响。" : "当前开发账号通过本地登录文件只读识别。") + " 隐藏图标可在 Windows 设置中调整。");
        tray.Text = Display.Tooltip(state.Accounts, activeEmail);
        widget.RefreshView(busy, activeEmail);
        var iconColor = state.Accounts.Any(a => a.Stale) ? Color.Gray : state.Accounts.Any(a => a.Windows.Any(w => w.Remaining < 20)) ? Color.DarkOrange : Color.SeaGreen;
        if (ownedIcon is null || tray.Tag as string != iconColor.Name)
        {
            var next = CreateIcon(iconColor); tray.Icon = next; ownedIcon?.Dispose(); ownedIcon = next; tray.Tag = iconColor.Name; form.Icon = next;
        }
    }

    async Task RefreshOneAsync(int index)
    {
        var a = state.Accounts[index];
        try
        {
            var result = await clients[index].CallAsync("account/read", new { refreshToken = false });
            var account = result.GetProperty("account");
            if (account.ValueKind != JsonValueKind.Object || account.GetProperty("type").GetString() != "chatgpt")
            { a.Fresh = false; a.Status = "尚未登录，请点击登录"; return; }
            var email = account.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (a.Email != email) { a.Windows.Clear(); a.Updated = null; a.Alerted.Clear(); }
            a.Email = email;
            a.Plan = account.TryGetProperty("planType", out var plan) && plan.ValueKind == JsonValueKind.String ? plan.GetString() : "未知计划";
            a.Windows = QuotaWindow.Parse(await clients[index].CallAsync("account/rateLimits/read"));
            a.Updated = DateTimeOffset.UtcNow;
            a.Fresh = a.Windows.Count > 0;
            a.Status = a.Fresh ? "额度已更新" : "服务未返回额度窗口";
            foreach (var w in a.Windows.Where(w => w.Remaining < 20 && w.Reset is not null && !w.Expired))
            {
                var key = $"{w.Bucket}:{w.Minutes}:{w.Reset}";
                if (state.AlertsEnabled && a.Alerted.Add(key)) tray.ShowBalloonTip(5000, a.Name + " 额度提醒", $"{w.Label} 剩余 {w.Remaining:0}%，距离重置 {w.Countdown}。", ToolTipIcon.Warning);
            }
            a.Alerted.IntersectWith(a.Windows.Select(w => $"{w.Bucket}:{w.Minutes}:{w.Reset}"));
        }
        catch (Exception ex)
        {
            a.Fresh = false;
            a.Status = ex is InvalidOperationException ? ex.Message : "查询失败，请检查网络或重新登录；已保留上次数据。";
        }
        finally
        {
            try { clients[index].Suspend(); }
            catch { a.Fresh = false; a.Status = "加密保存未完成；凭据留在仅本用户可访问的目录，请重试。"; }
        }
    }
    async Task RefreshAllAsync()
    {
        if (busy || demo || exiting) return;
        busy = true; UpdateUi();
        try { await Task.WhenAll(RefreshOneAsync(0), RefreshOneAsync(1)); Save(); }
        finally { busy = false; nextRefresh = DateTimeOffset.UtcNow.AddMinutes(2); if (!exiting) { UpdateUi(); RecordHealth("ready"); } }
    }
    async Task LoginAsync(int index)
    {
        if (busy || demo) return;
        busy = true; loginCancellation = new(); state.Accounts[index].Fresh = false; state.Accounts[index].Status = "等待浏览器授权"; UpdateUi();
        try
        {
            await clients[index].LoginAsync(url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), loginCancellation.Token);
            await RefreshOneAsync(index);
            if (state.Accounts[index].Email is { } email) MessageBox.Show(form, $"{state.Accounts[index].Name} 已登录：\n{email}\n\n请确认这就是你为该位置选择的账号。", "登录结果");
            Save();
        }
        catch (OperationCanceledException) { state.Accounts[index].Status = "登录已取消"; }
        catch (TimeoutException) { state.Accounts[index].Status = "登录超时，请重试"; }
        catch (Exception ex) { state.Accounts[index].Status = ex is InvalidOperationException ? ex.Message : "登录失败，请检查浏览器或网络后重试。"; }
        finally
        {
            try { clients[index].Suspend(); } catch { state.Accounts[index].Fresh = false; state.Accounts[index].Status = "加密保存未完成，请重试。"; }
            loginCancellation.Dispose(); loginCancellation = null; busy = false;
            nextRefresh = DateTimeOffset.UtcNow.AddMinutes(2); if (!exiting) UpdateUi();
        }
    }
    async Task LogoutAsync(int index)
    {
        if (busy || demo) return;
        if (MessageBox.Show(form, "移除这个监控位置的登录？开发用 Codex 的登录不受影响。", "移除监控登录", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        busy = true; UpdateUi();
        try { await clients[index].CallAsync("account/logout"); state.Accounts[index] = new() { Name = state.Accounts[index].Name }; Save(); }
        catch { state.Accounts[index].Status = "移除失败，请稍后重试"; }
        finally { try { clients[index].Suspend(); } catch { state.Accounts[index].Status = "凭据整理失败，请重试。"; } busy = false; UpdateUi(); }
    }

    SavedState Load()
    {
        try
        {
            var path = Path.Combine(data, "state.json");
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
                var loaded = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(path));
                if (loaded?.Accounts is { Count: 2 } && loaded.Accounts.All(a => a.Windows is not null && a.Alerted is not null && a.Windows.All(w => double.IsFinite(w.Remaining) && w.Remaining >= 0 && w.Remaining <= 100 && w.Minutes >= 0 && (w.Reset is null || w.Reset >= 0 && w.Reset <= 253402300799))))
                {
                    for (var i = 0; i < 2; i++) { loaded.Accounts[i].Name = i == 0 ? "GPT-A" : "GPT-B"; loaded.Accounts[i].Fresh = false; loaded.Accounts[i].Status = "等待刷新（上次缓存）"; }
                    return loaded;
                }
            }
        }
        catch (Exception ex) { cacheWarning = "无法读取额度缓存（" + ex.GetType().Name + "）。"; }
        return new();
    }
    void Save()
    {
        if (demo || exiting) return;
        try
        {
            Directory.CreateDirectory(data);
            var path = Path.Combine(data, "state.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            cacheWarning = null;
        }
        catch (Exception ex) { cacheWarning = "本地缓存保存失败（" + ex.GetType().Name + "）；当前窗口仍可查看，重启后需要重新刷新。"; }
    }
    internal static SavedState Demo()
    {
        var now = DateTimeOffset.UtcNow;
        return new() { Accounts = [
            new() { Name = "GPT-A", Email = "account-a@example.com", Plan = "plus", Fresh = true, Status = "额度已更新 · 示例", Updated = now, Windows = [new("codex",300,34,now.AddMinutes(102).ToUnixTimeSeconds()),new("codex",10080,61,now.AddDays(3).ToUnixTimeSeconds())] },
            new() { Name = "GPT-B", Email = "account-b@example.com", Plan = "plus", Fresh = true, Status = "额度已更新 · 示例", Updated = now, Windows = [new("codex",300,79,now.AddMinutes(37).ToUnixTimeSeconds()),new("codex",10080,48,now.AddDays(2).ToUnixTimeSeconds())] }
        ] };
    }
    static Icon CreateIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color); graphics.FillEllipse(brush, 2, 2, 28, 28);
        using var pen = new Pen(Color.White, 2.5f); graphics.DrawArc(pen, 9, 8, 14, 16, 50, 260);
        var handle = bitmap.GetHicon();
        try { using var raw = Icon.FromHandle(handle); return (Icon)raw.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= displayChanged;
        exiting = true; clock.Stop(); widget.Dispose(); loginCancellation?.Cancel();
        foreach (var client in clients) client.Dispose();
        tray.Visible = false; form.Close(); base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { SystemEvents.DisplaySettingsChanged -= displayChanged; widget.Dispose(); clock.Dispose(); tray.Dispose(); ownedIcon?.Dispose(); form.Dispose(); foreach (var client in clients) client.Dispose(); }
        base.Dispose(disposing);
    }
}
