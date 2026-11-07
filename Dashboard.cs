using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace AfterHours;

internal sealed partial class Dashboard : Window
{
    private const string PrimaryText = "TextFillColorPrimaryBrush";
    private const string SecondaryText = "TextFillColorSecondaryBrush";
    private const string AccentBrush = "AccentFillColorDefaultBrush";
    private const string Divider = "DividerStrokeColorDefaultBrush";
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");
    private static readonly (int Days, string Label)[] Periods = [(1, "Today"), (7, "Last 7 days"), (30, "Last 30 days")];
    private static readonly Style NavigationStyle = (Style)XamlReader.Parse("""
        <Style TargetType="ListBoxItem" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Setter Property="Margin" Value="0,1"/>
          <Setter Property="Padding" Value="13,0"/>
          <Setter Property="FocusVisualStyle" Value="{DynamicResource DefaultControlFocusVisualStyle}"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Grid Height="40">
                  <Border x:Name="surface" CornerRadius="4" Background="Transparent"/>
                  <Border x:Name="indicator" Width="3" Height="16" CornerRadius="1.5"
                          HorizontalAlignment="Left" Background="{DynamicResource AccentFillColorDefaultBrush}" Visibility="Collapsed"/>
                  <ContentPresenter Margin="{TemplateBinding Padding}" VerticalAlignment="Center"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="surface" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/>
                    <Setter TargetName="indicator" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="surface" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/>
                  </Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True">
                    <Setter TargetName="surface" Property="BorderBrush" Value="{DynamicResource FocusStrokeColorOuterBrush}"/>
                    <Setter TargetName="surface" Property="BorderThickness" Value="1"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    private readonly History history;
    private readonly Tracker tracker;
    private readonly Insights insights;
    private readonly string folder;
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox navigation = new();
    private readonly StackPanel body = new() { MaxWidth = 1120, Margin = new Thickness(28, 22, 28, 24) };
    private readonly StackPanel report = new();
    private readonly TextBlock heading = Label("Overview", 32, bold: true);
    private readonly TextBlock state = Label("", 12, secondary: true);
    private readonly ComboBox period;
    private readonly Dictionary<string, DateTime> browsers = new();
    private string page = "Overview";
    private int days = 1;
    private int refresh;
    private DateTime? selectedDay;
    private bool byTime;
    private TextBlock? connection;
    public bool AllowClose { get; set; }

    public Dashboard(History history, Tracker tracker, Insights insights, string folder)
    {
        this.history = history;
        this.tracker = tracker;
        this.insights = insights;
        this.folder = folder;
        Title = "AfterHours";
        Width = 1080;
        Height = 800;
        MinWidth = 750;
        MinHeight = 500;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, PrimaryText);

        period = Choice("Date range", Periods.Select(p => p.Label), 0, 136);
        period.SelectionChanged += (_, _) =>
        {
            if (period.SelectedIndex < 0) return;
            days = Periods[period.SelectedIndex].Days;
            RenderReport();
        };
        var header = Split(heading, period);
        header.Margin = new Thickness(0, 0, 0, 20);
        body.Children.Add(header);
        body.Children.Add(report);
        scroll.Content = body;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(BuildRail());
        var content = new Border { Child = scroll, CornerRadius = new CornerRadius(8, 0, 0, 0),
            BorderThickness = new Thickness(1, 1, 0, 0) };
        content.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        content.SetResourceReference(Border.BorderBrushProperty, Divider);
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;

        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
        tracker.Changed += () =>
        {
            UpdateState();
            if (IsVisible && UsesPeriod(page) && !report.IsKeyboardFocusWithin &&
                Mouse.LeftButton == MouseButtonState.Released && ++refresh % 5 == 0)
                RenderReport();
        };
        UpdateState();
        RenderReport();
    }

    public void Reveal()
    {
        if (page is not ("Settings" or "Categories" or "Time limits")) RenderReport();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ReceiveBrowser(BrowserMessage message)
    {
        browsers[message.Browser] = DateTime.UtcNow;
        if (message.Focused) tracker.BrowserUpdate(message.Browser, message.Host, message.Video);
        UpdateState();
    }

    private DockPanel BuildRail()
    {
        var rail = new DockPanel { Margin = new Thickness(10, 14, 10, 14) };
        var footer = new StackPanel { Margin = new Thickness(13, 0, 6, 0) };
        footer.Children.Add(state);
        DockPanel.SetDock(footer, Dock.Bottom);
        rail.Children.Add(footer);

        navigation.Background = Brushes.Transparent;
        navigation.BorderThickness = new Thickness(0);
        navigation.Padding = new Thickness(0);
        navigation.ItemContainerStyle = NavigationStyle;
        AutomationProperties.SetName(navigation, "Pages");
        foreach (var (title, glyph) in new[]
        {
            ("Overview", "\uE80F"), ("Day details", "\uE787"), ("Weekly recap", "\uE9D9"),
            ("Apps", "\uE71D"), ("Websites", "\uE774"), ("Categories", "\uE8EC"),
            ("Time limits", "\uE916"), ("Settings", "\uE713")
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(NavigationIcon(glyph));
            row.Children.Add(Label(title));
            var item = new ListBoxItem { Content = row, Tag = title };
            AutomationProperties.SetName(item, title);
            navigation.Items.Add(item);
        }
        navigation.SelectedIndex = 0;
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is not ListBoxItem { Tag: string next }) return;
            page = next;
            heading.Text = next;
            period.Visibility = UsesPeriod(page) ? Visibility.Visible : Visibility.Collapsed;
            RenderReport();
            scroll.ScrollToTop();
        };
        rail.Children.Add(navigation);
        return rail;
    }

    private void UpdateState()
    {
        state.Text = tracker.Paused ? "Tracking paused" : tracker.IsUnavailable ? "PC locked or asleep" :
            tracker.IsActive ? "Tracking on" : "Idle";
        if (connection != null) connection.Text = ConnectionText();
    }

    private void RenderReport()
    {
        connection = null;
        refreshLimits = null;
        report.Children.Clear();
        switch (page)
        {
            case "Settings": Settings(report); return;
            case "Day details": DayDetails(report); return;
            case "Weekly recap": WeeklyRecap(report); return;
            case "Categories": CategoryPage(report); return;
            case "Time limits": LimitsPage(report); return;
        }

        DateTime today = DateTime.Today;
        string first = Tracker.Day(today.AddDays(1 - days)), last = Tracker.Day(today);
        var rows = history.Read(first, last);
        var apps = Rank(rows, x => x.App);
        var sites = Rank(rows, x => x.Site);
        var closes = history.Closes(first, last);
        switch (page)
        {
            case "Apps":
                Ranking(report, "Most used", apps, a => Duration(a.Milliseconds), "No app activity recorded.");
                Ranking(report, "Most closed", closes, a => Quantity(a.Count, "close"), "No app window closes recorded.",
                    "A close is counted when an app’s last tracked main window closes.");
                break;
            case "Websites":
                Websites(report, sites);
                break;
            default:
                Overview(report, rows, apps, sites, closes, today);
                break;
        }
    }

    private void Overview(Panel panel, List<Activity> rows, List<Tally> apps, List<Tally> sites, List<Tally> closes, DateTime today)
    {
        int averageDays = days == 30 ? 30 : 7;
        string first = Tracker.Day(today.AddDays(-averageDays)), last = Tracker.Day(today.AddDays(-1));
        int covered = history.Observed(first, last).Count;
        long average = covered == 0 ? 0 : history.Read(first, last).Sum(Length) / covered;
        var metrics = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
        metrics.Children.Add(Metric("Active time", Duration(rows.Sum(Length)),
            "Time before the idle cutoff while Windows was unlocked."));
        metrics.Children.Add(Metric("Sessions", rows.Select(x => x.Session).Distinct().Count().ToString("N0"),
            "Stretches of use separated by idle time, lock, or tracking gaps."));
        metrics.Children.Add(Metric("Daily average", covered == 0 ? "—" : Duration(average),
            $"Based on {covered} of the last {averageDays} completed days with tracking data."));
        panel.Children.Add(metrics);

        int chartDays = days == 30 ? 30 : 7;
        var dates = Enumerable.Range(0, chartDays).Select(i => today.AddDays(i + 1 - chartDays)).ToList();
        var tallies = history.Read(Tracker.Day(dates[0]), Tracker.Day(today))
            .GroupBy(a => a.Day).ToDictionary(g => g.Key, g => g.Sum(Length));
        var observed = history.Observed(Tracker.Day(dates[0]), Tracker.Day(today)).ToHashSet();
        var chart = new StackPanel();
        chart.Children.Add(Split(Label("Daily activity", 16, bold: true), Label($"Last {chartDays} days", 12, secondary: true)));
        var trend = new TrendChart(dates, tallies, observed)
        {
            ToolTip = "Select a day to open its timeline. A dash marks a day without tracking data.",
            Cursor = Cursors.Hand
        };
        trend.MouseLeftButtonUp += (_, e) =>
        {
            if (trend.ActualWidth <= 0) return;
            int index = Math.Clamp((int)(e.GetPosition(trend).X * dates.Count / trend.ActualWidth), 0, dates.Count - 1);
            selectedDay = dates[index];
            OpenPage("Day details");
        };
        chart.Children.Add(trend);
        panel.Children.Add(Card(chart, new Thickness(16, 12, 16, 8)));

        var timeline = new StackPanel();
        var timelinePicker = DateChoice("Timeline date", selectedDay ?? today);
        var timelineControls = new StackPanel { Orientation = Orientation.Horizontal };
        timelinePicker.Margin = new Thickness(0, 0, 8, 0);
        timelineControls.Children.Add(timelinePicker);
        timelineControls.Children.Add(ActionButton("View day", () => OpenPage("Day details")));
        timeline.Children.Add(Split(Label("Day timeline", 16, bold: true), timelineControls));
        var chartHost = new StackPanel();
        timeline.Children.Add(chartHost);
        void UpdateTimeline()
        {
            DateTime date = selectedDay ?? DateTime.Today;
            string day = Tracker.Day(date);
            var segments = history.Read(day, day);
            chartHost.Children.Clear();
            chartHost.Children.Add(new DayChart(date, segments));
            chartHost.Children.Add(Label(segments.Count == 0 ? "No activity recorded." :
                $"{Duration(segments.Sum(Length))} active · {Quantity(segments.Select(a => a.Session).Distinct().Count(), "session")}",
                12, secondary: true));
        }
        timelinePicker.SelectedDateChanged += (_, _) =>
        {
            selectedDay = timelinePicker.SelectedDate;
            UpdateTimeline();
        };
        UpdateTimeline();
        panel.Children.Add(Card(timeline, new Thickness(16, 12, 16, 12)));

        AddHeading(panel, "Top activity");
        var top = new StackPanel();
        top.Children.Add(ValueRow("Top app", apps.FirstOrDefault()?.Name ?? "—"));
        top.Children.Add(ValueRow("Top website", sites.OrderByDescending(s => s.Count).FirstOrDefault()?.Name ?? "—", separated: true));
        top.Children.Add(ValueRow("Most closed app", closes.FirstOrDefault()?.Name ?? "—", separated: true));
        panel.Children.Add(Card(top, new Thickness(0)));
    }

    private void Websites(Panel panel, List<Tally> sites)
    {
        connection = Label(ConnectionText(), 13, secondary: true);
        var status = new StackPanel();
        status.Children.Add(Label("Browser connection", 14, bold: true));
        status.Children.Add(connection);
        panel.Children.Add(Card(Split(status, ActionButton("Settings", () => OpenPage("Settings"))),
            new Thickness(16, 10, 16, 10)));

        var sort = Choice("Sort websites", ["By visits", "By active time"], byTime ? 1 : 0, 136);
        sort.SelectionChanged += (_, _) =>
        {
            byTime = sort.SelectedIndex == 1;
            RenderReport();
        };
        var header = Split(Label("Top websites", 16, bold: true), sort);
        header.Margin = new Thickness(0, 8, 0, 8);
        panel.Children.Add(header);
        IEnumerable<Tally> sorted = byTime ? sites : sites.OrderByDescending(a => a.Count).ThenByDescending(a => a.Milliseconds);
        panel.Children.Add(RankingRows(sorted,
            a => byTime ? $"{Duration(a.Milliseconds)} · {Quantity(a.Count, "visit")}" :
                $"{Quantity(a.Count, "visit")} · {Duration(a.Milliseconds)}", "No website activity recorded."));
    }

    private string ConnectionText()
    {
        if (!tracker.SitesEnabled) return "Website tracking is off.";
        var active = browsers.Where(b => DateTime.UtcNow - b.Value < TimeSpan.FromSeconds(75))
            .Select(b => b.Key == "edge" ? "Edge" : "Chrome").Order().ToArray();
        return active.Length == 0 ? "Waiting for Chrome or Edge." : $"Connected to {string.Join(" and ", active)}.";
    }

    private void Settings(Panel panel)
    {
        AddHeading(panel, "Tracking");
        var pause = ActionButton(tracker.Paused ? "Resume" : "Pause", tracker.TogglePause);
        tracker.Changed += UpdatePause;
        pause.Unloaded += (_, _) => tracker.Changed -= UpdatePause;
        void UpdatePause() => pause.Content = tracker.Paused ? "Resume" : "Pause";
        panel.Children.Add(Setting("Activity tracking", pause));
        panel.Children.Add(Setting("Start with Windows", Check("Start with Windows", Program.StartsWithWindows(), enabled =>
        {
            Program.SetStartup(enabled);
            return true;
        })));

        int[] cutoffs = [1, 2, 5, 10, 15, 30, 60];
        var idle = Choice("Idle cutoff", cutoffs.Select(minutes => $"{minutes} min"), Array.IndexOf(cutoffs, tracker.IdleMinutes), 110);
        idle.SelectionChanged += (_, _) => Perform(() =>
        {
            if (idle.SelectedIndex < 0) return;
            int cutoff = cutoffs[idle.SelectedIndex];
            if (cutoff == tracker.IdleMinutes) return;
            history.Set("idle", cutoff.ToString(CultureInfo.InvariantCulture));
            tracker.LoadSettings();
        });
        panel.Children.Add(Setting("Idle cutoff", idle, "Time without keyboard or mouse input before tracking pauses."));

        AddHeading(panel, "Websites");
        panel.Children.Add(Setting("Track website domains", Check("Track website domains", tracker.SitesEnabled, enabled =>
        {
            if (enabled) BrowserBridge.Register(folder);
            else BrowserBridge.Unregister();
            history.Set("sites", enabled ? "1" : "0");
            tracker.LoadSettings();
            RenderReport();
            return true;
        }), "Chrome and Edge only. Private browsing is excluded."));
        if (tracker.SitesEnabled)
        {
            panel.Children.Add(Setting("Count foreground video", Check("Count foreground video", tracker.VideoEnabled, enabled =>
            {
                history.Set("video", enabled ? "1" : "0");
                tracker.LoadSettings();
                return true;
            }), "Use the AfterHours extension to allow video detection on each site."));
            connection = Label(ConnectionText(), 13, secondary: true);
            var setup = new StackPanel();
            setup.Children.Add(Label("Browser companion", 14, bold: true));
            setup.Children.Add(Label("In Chrome or Edge, turn on Developer mode under Extensions. Choose Load unpacked and select the companion folder.",
                13, secondary: true, top: 4));
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
            var open = ActionButton("Open folder", () => OpenFolder(BrowserBridge.ExtensionFolder));
            open.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(open);
            actions.Children.Add(ActionButton("Copy folder path", () => Clipboard.SetText(BrowserBridge.ExtensionFolder)));
            setup.Children.Add(actions);
            setup.Children.Add(connection);
            panel.Children.Add(Card(setup));
        }

        AddHeading(panel, "Exclusions");
        var exclusions = new StackPanel();
        exclusions.Children.Add(Label("Separate entries with a comma or a new line. Excluded apps and sites still count toward total active time.",
            13, secondary: true));
        var apps = EditList(exclusions, "App names", history.Get("apps_excluded", ""));
        var sites = EditList(exclusions, "Website domains", history.Get("sites_excluded", ""));
        var save = ActionButton("Save", () =>
        {
            history.Set("apps_excluded", apps.Text);
            history.Set("sites_excluded", sites.Text);
            tracker.LoadSettings();
            tracker.Reset();
            MessageBox.Show(this, "Exclusions saved.", Title);
        });
        AutomationProperties.SetName(save, "Save exclusions");
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Margin = new Thickness(0, 10, 0, 0);
        exclusions.Children.Add(save);
        panel.Children.Add(Card(exclusions));

        AddHeading(panel, "Local history");
        panel.Children.Add(Setting("History folder", ActionButton("Open folder", () => OpenFolder(folder))));
        panel.Children.Add(Setting("Export activity", ActionButton("Export CSV", Export)));
        var date = DateChoice("Date to delete", DateTime.Today);
        date.Margin = new Thickness(0, 0, 8, 0);
        var deleteDay = ActionButton("Delete day", () =>
        {
            if (date.SelectedDate is { } value) Delete(Tracker.Day(value));
        });
        deleteDay.IsEnabled = date.SelectedDate.HasValue;
        date.SelectedDateChanged += (_, _) => deleteDay.IsEnabled = date.SelectedDate.HasValue;
        var deleteControls = new StackPanel { Orientation = Orientation.Horizontal };
        deleteControls.Children.Add(date);
        deleteControls.Children.Add(deleteDay);
        panel.Children.Add(Setting("Delete activity by date", deleteControls));
        panel.Children.Add(Setting("Clear all activity", ActionButton("Delete all history", () => Delete(null))));
    }

    private ToggleButton Check(string name, bool value, Func<bool, bool> change)
    {
        var check = new ToggleButton { IsChecked = value, Style = ToggleStyle };
        AutomationProperties.SetName(check, name);
        check.Click += (_, _) =>
        {
            bool enabled = check.IsChecked == true;
            try
            {
                if (!change(enabled)) check.IsChecked = !enabled;
            }
            catch (Exception e)
            {
                check.IsChecked = !enabled;
                ShowError(e);
            }
        };
        return check;
    }

    private static TextBox EditList(Panel panel, string title, string value)
    {
        var label = Label(title, 13, bold: true, top: 12);
        panel.Children.Add(label);
        var input = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 56, Margin = new Thickness(0, 5, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetLabeledBy(input, label);
        panel.Children.Add(input);
        return input;
    }

    private static DatePicker DateChoice(string name, DateTime date)
    {
        var control = new DatePicker { SelectedDate = date, DisplayDateEnd = DateTime.Today, Width = 144 };
        AutomationProperties.SetName(control, name);
        return control;
    }

    private static ComboBox Choice(string name, IEnumerable<string> options, int selected, double width)
    {
        var control = new ComboBox { ItemsSource = options, SelectedIndex = selected, Width = width, MinHeight = 32 };
        AutomationProperties.SetName(control, name);
        return control;
    }

    private void OpenFolder(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "Export AfterHours history", Filter = "CSV file (*.csv)|*.csv",
            FileName = $"AfterHours-{Tracker.Day(DateTime.Today)}.csv", AddExtension = true };
        if (dialog.ShowDialog(this) == true)
        {
            if (Path.GetFullPath(dialog.FileName).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a folder outside the AfterHours data folder for your export.");
            history.Export(dialog.FileName);
        }
    }

    private void Delete(string? date)
    {
        string prompt = date == null ? "Delete all recorded activity and window closes?" :
            $"Delete recorded activity and window closes for {date}?";
        if (MessageBox.Show(this, prompt, "Delete local history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        history.Delete(date);
        tracker.Reset();
        MessageBox.Show(this, date == null ? "Local history was cleared." : $"History for {date} was cleared.", Title);
    }

    private void Perform(Action action)
    {
        try { action(); }
        catch (Exception e) { ShowError(e); }
    }

    private void ShowError(Exception e) =>
        MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private Button ActionButton(string title, Action action)
    {
        var button = new Button { Content = title, MinHeight = 32 };
        button.Click += (_, _) => Perform(action);
        return button;
    }

    private static Border Setting(string name, FrameworkElement control, string? description = null)
    {
        var label = new StackPanel();
        label.Children.Add(Label(name));
        if (description != null) label.Children.Add(Label(description, 12, secondary: true, top: 3));
        return Card(Split(label, control), new Thickness(16, 10, 16, 10));
    }

    private static Grid Split(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.VerticalAlignment = VerticalAlignment.Center;
        right.VerticalAlignment = VerticalAlignment.Center;
        right.Margin = new Thickness(right.Margin.Left + 12, right.Margin.Top, right.Margin.Right, right.Margin.Bottom);
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static Border Card(UIElement child, Thickness? padding = null)
    {
        var card = new Border { Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = padding ?? new Thickness(16), Margin = new Thickness(0, 0, 0, 8) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    private static Border Metric(string name, string value, string explanation)
    {
        var metric = new StackPanel();
        metric.Children.Add(Label(name, 13, secondary: true));
        metric.Children.Add(Label(value, 27, bold: true, top: 2));
        var card = Card(metric, new Thickness(16, 12, 16, 12));
        card.Margin = new Thickness(0, 0, 8, 8);
        card.ToolTip = explanation;
        AutomationProperties.SetHelpText(card, explanation);
        return card;
    }

    private static void AddHeading(Panel panel, string title) =>
        panel.Children.Add(Label(title, 17, bold: true, top: panel.Children.Count == 0 ? 0 : 10, bottom: 8));

    private static void Ranking(Panel panel, string title, IEnumerable<Tally> items, Func<Tally, string> value, string empty, string? explanation = null)
    {
        var heading = Label(title, 17, bold: true, top: panel.Children.Count == 0 ? 0 : 10, bottom: 8);
        if (explanation != null)
        {
            heading.ToolTip = explanation;
            AutomationProperties.SetHelpText(heading, explanation);
        }
        panel.Children.Add(heading);
        panel.Children.Add(RankingRows(items, value, empty));
    }

    private static Border RankingRows(IEnumerable<Tally> items, Func<Tally, string> value, string empty)
    {
        var list = new StackPanel();
        foreach (var item in items)
            list.Children.Add(ValueRow(item.Name, value(item), list.Children.Count > 0));
        return list.Children.Count == 0 ? Card(Label(empty, secondary: true)) : Card(list, new Thickness(0));
    }

    private static Border ValueRow(string name, string value, bool separated = false)
    {
        var row = new Border { Padding = new Thickness(16, 10, 16, 10),
            Child = Split(Label(name), Label(value, 13, secondary: true)),
            BorderThickness = new Thickness(0, separated ? 1 : 0, 0, 0) };
        row.SetResourceReference(Border.BorderBrushProperty, Divider);
        return row;
    }

    private static TextBlock NavigationIcon(string glyph)
    {
        var icon = Label(glyph, 16);
        icon.FontFamily = IconFont;
        icon.Width = 28;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    private static TextBlock Label(string text, double size = 14, bool secondary = false, bool bold = false, double top = 0, double bottom = 0)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, top, 0, bottom) };
        label.SetResourceReference(TextBlock.ForegroundProperty, secondary ? SecondaryText : PrimaryText);
        return label;
    }

    private static List<Tally> Rank(List<Activity> rows, Func<Activity, string> key) =>
        rows.Where(a => key(a).Length > 0).GroupBy(key)
            .Select(g => new Tally(g.Key, g.Sum(Length), g.Sum(a => a.Visits)))
            .OrderByDescending(x => x.Milliseconds).ThenBy(x => x.Name).ToList();

    private static long Length(Activity a) => a.End - a.Start;
    private static string Duration(long ms)
    {
        if (ms > 0 && ms < 60_000) return "<1 min";
        var minutes = ms / 60_000;
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60}h {minutes % 60:00}m";
    }

    private static string Quantity(int count, string word) => $"{count:N0} {word}{(count == 1 ? "" : "s")}";

    private abstract class Chart : Control
    {
        static Chart()
        {
            ForegroundProperty.OverrideMetadata(typeof(Chart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
            BackgroundProperty.OverrideMetadata(typeof(Chart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
            BorderBrushProperty.OverrideMetadata(typeof(Chart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        }

        protected Chart()
        {
            SetResourceReference(ForegroundProperty, SecondaryText);
            SetResourceReference(BackgroundProperty, "ControlAltFillColorTertiaryBrush");
            SetResourceReference(BorderBrushProperty, Divider);
        }

        protected Brush Accent => TryFindResource(AccentBrush) as Brush ?? SystemColors.HighlightBrush;

        protected FormattedText Caption(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Foreground ?? SystemColors.WindowTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);

        private sealed class ChartPeer(Chart owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
        {
            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
                System.Windows.Automation.Peers.AutomationControlType.Image;
        }
    }

    private sealed class DayChart : Chart
    {
        private readonly DateTime date;
        private readonly List<(long Start, long End)> sessions;

        public DayChart(DateTime date, List<Activity> rows)
        {
            this.date = date;
            sessions = new();
            foreach (var a in rows.OrderBy(x => x.Start))
            {
                if (sessions.Count > 0 && sessions[^1].End >= a.Start)
                    sessions[^1] = (sessions[^1].Start, Math.Max(a.End, sessions[^1].End));
                else sessions.Add((a.Start, a.End));
            }
            Height = 50;
            Margin = new Thickness(0, 10, 0, 0);
            string range = sessions.Count == 0 ? "No activity" : string.Join(", ", sessions.Take(20).Select(s =>
                $"{DateTimeOffset.FromUnixTimeMilliseconds(s.Start).LocalDateTime:t} to {DateTimeOffset.FromUnixTimeMilliseconds(s.End).LocalDateTime:t}"));
            AutomationProperties.SetName(this, $"Active time on {date:D}. {range}.");
            ToolTip = "Colored segments show active time.";
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 5) return;
            long first = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            long last = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            dc.DrawRoundedRectangle(Background, null, new Rect(0, 4, ActualWidth, 16), 3, 3);
            foreach (var (start, end) in sessions)
            {
                double x = ActualWidth * Math.Clamp((double)(start - first) / (last - first), 0, 1);
                double right = ActualWidth * Math.Clamp((double)(end - first) / (last - first), 0, 1);
                dc.DrawRoundedRectangle(Accent, null, new Rect(x, 4, Math.Min(ActualWidth - x, Math.Max(2, right - x)), 16), 2, 2);
            }
            for (int i = 0; i <= 4; i++)
            {
                var time = DateTimeOffset.FromUnixTimeMilliseconds(first + i * (last - first) / 4).LocalDateTime;
                var text = Caption(i == 4 ? "Midnight" : time.ToString("t"));
                double x = i * ActualWidth / 4;
                dc.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width)), 26));
            }
        }
    }

    private sealed class TrendChart : Chart
    {
        private readonly List<DateTime> dates;
        private readonly Dictionary<string, long> tallies;
        private readonly HashSet<string> observed;

        public TrendChart(List<DateTime> dates, Dictionary<string, long> tallies, HashSet<string> observed)
        {
            this.dates = dates;
            this.tallies = tallies;
            this.observed = observed;
            Height = 108;
            Margin = new Thickness(0, 7, 0, 0);
            AutomationProperties.SetName(this, "Daily activity. " + string.Join(". ", dates.Select(d =>
                $"{d:M/d}: {(observed.Contains(Tracker.Day(d)) ? Duration(tallies.GetValueOrDefault(Tracker.Day(d))) : "no data")}")));
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 5) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            long max = Math.Max(3_600_000, tallies.Values.DefaultIfEmpty().Max());
            double column = ActualWidth / dates.Count;
            double bar = Math.Min(43, column * 0.63);
            dc.DrawLine(new Pen(BorderBrush, 1), new Point(0, 80), new Point(ActualWidth, 80));
            for (int i = 0; i < dates.Count; i++)
            {
                string day = Tracker.Day(dates[i]);
                long ms = tallies.GetValueOrDefault(day);
                double x = (i + 0.5) * column;
                if (observed.Contains(day))
                {
                    double height = Math.Max(2, ms * 57.0 / max);
                    dc.PushOpacity(i == dates.Count - 1 ? 1 : 0.68);
                    dc.DrawRoundedRectangle(Accent, null,
                        new Rect(x - bar / 2, 80 - height, bar, height), 2, 2);
                    dc.Pop();
                    if (dates.Count <= 7)
                    {
                        var amount = Caption(Duration(ms));
                        dc.DrawText(amount, new Point(Math.Clamp(x - amount.Width / 2, 0, Math.Max(0, ActualWidth - amount.Width)), 77 - height - amount.Height));
                    }
                }
                else dc.DrawLine(new Pen(Foreground, 1), new Point(x - 3, 76), new Point(x + 3, 76));

                if (dates.Count <= 7 || i % 5 == 0 || i == dates.Count - 1)
                {
                    var label = Caption(dates.Count <= 7 ? dates[i].ToString("ddd") : dates[i].ToString("M/d"));
                    dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(0, ActualWidth - label.Width)), 87));
                }
            }
        }
    }
}
