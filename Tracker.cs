using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AfterHours;

internal sealed class Tracker : IDisposable
{
    private readonly History history;
    private readonly HwndSource receiver;
    private readonly DispatcherTimer timer;
    private readonly WinEvent callback;
    private readonly IntPtr foregroundHook, destroyHook;
    private readonly Dictionary<IntPtr, string> windows = new();
    private readonly Dictionary<uint, (string Name, string Browser)> identities = new();
    private HashSet<string> excludedApps = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> excludedSites = new(StringComparer.OrdinalIgnoreCase);
    private (string Browser, string Site, IntPtr Window, long Until, bool Video) website;
    private (long Start, long Tick, string App, string Site, int Visit, string Session, long VideoUntil)? previous;
    private string? session;
    private string? lastSite;
    private bool suspended, locked;
    private int samples;
    private IntPtr focused;
    private uint focusedPid;
    private (string Name, string Browser) currentApp;

    public bool Paused { get; private set; }
    public bool SitesEnabled { get; private set; }
    public bool VideoEnabled { get; private set; }
    public int IdleMinutes { get; private set; }
    public bool IsActive => session != null;
    public bool IsUnavailable => locked || suspended;
    public event Action? Changed;

    public Tracker(History history)
    {
        this.history = history;
        LoadSettings();
        Paused = history.Get("paused", "0") == "1";
        locked = !IsDefaultDesktop();
        // Power broadcasts need a top-level window. Omit WS_VISIBLE so the receiver stays off screen.
        receiver = new HwndSource(new HwndSourceParameters("AfterHours activity notifications", 0, 0)
        {
            WindowStyle = 0
        });
        receiver.AddHook(OnMessage);
        if (!RegisterSession(receiver.Handle, 0))
            throw new InvalidOperationException("Windows could not register lock and unlock notifications.");
        callback = OnWinEvent;
        foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
        destroyHook = SetWinEventHook(0x8001, 0x8002, IntPtr.Zero, callback, 0, 0, 0);
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (++samples % 5 == 0 && !Paused && !IsUnavailable)
            {
                if (samples % 60 == 0) identities.Clear();
                RefreshWindows();
                history.Flush();
            }
            Sample();
            Changed?.Invoke();
        };
        Reset();
        timer.Start();
    }

    public void LoadSettings()
    {
        IdleMinutes = int.TryParse(history.Get("idle", "5"), out var n) ? Math.Clamp(n, 1, 60) : 5;
        SitesEnabled = history.Get("sites", "0") == "1";
        VideoEnabled = history.Get("video", "0") == "1";
        excludedApps = ParseList(history.Get("apps_excluded", ""));
        excludedSites = ParseList(history.Get("sites_excluded", ""));
        website = default;
    }

    private static HashSet<string> ParseList(string value) =>
        new(value.Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    public void TogglePause()
    {
        Commit(DateTimeOffset.UtcNow, Environment.TickCount64);
        history.Flush();
        Paused = !Paused;
        history.Set("paused", Paused ? "1" : "0");
        Reset();
        Changed?.Invoke();
    }

    public void Reset()
    {
        previous = null;
        session = null;
        lastSite = null;
        website = default;
        focused = IntPtr.Zero;
        focusedPid = 0;
        windows.Clear();
        if (!Paused && !IsUnavailable) RefreshWindows();
        Sample();
    }

    public void BrowserUpdate(string browser, string host, bool video = false)
    {
        if (Paused || IsUnavailable || !SitesEnabled) return;
        var hwnd = GetForegroundWindow();
        if (host.Length == 0)
        {
            if (website.Browser == browser) website = default;
            Sample();
            return;
        }
        if (hwnd == IntPtr.Zero || Identify(hwnd).Browser != browser) return;
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Length > 253) return;
        website = (browser, host, hwnd, Environment.TickCount64 + 47_000, video);
        Sample();
    }

    public static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Sample()
    {
        var now = DateTimeOffset.UtcNow;
        long tick = Environment.TickCount64;
        if (Paused || IsUnavailable) return;
        var idle = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref idle)) { previous = null; session = null; lastSite = null; return; }
        var idleMs = unchecked((uint)tick - idle.Tick);
        long limit = IdleMinutes * 60_000L;
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (hwnd != focused || pid != focusedPid)
        {
            focused = hwnd;
            focusedPid = pid;
            currentApp = Identify(hwnd);
            if (website.Window != hwnd) website = default;
            if (IsMainWindow(hwnd) && currentApp.Name.Length > 0) windows[hwnd] = currentApp.Name;
        }
        var app = currentApp;
        if (app.Name == null || excludedApps.Contains(app.Name)) app = ("", "");
        string site = "";
        if (SitesEnabled && website.Window == hwnd && website.Browser == app.Browser && tick < website.Until &&
            website.Site is { Length: > 0 } domain && !excludedSites.Any(e => domain.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                domain.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))) site = domain;
        bool video = VideoEnabled && site.Length > 0 && website.Video &&
            (idleMs < limit || previous is { VideoUntil: > 0 });
        history.Observe(Day(now.LocalDateTime));
        if (idleMs >= limit && !video)
        {
            long cutoff = Math.Max(tick - (idleMs - limit), Math.Min(tick, previous?.VideoUntil ?? 0));
            Commit(now.AddMilliseconds(cutoff - tick), cutoff);
            previous = null;
            session = null;
            lastSite = null;
            return;
        }
        Commit(now, tick);
        session ??= Guid.NewGuid().ToString("N");
        int visit = site.Length > 0 && site != lastSite ? 1 : 0;
        if (previous is { } old && old.Start == now.ToUnixTimeMilliseconds() && old.Site == site)
            visit = old.Visit;
        lastSite = site;
        previous = (now.ToUnixTimeMilliseconds(), tick, app.Name ?? "", site, visit, session, video ? website.Until : 0);
    }

    private void Commit(DateTimeOffset now, long tick)
    {
        if (previous is not { } a) return;
        long elapsed = tick - a.Tick;
        if (elapsed < 0 || elapsed > 5_000) { session = null; lastSite = null; return; }
        long end = now.ToUnixTimeMilliseconds();
        if (end < a.Start || Math.Abs(end - a.Start - elapsed) > 1_500)
        {
            session = null;
            lastSite = null;
            return;
        }
        long start = a.Start;
        int visit = a.Visit;
        while (start < end)
        {
            DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(start).LocalDateTime;
            long midnight = new DateTimeOffset(local.Date.AddDays(1)).ToUnixTimeMilliseconds();
            long stop = midnight > start ? Math.Min(midnight, end) : end;
            history.Record(new Activity(Day(local), start, stop, a.Session, a.App, a.Site, visit));
            visit = 0;
            start = stop;
        }
    }

    private void RefreshWindows()
    {
        foreach (var hwnd in windows.Keys.Where(hwnd => !IsWindow(hwnd)).ToArray()) windows.Remove(hwnd);
        EnumWindows((hwnd, _) =>
        {
            if (windows.ContainsKey(hwnd) || !IsMainWindow(hwnd)) return true;
            string name = Identify(hwnd).Name;
            if (name.Length > 0) windows[hwnd] = name;
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsMainWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == IntPtr.Zero &&
        (GetWindowLong(hwnd, -20) & 0x80) == 0 && !IsShellWindow(hwnd) &&
        (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) != 0 || cloaked == 0);

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private (string Name, string Browser) Identify(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return ("", "");
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId || pid == 0) return ("", "");
        if (identities.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string exe = process.ProcessName;
            string browser = exe.ToLowerInvariant() switch { "chrome" => "chrome", "msedge" => "edge", _ => "" };
            if (exe is "ShellExperienceHost" or "StartMenuExperienceHost" or "SearchHost" or "LockApp" or "dwm")
                return ("", "");
            string name = exe == "explorer" ? "File Explorer" : exe;
            try { name = process.MainModule?.FileVersionInfo.FileDescription?.Trim() is { Length: > 0 } description ? description : name; }
            catch (System.ComponentModel.Win32Exception) { }
            if (exe == "explorer") name = "File Explorer";
            return identities[pid] = (name, browser);
        }
        catch (ArgumentException) { return ("", ""); }
        catch (InvalidOperationException) { return ("", ""); }
        catch (System.ComponentModel.Win32Exception) { return ("", ""); }
    }

    private void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (Paused || IsUnavailable || obj != 0 || child != 0) return;
        if (type == 3) Sample();
        else if (type == 0x8002)
        {
            if (IsMainWindow(hwnd))
            {
                string app = Identify(hwnd).Name;
                if (app.Length > 0) windows[hwnd] = app;
            }
        }
        else if (windows.Remove(hwnd, out var name) && !excludedApps.Contains(name) &&
            !windows.ContainsValue(name))
            history.Closed(Day(DateTime.Now), name);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        int code = unchecked((int)wParam.ToInt64());
        if (message == 0x02B1 && (code is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8))
        {
            bool next = code is 2 or 4 or 6 or 7;
            if (locked != next)
            {
                if (next) StopAtBoundary();
                locked = next;
                Reset();
            }
        }
        if (message == 0x0218 && (code is 4 or 7 or 18))
        {
            if (code == 4) StopAtBoundary();
            suspended = code == 4;
            Reset();
        }
        return IntPtr.Zero;
    }

    private void StopAtBoundary()
    {
        Commit(DateTimeOffset.UtcNow, Environment.TickCount64);
        history.Flush();
    }

    private static bool IsDefaultDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, 2, name, 256, out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    public void Dispose()
    {
        timer.Stop();
        StopAtBoundary();
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        if (destroyHook != IntPtr.Zero) UnhookWinEvent(destroyHook);
        UnregisterSession(receiver.Handle);
        receiver.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }
    private delegate void WinEvent(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint time);
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int result, int size);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification")] private static extern bool RegisterSession(IntPtr hwnd, int flags);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification")] private static extern bool UnregisterSession(IntPtr hwnd);
}
