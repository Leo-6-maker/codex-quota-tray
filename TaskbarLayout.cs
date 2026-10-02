using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace CodexQuotaTray;

internal sealed record BarSnapshot(Rectangle Bar, List<Rectangle> Occupied, List<Rectangle> Traffic, bool Reliable, long ShellHandle = 0, uint Dpi = 96);

internal static class TaskbarLayout
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; public readonly Rectangle Bounds => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    delegate bool EnumWindow(IntPtr window, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window, EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colorKey, out byte alpha, out uint flags);
    public static IntPtr Shell => FindWindow("Shell_TrayWnd", null);
    public static Rectangle BoundsOf(IntPtr window) => GetWindowRect(window, out var rect) ? rect.Bounds : Rectangle.Empty;
    public static bool IsEmbedded(IntPtr window) => Shell != IntPtr.Zero && GetParent(window) == Shell;
    public static bool IsCurrent(BarSnapshot snapshot) => snapshot.ShellHandle != 0 && Shell.ToInt64() == snapshot.ShellHandle &&
        IsWindow((IntPtr)snapshot.ShellHandle) && BoundsOf((IntPtr)snapshot.ShellHandle) == snapshot.Bar && GetDpiForWindow((IntPtr)snapshot.ShellHandle) == snapshot.Dpi;
    public static void Mount(IntPtr window, BarSnapshot snapshot, Rectangle bounds, Color transparentKey)
    {
        if (!IsCurrent(snapshot) || bounds.IsEmpty || !snapshot.Bar.Contains(bounds)) throw new InvalidOperationException("任务栏正在变化，等待稳定后恢复。");
        var parent = (IntPtr)snapshot.ShellHandle;
        var reparent = GetParent(window) != parent;
        if (reparent)
        {
            var style = GetWindowLongPtr(window, -16).ToInt64();
            SetWindowLongPtr(window, -16, (IntPtr)((style & ~0x80000000L) | 0x40000000L));
            SetParent(window, parent);
            if (GetParent(window) != parent) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetParent");
            // Rebuild the composited surface only after an actual parent change.
            var extended = GetWindowLongPtr(window, -20).ToInt64();
            SetWindowLongPtr(window, -20, (IntPtr)(extended & ~0x00080000L));
            SetWindowLongPtr(window, -20, (IntPtr)(extended | 0x00080000L));
        }
        var key = (uint)(transparentKey.R | transparentKey.G << 8 | transparentKey.B << 16);
        if ((!GetLayeredWindowAttributes(window, out var oldKey, out var alpha, out var flags) || oldKey != key || alpha != 255 || flags != 3) &&
            !SetLayeredWindowAttributes(window, key, 255, 3)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetLayeredWindowAttributes");
        if (BoundsOf(window) != bounds || reparent)
        {
            var location = bounds.Location;
            if (!ScreenToClient(parent, ref location) || !SetWindowPos(window, IntPtr.Zero, location.X, location.Y, bounds.Width, bounds.Height, 0x0030))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Taskbar position");
        }
        if (!IsCurrent(snapshot) || GetParent(window) != parent || BoundsOf(window) != bounds)
            throw new InvalidOperationException("任务栏挂载位置已变化，等待重新定位。");
    }
    public static float PrimaryScale => Math.Max(96, GetDpiForWindow(Shell)) / 96f;
    public static object WindowInfo(IntPtr window) => new { Handle = window.ToInt64(), Parent = GetParent(window).ToInt64(), Visible = IsWindowVisible(window), Bounds = BoundsOf(window), Dpi = GetDpiForWindow(window), Style = GetWindowLongPtr(window, -16).ToInt64().ToString("X"), Awareness = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(window)) };
    public static List<object> MonitorWindows()
    {
        var ids = Process.GetProcessesByName("CodexQuotaTray").Select(p => { using (p) return p.Id; }).ToHashSet();
        var windows = new List<object>();
        bool Inspect(IntPtr window, IntPtr _)
        {
            GetWindowThreadProcessId(window, out var pid);
            if (ids.Contains((int)pid))
            {
                var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
                windows.Add(new { Title = title.ToString(), Process = pid, Info = WindowInfo(window) });
            }
            return true;
        }
        EnumWindows((window, _) => { Inspect(window, IntPtr.Zero); EnumChildWindows(window, Inspect, IntPtr.Zero); return true; }, IntPtr.Zero);
        return windows;
    }
    public static bool IsTaskbarAbove(IntPtr window)
    {
        var shell = Shell;
        if (shell == IntPtr.Zero) return false;
        for (var previous = GetWindow(window, 3); previous != IntPtr.Zero; previous = GetWindow(previous, 3))
            if (previous == shell) return true;
        return false;
    }

    public static List<Rectangle> ResidentBounds(out bool taskbarAbove, out bool embedded, out List<object> rendering)
    {
        var ids = Process.GetProcessesByName("CodexQuotaTray").Select(p => { using (p) return p.Id; }).ToHashSet();
        var bounds = new List<Rectangle>();
        var paints = new List<object>();
        var covered = false; var mounted = false;
        bool Inspect(IntPtr window, IntPtr _)
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!ids.Contains((int)pid) || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(64); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "Codex 额度胶囊" && GetWindowRect(window, out var rect))
            {
                bounds.Add(rect.Bounds); mounted |= IsEmbedded(window); covered |= !IsEmbedded(window) && IsTaskbarAbove(window);
                GetClientRect(window, out var client);
                var transparent = GetLayeredWindowAttributes(window, out var colorKey, out var alpha, out var flags) && (flags & 1) != 0 && alpha == 255;
                paints.Add(new { Style = GetWindowLongPtr(window, -16).ToInt64().ToString("X"), ExtendedStyle = GetWindowLongPtr(window, -20).ToInt64().ToString("X"), ClientBounds = client.Bounds, TransparentBackground = transparent, RingPixels = VisibleRingPixels(rect.Bounds) });
            }
            return true;
        }
        EnumWindows(Inspect, IntPtr.Zero); EnumChildWindows(Shell, Inspect, IntPtr.Zero);
        taskbarAbove = covered; embedded = mounted; rendering = paints; return bounds;
    }

    // Read only our ring area on the displayed desktop, not a DrawToBitmap preview.
    public static int[] VisibleRingPixels(Rectangle bounds)
    {
        var counts = new int[2];
        if (bounds.Width <= 0 || bounds.Height <= 0) return counts;
        var palette = new[] { ResidentWidget.Mint, ResidentWidget.Blue, ResidentWidget.LightMint, ResidentWidget.LightBlue, ResidentWidget.Amber, ResidentWidget.Neutral, ResidentWidget.TrackColor(true), ResidentWidget.TrackColor(false) };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            for (var slot = 0; slot < 2; slot++)
                foreach (var weekly in new[] { false, true })
                    for (var angle = 0; angle < 360; angle += 45)
                {
                    var ring = ResidentWidget.RingBounds(slot, weekly);
                    var x = (int)Math.Round((ring.X + ring.Width / 2 + ring.Width / 2 * Math.Cos(angle * Math.PI / 180)) * bounds.Width / ResidentWidget.LogicalWidth);
                    var y = (int)Math.Round((ring.Y + ring.Height / 2 + ring.Height / 2 * Math.Sin(angle * Math.PI / 180)) * bounds.Height / ResidentWidget.LogicalHeight);
                    var pixel = GetPixel(dc, bounds.Left + x, bounds.Top + y);
                    var r = (int)(pixel & 255); var g = (int)((pixel >> 8) & 255); var b = (int)((pixel >> 16) & 255);
                    if (palette.Any(c => Math.Abs(c.R-r) <= 3 && Math.Abs(c.G-g) <= 3 && Math.Abs(c.B-b) <= 3)) counts[slot]++;
                }
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        return counts;
    }

    public static BarSnapshot Read()
    {
        var shell = FindWindow("Shell_TrayWnd", null);
        var bar = GetWindowRect(shell, out var r) ? r.Bounds : Rectangle.Empty;
        var dpi = GetDpiForWindow(shell);
        var occupied = new List<Rectangle>();
        var traffic = new List<Rectangle>();
        var trafficIds = Process.GetProcessesByName("TrafficMonitor").Select(p => { using (p) return p.Id; }).ToHashSet();
        var monitorIds = Process.GetProcessesByName("CodexQuotaTray").Select(p => { using (p) return p.Id; }).ToHashSet();
        bool Inspect(IntPtr window, IntPtr _)
        {
            GetWindowThreadProcessId(window, out var pid);
            if (trafficIds.Contains((int)pid) && IsWindowVisible(window) && GetWindowRect(window, out var rect) && rect.Bounds.Width > 0 && rect.Bounds.Height > 0)
                traffic.Add(rect.Bounds);
            return true;
        }
        EnumWindows((window, _) => { Inspect(window, IntPtr.Zero); EnumChildWindows(window, Inspect, IntPtr.Zero); return true; }, IntPtr.Zero);
        var reliable = false;
        if (shell != IntPtr.Zero && bar.Width > bar.Height)
        {
            try
            {
                var root = AutomationElement.FromHandle(shell);
                var items = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                for (var i = 0; i < items.Count; i++)
                {
                    var current = items[i].Current;
                    if (monitorIds.Contains(current.ProcessId)) continue; // Our mounted rings do not reserve a second slot.
                    if (current.IsOffscreen) continue;
                    var b = current.BoundingRectangle;
                    if (b.IsEmpty || !double.IsFinite(b.X) || !double.IsFinite(b.Y)) continue;
                    var bounds = Rectangle.FromLTRB((int)Math.Floor(b.Left), (int)Math.Floor(b.Top), (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom));
                    // Reserve actual controls, never the whole taskbar's container rectangle.
                    if (bounds.Width > 0 && bounds.Width < bar.Width / 2 && bounds.Height > 0 && bounds.IntersectsWith(bar) &&
                        current.ControlType != ControlType.Pane && current.ControlType != ControlType.ToolBar && current.ControlType != ControlType.Window)
                        occupied.Add(bounds);
                }
                reliable = occupied.Count > 0;
            }
            catch { /* Explorer may restart: remain hidden until its layout is readable. */ }
            EnumChildWindows(shell, (window, _) =>
            {
                var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
                if (IsWindowVisible(window) && GetWindowRect(window, out var child) && cls.ToString() is "TrayNotifyWnd" or "TrayClockWClass") occupied.Add(child.Bounds);
                return true;
            }, IntPtr.Zero);
        }
        occupied.AddRange(traffic);
        return new(bar, occupied, traffic, reliable, shell.ToInt64(), dpi);
    }

    public static Rectangle Place(BarSnapshot snapshot, Rectangle screen, Size size, int gap)
    {
        var bar = snapshot.Bar;
        var horizontal = bar.Width > bar.Height && bar.IntersectsWith(screen);
        var obstacles = snapshot.Occupied.Select(r => Rectangle.Inflate(r, gap, gap)).ToList();
        if (horizontal && snapshot.Reliable && bar.Height >= size.Height)
        {
            var y = bar.Top + (bar.Height - size.Height) / 2;
            // ponytail: one capsule on the primary taskbar; scan the free gaps, no shell injection.
            for (var x = bar.Right - size.Width - gap; x >= bar.Left + gap; x -= Math.Max(1, gap))
            {
                var candidate = new Rectangle(x, y, size.Width, size.Height);
                if (!obstacles.Any(o => o.IntersectsWith(candidate))) return candidate;
            }
        }
        return Rectangle.Empty; // No desktop overlay: retry the taskbar, while the tray remains available.
    }
}
