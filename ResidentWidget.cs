using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace CodexQuotaTray;

internal sealed class ResidentWidget : Control
{
    internal const int LogicalWidth = 72, LogicalHeight = 34;
    internal static readonly Color Mint = Color.FromArgb(104,199,163), Blue = Color.FromArgb(141,169,214), LightMint = Color.FromArgb(60,154,125), LightBlue = Color.FromArgb(94,125,175), Amber = Color.FromArgb(217,169,80), Neutral = Color.FromArgb(137,146,159);
    internal static Color TrackColor(bool dark) => dark ? Color.FromArgb(56,59,65) : Color.FromArgb(193,201,211);
    internal static RectangleF RingBounds(int slot, bool weekly) => weekly ? new(9 + slot * 38, 9, 16, 16) : new(4 + slot * 38, 4, 26, 26);
    readonly SavedState state;
    readonly HoverCard card;
    readonly System.Windows.Forms.Timer hover = new() { Interval = 120 };
    readonly System.Windows.Forms.Timer layout = new() { Interval = 3000 };
    bool enabled, checking, dark;
    Color TransparencyKey => dark ? Color.FromArgb(1,1,1) : Color.FromArgb(254,254,254);
    public string? LayoutError { get; private set; }
    long entered, left, lastTick;
    Point lastPointer;
    public string? HoverError { get; private set; }
    int hoverResets;
    string? resetReason;
    internal bool PointerOverRing => ScreenBounds.Contains(lastPointer);
    internal object HoverState => new { TimerRunning = hover.Enabled, LastTick = lastTick, Pointer = lastPointer, LogicalPointer = Cursor.Position, OverRing = ScreenBounds.Contains(lastPointer), CardVisible = card.Visible, CardBounds = card.Bounds, CardWindow = card.IsHandleCreated ? TaskbarLayout.WindowInfo(card.Handle) : null, Resets = hoverResets, ResetReason = resetReason, Error = HoverError };
    public Rectangle TrafficBounds { get; private set; }
    public bool InTaskbar { get; private set; }
    public bool Embedded => IsHandleCreated && TaskbarLayout.IsEmbedded(Handle);
    public bool LiveHandle => IsHandleCreated && TaskbarLayout.IsWindow(Handle);
    public Rectangle ScreenBounds => IsHandleCreated ? TaskbarLayout.BoundsOf(Handle) : Rectangle.Empty;
    public BarSnapshot? Snapshot { get; private set; }
    internal bool FailNextHandleCreationForCheck { get; set; }
    internal Point? HoverPointerForCheck { get; set; }
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.Style = (p.Style & ~unchecked((int)0x80000000)) | 0x40000000;
            p.ExStyle |= 0x08080080; // Native child surface; never a top-level Form.
            if (Snapshot is { ShellHandle: not 0 } snapshot) p.Parent = (IntPtr)snapshot.ShellHandle;
            return p;
        }
    }

    public ResidentWidget(SavedState state, bool demo, ContextMenuStrip menu, Action open, Action refresh)
    {
        this.state = state;
        Text = "Codex 额度胶囊"; AccessibleName = "Codex 双账号剩余额度"; AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.Selectable, false); TabStop = false;
        DoubleBuffered = true; ContextMenuStrip = menu; Hide();
        BackColor = TransparencyKey;
        // Create the native child only inside RepositionAsync's guarded retry path.
        card = new HoverCard(state, demo, open, refresh) { ContextMenuStrip = menu };
        MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
        hover.Tick += (_, _) => HoverTick();
        layout.Tick += async (_, _) => await RepositionAsync();
    }

    protected override void CreateHandle()
    {
        if (FailNextHandleCreationForCheck)
        {
            FailNextHandleCreationForCheck = false;
            throw new System.ComponentModel.Win32Exception(5, "Simulated taskbar access denied during logon");
        }
        base.CreateHandle();
    }

    public async Task SetEnabledAsync(bool value)
    {
        enabled = value;
        if (!value) { hover.Stop(); layout.Stop(); card.Hide(); Hide(); return; }
        await RepositionAsync();
        if (!enabled || IsDisposed) return;
        hover.Start(); layout.Start(); // Only a successful placement may show the child.
    }

    public async Task RepositionAsync(BarSnapshot? suppliedSnapshot = null)
    {
        if (checking || IsDisposed) return;
        checking = true;
        try
        {
            // UIA queries may wait for Explorer; keep the monitor's UI responsive.
            var snapshot = suppliedSnapshot ?? await Task.Run(TaskbarLayout.Read);
            if (IsDisposed || !enabled) return;
            if (!TaskbarLayout.IsCurrent(snapshot)) throw new InvalidOperationException("任务栏正在变化，等待稳定后恢复。");
            if (Snapshot is { } previous && (previous.Dpi != snapshot.Dpi || previous.Bar != snapshot.Bar || previous.ShellHandle != snapshot.ShellHandle))
                ResetHover("Taskbar changed");
            Snapshot = snapshot;
            var screen = Screen.FromRectangle(snapshot.Bar).Bounds;
            var scale = snapshot.Dpi / 96f;
            var size = new Size((int)Math.Round(LogicalWidth * scale), (int)Math.Round(LogicalHeight * scale));
            var gap = (int)Math.Ceiling(8 * scale);
            var bounds = TaskbarLayout.Place(snapshot, screen, size, gap);
            TrafficBounds = snapshot.Traffic.FirstOrDefault();
            if (bounds.IsEmpty) throw new InvalidOperationException("任务栏暂无可靠空位，等待恢复；可使用托盘查看。");
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
            card.Dark = dark;
            BackColor = TransparencyKey;
            if (ScreenBounds != bounds || !Embedded)
            {
                card.Hide(); entered = left = 0;
            }
            TaskbarLayout.Mount(Handle, snapshot, bounds, TransparencyKey);
            InTaskbar = true;
            if (!Visible) Show();
            Invalidate();
            LayoutError = null;
        }
        catch (Exception ex) { LayoutError = ex.GetType().Name + ": " + ex.Message; InTaskbar = false; card.Hide(); if (!IsDisposed) Hide(); }
        finally { checking = false; }
    }

    public void RefreshView(bool busy, string? activeEmail)
    {
        card.Busy = busy;
        AccessibleDescription = Display.Tooltip(state.Accounts, activeEmail);
        Invalidate(); if (card.Visible) card.Invalidate();
    }

    void HoverTick()
    {
        if (!enabled || !Visible) return;
        try
        {
            var now = Environment.TickCount64;
            // Modern Standby can pause timers without sending PowerModeChanged.Resume.
            if (lastTick != 0 && now - lastTick > 5000) ResetHover("Timer resumed after pause");
            lastTick = now;
            if (HoverPointerForCheck is { } pointer) UpdateHover(pointer, now);
            else if (GetPhysicalCursorPos(out pointer)) UpdateHover(pointer, now);
            HoverError = null;
        }
        catch (Exception ex) { HoverError = ex.GetType().Name + ": " + ex.Message; card.Hide(); entered = left = 0; }
    }
    internal void ResetHover(string reason)
    {
        hover.Stop();
        card.ResetSurface();
        entered = left = lastTick = 0;
        HoverError = null; resetReason = reason; hoverResets++;
        if (enabled && !IsDisposed) hover.Start();
    }
    void UpdateHover(Point pointer, long now)
    {
        lastPointer = pointer;
        var over = ScreenBounds.Contains(pointer);
        var detail = card.Visible && InHoverArea(ScreenBounds, card.Bounds, pointer, (int)Math.Ceiling(10 * TaskbarLayout.PrimaryScale));
        if (over || detail)
        {
            if (over)
            {
                var account = pointer.X < ScreenBounds.Left + ScreenBounds.Width / 2 ? 0 : 1;
                if (card.HighlightedAccount != account) { card.HighlightedAccount = account; card.Invalidate(); }
            }
            left = 0;
            if (entered == 0) entered = now;
            if (now - entered >= 350 && (!card.Visible || over && !card.Presented)) ShowCard();
        }
        else
        {
            entered = 0;
            if (left == 0) left = now;
            if (now - left >= 500) card.Hide();
        }
    }

    internal static bool InHoverArea(Rectangle rings, Rectangle details, Point pointer, int tolerance)
    {
        if (Rectangle.Inflate(details, tolerance, tolerance).Contains(pointer)) return true;
        var left = Math.Max(rings.Left, details.Left);
        var right = Math.Min(rings.Right, details.Right);
        if (left >= right) return false;
        var bridge = details.Bottom <= rings.Top ? Rectangle.FromLTRB(left - tolerance, details.Bottom, right + tolerance, rings.Top) :
            details.Top >= rings.Bottom ? Rectangle.FromLTRB(left - tolerance, rings.Bottom, right + tolerance, details.Top) : Rectangle.Empty;
        return bridge.Contains(pointer);
    }

    void ShowCard()
    {
        var bounds = ScreenBounds;
        var working = Screen.FromRectangle(bounds).WorkingArea;
        var scale = TaskbarLayout.PrimaryScale;
        card.Size = new Size((int)(HoverCard.LogicalWidth * scale), (int)(HoverCard.LogicalHeight * scale));
        var x = Math.Clamp(bounds.Right - card.Width, working.Left, Math.Max(working.Left, working.Right - card.Width));
        var gap = (int)Math.Ceiling(8 * scale);
        var y = bounds.Top - card.Height - gap;
        if (y < working.Top) y = bounds.Bottom + gap;
        var target = new Rectangle(new Point(x, Math.Clamp(y, working.Top, Math.Max(working.Top, working.Bottom - card.Height))), card.Size);
        card.Bounds = target;
        card.Show();
        // Reassert native position and Z order after Show/DPI processing, without activation.
        card.Present(target);
        card.Invalidate();
    }

    // Render the same hover card for visual QA without moving the user's mouse.
    public async Task RenderPreviewAsync(string prefix)
    {
        var focus = GetForegroundWindow();
        var timerWasRunning = hover.Enabled;
        entered = left = 0; card.Hide();
        HoverPointerForCheck = new Point(ScreenBounds.Left + ScreenBounds.Width / 4, ScreenBounds.Top + ScreenBounds.Height / 2);
        try
        {
            await WaitForCard();
            for (var sample = 0; sample < 10; sample++)
            {
                if (!card.Visible) throw new InvalidOperationException($"Timed hover must retain its card: enabled={enabled}, timer={hover.Enabled}, widget={Visible}, bounds={ScreenBounds}, pointer={HoverPointerForCheck}, entered={entered}, left={left}, error={LayoutError}");
                if (WindowFromPoint(new Point(card.Left + card.Width / 2, card.Top + 20)) != card.Handle)
                    throw new InvalidOperationException("Timed hover card must be on top of the displayed desktop");
                await Task.Delay(400);
            }
            foreach (var reason in new[] { "Display settings changed", "System resumed", "Session unlocked" })
            {
                ResetHover(reason);
                if (card.Visible || card.IsHandleCreated) throw new InvalidOperationException("Environment recovery must discard the stale card surface");
                await WaitForCard();
                if (!card.Visible || !card.Presented || WindowFromPoint(new Point(card.Left + card.Width / 2, card.Top + 20)) != card.Handle || GetForegroundWindow() != focus)
                    throw new InvalidOperationException("Hover must recover without focus activation after " + reason);
            }
            // Keep WinForms Visible=true while the native window is offscreen and no longer topmost.
            HoverCard.SetWindowPos(card.Handle, (IntPtr)(-2), -30000, -30000, card.Width, card.Height, 0x0010);
            if (!card.Visible || card.Presented) throw new InvalidOperationException("Native presentation fault injection failed");
            await WaitForCard();
            if (!card.Presented || WindowFromPoint(new Point(card.Left + card.Width / 2, card.Top + 20)) != card.Handle)
                throw new InvalidOperationException("Timer must repair an offscreen or lowered card even when Visible=true");
            var resets = hoverResets;
            lastTick = Environment.TickCount64 - 6000;
            await WaitForCard(resets);
            if (hoverResets != resets + 1 || !card.Presented || GetForegroundWindow() != focus)
                throw new InvalidOperationException($"Paused timer must recover hover without relying on a resume broadcast: resets={resets}->{hoverResets}, visible={card.Visible}, presented={card.Presented}, focus={focus}->{GetForegroundWindow()}, entered={entered}, lastTick={lastTick}, now={Environment.TickCount64}, reason={resetReason}, error={HoverError}");
        }
        finally { HoverPointerForCheck = null; hover.Stop(); }
        async Task WaitForCard(int? afterReset = null)
        {
            var deadline = Environment.TickCount64 + 3000;
            while ((!card.Visible || !card.Presented || afterReset is int count && hoverResets <= count) && Environment.TickCount64 < deadline)
                await Task.Delay(50);
        }
        entered = left = 0; card.Hide();
        if (Embedded)
        {
            var retained = ScreenBounds;
            RecreateHandle();
            TaskbarLayout.Mount(Handle, Snapshot!, retained, TransparencyKey);
            if (!Embedded || ScreenBounds != retained)
                throw new InvalidOperationException("Native taskbar mount must recover after its handle is recreated");
            Show();
        }
        var screenBounds = ScreenBounds;
        var pointer = new Point(screenBounds.Left + Width / 4, screenBounds.Top + Height / 2);
        UpdateHover(pointer, 1000);
        if (card.Visible) throw new InvalidOperationException("Hover opened before its delay");
        UpdateHover(pointer, 1400);
        if (!card.Visible || card.HighlightedAccount != 0 || GetForegroundWindow() != focus) throw new InvalidOperationException("Hover must highlight A without stealing focus");
        UpdateHover(new Point(screenBounds.Left + Width * 3 / 4, pointer.Y), 1500);
        if (!card.Visible || card.HighlightedAccount != 1) throw new InvalidOperationException("Hovering B must highlight B");
        var bridgePointer = new Point(pointer.X, (card.Bottom + screenBounds.Top) / 2);
        UpdateHover(bridgePointer, 1600);
        UpdateHover(bridgePointer, 2200);
        if (!card.Visible) throw new InvalidOperationException("The gap between rings and card must retain the card");
        UpdateHover(new Point(card.Left + 20, card.Top + 20), 2300);
        if (!card.Visible || card.HighlightedAccount != 1) throw new InvalidOperationException("Card should retain its highlighted account while reading it");
        UpdateHover(new Point(-30000, -30000), 2400);
        UpdateHover(new Point(-30000, -30000), 2800);
        if (!card.Visible) throw new InvalidOperationException("Card must allow 500ms to leave before closing");
        UpdateHover(new Point(-30000, -30000), 3000);
        if (card.Visible) throw new InvalidOperationException("Card must close after leaving");
        Invalidate(); Update();
        await Task.Delay(200); // Let Explorer and the recreated color-key surface present a frame.
        DwmFlush();
        var pixels = TaskbarLayout.VisibleRingPixels(ScreenBounds);
        using (var bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, ClientRectangle); bitmap.MakeTransparent(TransparencyKey);
            if (bitmap.GetPixel(Width / 4, Height / 2).A != 0 || bitmap.GetPixel(0, 0).A != 0)
                throw new InvalidOperationException("Ring centers and surrounding background must be transparent");
            bitmap.Save(prefix + "-capsule.png");
        }
        if (pixels.Any(count => count < 8)) throw new InvalidOperationException("Both rings must be visible on the displayed taskbar, not just in a preview bitmap: " + string.Join(",", pixels));
        card.HighlightedAccount = 0;
        ShowCard();
        using (var bitmap = new Bitmap(card.Width, card.Height)) { card.DrawToBitmap(bitmap, card.ClientRectangle); bitmap.Save(prefix + "-card.png"); }
        card.HighlightedAccount = 1;
        using (var bitmap = new Bitmap(card.Width, card.Height)) { card.DrawToBitmap(bitmap, card.ClientRectangle); bitmap.Save(prefix + "-card-b.png"); }
        File.WriteAllText(prefix + "-placement.json", System.Text.Json.JsonSerializer.Serialize(new { Bounds = ScreenBounds, InTaskbar, Embedded, TransparentBackground = true, RemountCheck = Embedded ? "PASS" : "Fallback", TrafficBounds, HoverCheck = "PASS", TimerHoverCheck = "PASS", EnvironmentHoverRecoveryCheck = "PASS", NativePresentationRecoveryCheck = "PASS", PausedTimerRecoveryCheck = "PASS", HoverCorridorCheck = "PASS", AccountHighlightCheck = "PASS", ScreenRingPixels = pixels, LayerCheck = Embedded ? "Native taskbar child" : InTaskbar ? "PASS" : "Not in taskbar", FocusPreserved = GetForegroundWindow() == focus, Collision = Snapshot?.Occupied.Any(r => r.IntersectsWith(ScreenBounds)) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (timerWasRunning) hover.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE also covers active-window tracking.
        if (m.Msg == 0x02E0) { m.Result = IntPtr.Zero; return; } // Top-level suggested screen rectangles do not position taskbar children.
        base.WndProc(ref m);
    }
    internal void SimulateParentLoss()
    {
        // Destroy only a temporary parent we own, never Explorer or its taskbar.
        using var parent = new Form { ShowInTaskbar = false };
        SetParent(Handle, parent.Handle);
        if (!DestroyWindow(parent.Handle)) throw new InvalidOperationException("Cannot destroy test parent");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(Width / (float)LogicalWidth, Height / (float)LogicalHeight);
        for (var i = 0; i < 2; i++)
        {
            var a = state.Accounts[i];
            DrawRing(g, a, a.ShortWindow, RingBounds(i, false), false);
            DrawRing(g, a, a.WeekWindow, RingBounds(i, true), true);
        }
    }
    void DrawRing(Graphics g, AccountState a, QuotaWindow? w, RectangleF bounds, bool weekly)
    {
        using var track = new Pen(TrackColor(dark), weekly ? 1.8f : 2.4f) { DashStyle = w is null ? DashStyle.Dash : DashStyle.Solid };
        g.DrawEllipse(track, bounds);
        using var ring = new Pen(RingColor(a, w, weekly, dark), track.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (w?.Remaining > 0) g.DrawArc(ring, bounds, -90, (float)(360 * w.Remaining / 100));
    }
    internal static Color RingColor(AccountState a, QuotaWindow? w, bool weekly, bool dark) => a.Stale || w is null ? Neutral : w.Remaining < 20 ? Amber : weekly ? (dark ? Blue : LightBlue) : (dark ? Mint : LightMint);
    internal static Color Accent(AccountState a, bool dark) => a.Stale ? Neutral : a.Summary.Any(w => w.Remaining < 20) ? Amber : dark ? Mint : LightMint;
    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    internal static void DrawText(Graphics g, string text, RectangleF rect, float size, Color color, bool bold = false, bool center = false)
    {
        using var font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = center ? StringAlignment.Center : StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, brush, rect, format);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            enabled = false;
            hover.Dispose(); layout.Dispose(); card.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class HoverCard : Form
{
    internal const int LogicalWidth = 320, LogicalHeight = 204;
    static readonly Rectangle RefreshButton = new(254, 178, 54, 22);
    readonly SavedState state;
    readonly bool demo;
    readonly Action open, refresh;
    public bool Dark { get; set; }
    public bool Busy { get; set; }
    public int HighlightedAccount { get; set; } = -1;
    internal bool Presented => IsHandleCreated && TaskbarLayout.IsWindowVisible(Handle) &&
        (GetWindowLongPtrW(Handle, -20).ToInt64() & 8) != 0 &&
        Screen.FromRectangle(Bounds).WorkingArea.Contains(TaskbarLayout.BoundsOf(Handle));
    internal void ResetSurface() { Hide(); if (IsHandleCreated) DestroyHandle(); }
    internal void Present(Rectangle bounds)
    {
        if (!SetWindowPos(Handle, (IntPtr)(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0050))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Hover card position");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    protected override bool ShowWithoutActivation => true;
    // Native topmost style avoids Form.TopMost's focus-on-show path.
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000088; return p; } }
    public HoverCard(SavedState state, bool demo, Action open, Action refresh)
    {
        this.state = state; this.demo = demo; this.open = open; this.refresh = refresh;
        Text = "Codex 额度详情卡片"; AccessibleName = "双账号额度详情，点击查看完整面板";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ClickAt(new Point(e.X * LogicalWidth / Width, e.Y * LogicalHeight / Height));
        };
    }
    internal void ClickAt(Point point)
    {
        if (RefreshButton.Contains(point)) { if (!Busy && !demo) refresh(); return; }
        open();
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = ResidentWidget.Rounded(ClientRectangle, 12 * Width / (float)LogicalWidth);
        var old = Region; Region = new Region(path); old?.Dispose();
    }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } base.WndProc(ref m); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(Width / (float)LogicalWidth, Height / (float)LogicalHeight);
        var bg = Dark ? Color.FromArgb(30, 34, 40) : Color.FromArgb(250, 251, 253);
        var ink = Dark ? Color.FromArgb(229, 234, 241) : Color.FromArgb(33, 43, 58);
        var muted = Dark ? Color.FromArgb(145, 155, 170) : Color.FromArgb(107, 119, 138);
        var border = Dark ? Color.FromArgb(50, 56, 64) : Color.FromArgb(221, 227, 236);
        g.Clear(bg);
        using var outline = new Pen(border); using var shape = ResidentWidget.Rounded(new RectangleF(.5f, .5f, LogicalWidth - 1, LogicalHeight - 1), 12); g.DrawPath(outline, shape);
        ResidentWidget.DrawText(g, "Codex", new(16, 10, 100, 24), 13, ink, true);
        ResidentWidget.DrawText(g, demo ? "剩余额度 · 示例" : "剩余额度", new(212, 12, 92, 20), 10, muted);
        for (var i = 0; i < 2; i++)
        {
            var a = state.Accounts[i]; var y = 40 + i * 68;
            if (HighlightedAccount == i)
            {
                using var fill = new SolidBrush(Dark ? Color.FromArgb(39, 47, 51) : Color.FromArgb(236, 244, 241));
                using var row = ResidentWidget.Rounded(new RectangleF(8, y, 304, 64), 8); g.FillPath(fill, row);
            }
            ResidentWidget.DrawText(g, a.Name, new(20, y + 8, 72, 22), 12, ink, true);
            ResidentWidget.DrawText(g, a.Windows.Count == 0 ? "未登录" : a.Stale ? "数据过期" : "", new(20, y + 34, 72, 18), 9, a.Stale ? ResidentWidget.Amber : muted);
            DrawMetric(a, a.ShortWindow, a.ShortWindow?.Label ?? "短期", 100);
            DrawMetric(a, a.WeekWindow, "周", 210);
            void DrawMetric(AccountState account, QuotaWindow? window, string label, int x)
            {
                var tone = account.Stale ? muted : ResidentWidget.RingColor(account, window, x == 210, Dark);
                ResidentWidget.DrawText(g, label, new(x, y + 8, 32, 22), 10, muted);
                ResidentWidget.DrawText(g, window is null ? "—" : $"{window.Remaining:0}%", new(x + 32, y + 6, 62, 26), 18, tone, true);
                ResidentWidget.DrawText(g, "重置 " + (window?.Countdown ?? "—"), new(x, y + 35, 94, 18), 9, muted);
            }
        }
        g.DrawLine(outline, 16, 175, 304, 175);
        var updated = string.Join(" · ", state.Accounts.Select((a, i) => (i == 0 ? "A " : "B ") + (a.Updated?.ToLocalTime().ToString("HH:mm") ?? "—")));
        ResidentWidget.DrawText(g, "更新 " + updated, new(16, 179, 232, 20), 9, muted);
        ResidentWidget.DrawText(g, demo ? "示例" : Busy ? "更新中…" : "刷新", RefreshButton, 10, ink, true, true);
        AccessibleDescription = Display.Tooltip(state.Accounts, null);
    }
    internal static void DrawWindow(Graphics g, QuotaWindow? w, string label, int x, int y, int width, Color accent, Color ink, Color muted, Color track)
    {
        ResidentWidget.DrawText(g, label, new(x, y, width - 62, 24), 11, muted);
        ResidentWidget.DrawText(g, w is null ? "—" : $"{w.Remaining:0}%", new(x + width - 60, y, 60, 24), 18, ink, true);
        using var back = new SolidBrush(track); g.FillRectangle(back, x, y + 28, width, 4);
        using var fill = new SolidBrush(accent); if (w is not null) g.FillRectangle(fill, x, y + 28, (float)(width * w.Remaining / 100), 4);
        ResidentWidget.DrawText(g, "重置 " + (w?.Countdown ?? "—"), new(x, y + 36, width, 17), 10, muted);
    }
}

internal sealed class QuotaBars : Control
{
    public AccountState? Account { get; set; }
    public QuotaBars() { DoubleBuffered = true; AccessibleRole = AccessibleRole.StaticText; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Account is not {} a) return;
        var g = e.Graphics; var scale = DeviceDpi / 96f; g.ScaleTransform(scale, scale);
        var width = (int)(Width / scale); var column = Math.Max(100, (width - 18) / 2);
        var accent = ResidentWidget.Accent(a, false);
        var shortWindow = a.ShortWindow;
        HoverCard.DrawWindow(g, shortWindow, shortWindow?.Label ?? "短周期", 0, 0, column, accent, Color.FromArgb(33, 43, 58), Color.DimGray, Color.FromArgb(229, 234, 240));
        HoverCard.DrawWindow(g, a.WeekWindow, "周额度", column + 18, 0, column, accent, Color.FromArgb(33, 43, 58), Color.DimGray, Color.FromArgb(229, 234, 240));
        AccessibleName = a.Compact(false);
    }
}
