using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace AfterHours;

internal sealed partial class Dashboard
{
    private static bool UsesPeriod(string page) => page is "Overview" or "Apps" or "Websites";

    private void OpenPage(string title) =>
        navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().First(item => (string)item.Tag == title);

    public void RevealLimits()
    {
        OpenPage("Time limits");
        RenderReport();
        Reveal();
    }

    private sealed record TimelineRow(string Time, string App, string Website, string Category, string Duration);

    private void DayDetails(Panel panel)
    {
        var date = DateChoice("Activity date", selectedDay ?? DateTime.Today);
        var header = Split(Label("Timeline", 17, bold: true), date);
        header.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(header);
        var dayContent = new StackPanel();
        panel.Children.Add(dayContent);

        void LoadDay()
        {
            DateTime value = date.SelectedDate ?? DateTime.Today;
            string key = Tracker.Day(value);
            var rows = history.Read(key, key).OrderBy(a => a.Start).ToList();
            dayContent.Children.Clear();
            var summary = new StackPanel();
            summary.Children.Add(Label($"{Duration(rows.Sum(Length))} active · {Quantity(rows.Select(a => a.Session).Distinct().Count(), "session")}",
                14, bold: true));
            summary.Children.Add(new DayChart(value, rows));
            dayContent.Children.Add(Card(summary));
            if (rows.Count == 0)
            {
                dayContent.Children.Add(Card(Label("No activity recorded for this day.", secondary: true)));
                return;
            }

            var spans = new List<Activity>();
            foreach (var row in rows)
            {
                if (spans.Count > 0 && spans[^1] is var last && last.End == row.Start &&
                    last.App == row.App && last.Site == row.Site && last.Session == row.Session)
                    spans[^1] = last with { End = row.End };
                else spans.Add(row);
            }

            var entries = new List<TimelineRow>();
            long? previousEnd = null;
            foreach (var span in spans)
            {
                if (previousEnd is long end && span.Start - end >= 1000)
                    entries.Add(new TimelineRow(TimeRange(end, span.Start), "Break or no data", "", "", Duration(span.Start - end)));

                string app = span.App.Length > 0 ? span.App : "Unlabeled activity";
                string category = insights.CategoryOf(span)?.Name ?? "Uncategorized";
                entries.Add(new TimelineRow(TimeRange(span.Start, span.End), app, span.Site, category, Duration(Length(span))));
                previousEnd = span.End;
            }

            var table = new DataGrid { ItemsSource = entries, AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, CanUserReorderColumns = false, CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                MaxHeight = 400, EnableRowVirtualization = true };
            AutomationProperties.SetName(table, "Chronological activity");
            table.Columns.Add(TimelineColumn("Time", nameof(TimelineRow.Time), 1.25));
            table.Columns.Add(TimelineColumn("App", nameof(TimelineRow.App), 1.15));
            table.Columns.Add(TimelineColumn("Website", nameof(TimelineRow.Website), 1.15));
            table.Columns.Add(TimelineColumn("Category", nameof(TimelineRow.Category), 1));
            table.Columns.Add(TimelineColumn("Duration", nameof(TimelineRow.Duration), 0.65));
            dayContent.Children.Add(Card(table, new Thickness(0)));
            Ranking(dayContent, "Time by category", insights.CategoryTotals(rows), a => Duration(a.Milliseconds), "No activity recorded.");
        }

        date.SelectedDateChanged += (_, _) =>
        {
            selectedDay = date.SelectedDate;
            LoadDay();
        };
        LoadDay();
    }

    private static string TimeRange(long start, long end) =>
        $"{DateTimeOffset.FromUnixTimeMilliseconds(start).LocalDateTime:t}–{DateTimeOffset.FromUnixTimeMilliseconds(end).LocalDateTime:t}";

    private static DataGridTextColumn TimelineColumn(string title, string property, double width) =>
        new() { Header = title, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            MinWidth = 75 };

    private void WeeklyRecap(Panel panel)
    {
        var recap = insights.CompareWeeks(DateTime.Today);
        var summary = new StackPanel();
        summary.Children.Add(Label($"{recap.Start:MMM d}–{recap.End:MMM d} vs. {recap.Start.AddDays(-7):MMM d}–{recap.End.AddDays(-7):MMM d}",
            16, bold: true));
        summary.Children.Add(Label(recap.MatchedDays == 0
            ? "The recap needs activity records for the same weekday in both weeks."
            : $"Comparing {recap.MatchedDays} of 7 days with tracking data in both weeks.", 13, secondary: true, top: 4));
        if (recap.MatchedDays > 0)
        {
            summary.Children.Add(ValueRow("This period", Duration(recap.Current)));
            summary.Children.Add(ValueRow("Previous period", Duration(recap.Previous), separated: true));
            summary.Children.Add(ValueRow("Change", ChangeText(recap.Current - recap.Previous), separated: true));
        }
        panel.Children.Add(Card(summary));
        if (recap.MatchedDays == 0) return;
        Ranking(panel, "Biggest app changes", recap.Apps, a => ChangeText(a.Milliseconds), "No changes in app time.");
        Ranking(panel, "Biggest website changes", recap.Sites, a => ChangeText(a.Milliseconds), "No changes in website time.");
    }

    private static string ChangeText(long difference) => difference == 0 ? "No change" :
        $"{Duration(Math.Abs(difference))} {(difference > 0 ? "more" : "less")}";

    private void CategoryPage(Panel panel)
    {
        DateTime today = DateTime.Today;
        var recent = history.Read(Tracker.Day(today.AddDays(-6)), Tracker.Day(today));
        Ranking(panel, "Last 7 days", insights.CategoryTotals(recent), a => Duration(a.Milliseconds), "No activity recorded.");
        AddHeading(panel, "Manage categories");
        var categories = insights.Categories;
        var pick = Choice("Category to edit", categories.Select(c => c.Name).Prepend("New category"), 0, 180);
        var editor = new StackPanel();
        editor.Children.Add(pick);
        var fields = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        editor.Children.Add(fields);
        panel.Children.Add(Card(editor));

        void LoadEditor()
        {
            var category = pick.SelectedIndex > 0 ? categories[pick.SelectedIndex - 1] : null;
            fields.Children.Clear();
            var name = TextField(fields, "Category name", category?.Name ?? "", 60);
            fields.Children.Add(Label("Use names from Apps and domains such as example.com. Separate entries with a comma or a new line.",
                13, secondary: true, top: 10));
            var apps = EditList(fields, "App names", string.Join(Environment.NewLine, category?.Apps ?? []));
            var sites = EditList(fields, "Website domains", string.Join(Environment.NewLine, category?.Sites ?? []));
            var save = ActionButton("Save category", () =>
            {
                insights.SaveCategory(category?.Id, name.Text, apps.Text, sites.Text);
                RenderReport();
            });
            Button? remove = category == null ? null : ActionButton("Remove category", () =>
            {
                if (MessageBox.Show(this, $"Remove {category.Name} and any limits for it?", Title, MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                insights.RemoveCategory(category.Id);
                RenderReport();
            });
            fields.Children.Add(EditorActions(save, remove));
        }

        pick.SelectionChanged += (_, _) => LoadEditor();
        LoadEditor();
    }

    private void LimitsPage(Panel panel)
    {
        var limits = insights.Limits;
        AddHeading(panel, "Today");
        if (limits.Count == 0) panel.Children.Add(Card(Label("No time limits set.", secondary: true)));
        else
        {
            var rows = new List<(TimeLimit Limit, TextBlock Status, ProgressBar Progress)>();
            foreach (var limit in limits)
            {
                var details = new StackPanel();
                details.Children.Add(Label(insights.LimitName(limit), 15, bold: true));
                var status = Label("", 12, secondary: true, top: 3);
                details.Children.Add(status);
                var progress = new ProgressBar
                {
                    Height = 4, Maximum = limit.Minutes * 60_000L, Margin = new Thickness(0, 9, 0, 0),
                    MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(progress, $"Today’s usage for {insights.LimitName(limit)}");
                details.Children.Add(progress);
                panel.Children.Add(Card(details, new Thickness(16, 12, 16, 12)));
                rows.Add((limit, status, progress));
            }
            refreshLimits = () =>
            {
                DateTime today = DateTime.Today;
                string day = Tracker.Day(today);
                var activity = history.Read(day, day);
                foreach (var row in rows)
                {
                    long used = insights.Used(row.Limit, activity);
                    string state = used >= row.Limit.Minutes * 60_000L ? "Limit reached" : "Reminder at limit";
                    row.Status.Text = $"{row.Limit.Kind} · {Duration(used)} / {Duration(row.Limit.Minutes * 60_000L)} · {state}";
                    row.Progress.Value = Math.Min(row.Progress.Maximum, used);
                }
            };
            refreshLimits();
        }

        AddHeading(panel, "Manage limits");
        var editor = new StackPanel();
        var pick = Choice("Time limit to edit", limits.Select(l => $"{insights.LimitName(l)} ({l.Kind})").Prepend("New limit"), 0, 225);
        editor.Children.Add(pick);
        var fields = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        editor.Children.Add(fields);
        panel.Children.Add(Card(editor));

        void LoadEditor()
        {
            var limit = pick.SelectedIndex > 0 ? limits[pick.SelectedIndex - 1] : null;
            fields.Children.Clear();
            var kind = Choice("Limit type", ["App", "Website", "Category"], (int)(limit?.Kind ?? TargetKind.App), 130);
            fields.Children.Add(Setting("Limit type", kind));
            var targetHost = new StackPanel();
            fields.Children.Add(targetHost);
            TextBox? target = null;
            ComboBox? category = null;
            var categories = insights.Categories;
            void UpdateTarget()
            {
                var selected = (TargetKind)kind.SelectedIndex;
                targetHost.Children.Clear();
                if (selected == TargetKind.Category)
                {
                    int index = limit?.Kind == selected ? categories.ToList().FindIndex(c => c.Id == limit.Target) : -1;
                    category = Choice("Category", categories.Select(c => c.Name), index, 180);
                    targetHost.Children.Add(Setting("Category", category));
                    if (categories.Count == 0)
                        targetHost.Children.Add(Label("Create a category before setting a category limit.", 13, secondary: true));
                }
                else
                {
                    target = TextField(targetHost, selected == TargetKind.App ? "App name (as shown in Apps)" : "Website domain",
                        limit?.Kind == selected ? limit.Target : "", 253);
                }
            }
            kind.SelectionChanged += (_, _) => UpdateTarget();
            UpdateTarget();

            var minutes = TextField(fields, "Minutes per day", limit?.Minutes.ToString() ?? "", 4);
            var save = ActionButton("Save limit", () =>
            {
                if (!int.TryParse(minutes.Text.Trim(), out int budget)) throw new ArgumentException("Enter the daily limit in minutes.");
                var selected = (TargetKind)kind.SelectedIndex;
                string value = selected == TargetKind.Category
                    ? category?.SelectedIndex is int index && index >= 0 ? categories[index].Id : ""
                    : target?.Text ?? "";
                insights.SaveLimit(limit?.Id, selected, value, budget);
                RenderReport();
            });
            var remove = limit == null ? null : ActionButton("Remove limit", () =>
            {
                insights.RemoveLimit(limit.Id);
                RenderReport();
            });
            fields.Children.Add(EditorActions(save, remove));
        }

        pick.SelectionChanged += (_, _) => LoadEditor();
        LoadEditor();

    }

    private static TextBox TextField(Panel panel, string title, string value, int maxLength)
    {
        var label = Label(title, 13, bold: true, top: 8, bottom: 4);
        panel.Children.Add(label);
        var text = new TextBox { Text = value, MaxLength = maxLength, MinHeight = 32 };
        AutomationProperties.SetLabeledBy(text, label);
        panel.Children.Add(text);
        return text;
    }

    private static StackPanel EditorActions(Button save, Button? remove)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        if (remove != null)
        {
            remove.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(remove);
        }
        actions.Children.Add(save);
        return actions;
    }
}
