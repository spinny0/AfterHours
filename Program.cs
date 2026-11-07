using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AfterHours;

internal static class Program
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault()?.StartsWith("chrome-extension://", StringComparison.Ordinal) == true)
        {
            BrowserBridge.RunHost();
            return 0;
        }

        string scope = $@"Local\AfterHours.{Environment.UserName}";
        using var singleInstance = new Mutex(true, scope, out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, scope + ".Show");
        if (!first)
        {
            if (!args.Contains("--background")) showSignal.Set();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // ThemeMode enables the built-in Windows Fluent styles.
        app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        using var stop = new CancellationTokenSource();
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            MessageBox.Show(e.Exception.Message, "AfterHours", MessageBoxButton.OK, MessageBoxImage.Error);
            app.Shutdown(1);
        };

        try
        {
            string folder = Environment.GetEnvironmentVariable("AFTERHOURS_DATA_DIR") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfterHours");
            Directory.CreateDirectory(folder);
            using var history = new History(Path.Combine(folder, "history.sqlite"));
            var insights = new Insights(history);
            bool policiesRemoved = LegacyBrowserPolicies.Remove(history);
            history.RemoveSetting("limit_password_v1");
            RefreshStartupPath();
            using var tracker = new Tracker(history);
            var window = new Dashboard(history, tracker, insights, folder);
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon != null)
                window.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            using var trayIconStream = typeof(Program).Assembly.GetManifestResourceStream("AfterHours.TrayIcon.ico")!;
            using var trayIcon = new System.Drawing.Icon(trayIconStream, Forms.SystemInformation.SmallIconSize);
            using var tray = new Forms.NotifyIcon { Icon = trayIcon, Visible = true };
            using var menu = new Forms.ContextMenuStrip();
            tray.ContextMenuStrip = menu;
            menu.Items.Add("Open AfterHours", null, (_, _) => window.Reveal());
            var pause = menu.Items.Add("Pause tracking", null, (_, _) => tracker.TogglePause());
            menu.Items.Add("Quit AfterHours", null, (_, _) => app.Shutdown());
            tray.DoubleClick += (_, _) => window.Reveal();
            tray.BalloonTipClicked += (_, _) => window.RevealLimits();
            void UpdateTray()
            {
                tray.Text = tracker.Paused ? "AfterHours · Paused" : "AfterHours · Tracking on this PC";
                pause.Text = tracker.Paused ? "Resume tracking" : "Pause tracking";
            }
            tracker.Changed += UpdateTray;
            if (!policiesRemoved)
                tray.ShowBalloonTip(10000, "AfterHours · Upgrade", "Some old browser rules could not be removed. Close the older version and reopen AfterHours to retry.", Forms.ToolTipIcon.Warning);
            var limitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            limitTimer.Tick += (_, _) =>
            {
                window.UpdateLimitUsage();
                if (tracker.Paused || tracker.IsUnavailable) return;
                var reached = insights.CheckReminders(DateTime.Today);
                if (reached.Count == 0) return;
                string message = reached.Count == 1
                    ? $"{insights.LimitName(reached[0])} reached its daily limit of {reached[0].Minutes} min."
                    : $"{reached.Count} daily limits reached. Open AfterHours to see today’s usage.";
                tray.ShowBalloonTip(10000, "AfterHours · Time limit", message, Forms.ToolTipIcon.Info);
            };
            limitTimer.Start();
            UpdateTray();
            var wait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) =>
                app.Dispatcher.BeginInvoke(window.Reveal), null, Timeout.Infinite, false);
            _ = BrowserBridge.Listen(message =>
                app.Dispatcher.InvokeAsync(() =>
                {
                    if (message.OpenLimits) window.RevealLimits();
                    else window.ReceiveBrowser(message);

                }).Task, stop.Token);

            app.Exit += (_, _) =>
            {
                window.AllowClose = true;
                limitTimer.Stop();
                stop.Cancel();
                wait.Unregister(null);
                tray.Visible = false;
            };
            if (!args.Contains("--background")) window.Reveal();
            return app.Run();
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "AfterHours", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    public static bool StartsWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return (key?.GetValue("AfterHours") as string) == StartupCommand;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("AfterHours", StartupCommand);
        else key.DeleteValue("AfterHours", false);
    }

    private static void RefreshStartupPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue("AfterHours") is not string current || current == StartupCommand) return;
            if (current.StartsWith('"') && current.EndsWith("\\AfterHours.exe\" --background", StringComparison.OrdinalIgnoreCase))
                key.SetValue("AfterHours", StartupCommand);
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" --background";
}
