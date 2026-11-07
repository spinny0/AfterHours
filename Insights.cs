using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AfterHours;

internal enum TargetKind { App, Website, Category }
internal sealed record Category(string Id, string Name, string[] Apps, string[] Sites);
internal sealed record TimeLimit(string Id, TargetKind Kind, string Target, int Minutes);
internal sealed record Rules(Category[] Categories, TimeLimit[] Limits);
internal sealed record WeeklyComparison(DateTime Start, DateTime End, int MatchedDays,
    long Current, long Previous, List<Tally> Apps, List<Tally> Sites);

internal sealed class Insights
{
    private readonly History history;
    private Rules rules;
    public IReadOnlyList<Category> Categories => rules.Categories;
    public IReadOnlyList<TimeLimit> Limits => rules.Limits;

    public Insights(History history)
    {
        this.history = history;
        rules = JsonSerializer.Deserialize<Rules>(history.Get("insight_rules", "null")) ?? new Rules([], []);
    }

    public void SaveCategory(string? id, string name, string apps, string sites)
    {
        name = Required(name, 60, "Enter a category name of up to 60 characters.");
        if (name.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase) ||
            Categories.Any(c => c.Id != id && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a unique category name.");

        var appNames = Entries(apps);
        var domains = Entries(sites).Select(Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var other in Categories.Where(c => c.Id != id))
        {
            if (other.Apps.Intersect(appNames, StringComparer.OrdinalIgnoreCase).Any() ||
                other.Sites.Intersect(domains, StringComparer.OrdinalIgnoreCase).Any())
                throw new ArgumentException($"An app or domain is already assigned to {other.Name}.");
        }
        var edited = new Category(id ?? Guid.NewGuid().ToString("N"), name, appNames, domains);
        Save(rules with { Categories = Categories.Where(c => c.Id != id).Append(edited).OrderBy(c => c.Name).ToArray() });
    }

    public void RemoveCategory(string id) => Save(new Rules(
        Categories.Where(c => c.Id != id).ToArray(),
        Limits.Where(l => l.Kind != TargetKind.Category || l.Target != id).ToArray()));

    public void SaveLimit(string? id, TargetKind kind, string target, int minutes)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentException("Enter a daily limit from 1 to 1,440 minutes.");
        target = kind switch
        {
            TargetKind.Website => Domain(target),
            TargetKind.Category when Categories.Any(c => c.Id == target) => target,
            TargetKind.Category => throw new ArgumentException("Choose a category."),
            _ => Required(target, 150, "Enter an app name as it appears in Apps.")
        };
        if (Limits.Any(l => l.Id != id && l.Kind == kind && l.Target.Equals(target, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("That target already has a limit.");
        var edited = new TimeLimit(id ?? Guid.NewGuid().ToString("N"), kind, target, minutes);
        Save(rules with { Limits = Limits.Where(l => l.Id != id).Append(edited).ToArray() });
    }

    public void RemoveLimit(string id) => Save(rules with { Limits = Limits.Where(l => l.Id != id).ToArray() });

    public string LimitName(TimeLimit limit) => limit.Kind == TargetKind.Category
        ? Categories.FirstOrDefault(c => c.Id == limit.Target)?.Name ?? "Category"
        : limit.Target;

    public Category? CategoryOf(Activity activity)
    {
        Category? best = null;
        int specificity = 0;
        if (activity.Site.Length > 0)
        {
            foreach (var category in Categories)
                foreach (var domain in category.Sites)
                    if (domain.Length > specificity && MatchesDomain(activity.Site, domain))
                    {
                        best = category;
                        specificity = domain.Length;
                    }
        }
        return best ?? Categories.FirstOrDefault(c => c.Apps.Contains(activity.App, StringComparer.OrdinalIgnoreCase));
    }

    public List<Tally> CategoryTotals(IEnumerable<Activity> rows) => rows
        .GroupBy(a => CategoryOf(a)?.Name ?? "Uncategorized")
        .Select(group => new Tally(group.Key, group.Sum(a => a.End - a.Start), 0))
        .OrderByDescending(a => a.Milliseconds).ThenBy(a => a.Name).ToList();

    public long Used(TimeLimit limit, IEnumerable<Activity> rows) => rows.Where(a => limit.Kind switch
    {
        TargetKind.App => a.App.Equals(limit.Target, StringComparison.OrdinalIgnoreCase),
        TargetKind.Website => MatchesDomain(a.Site, limit.Target),
        TargetKind.Category => CategoryOf(a)?.Id == limit.Target,
        _ => false
    }).Sum(a => a.End - a.Start);

    public List<TimeLimit> CheckReminders(DateTime date)
    {
        if (Limits.Count == 0) return [];
        string day = Tracker.Day(date);
        var rows = history.Read(day, day);
        return Limits.Where(limit => Used(limit, rows) >= limit.Minutes * 60_000L &&
            history.MarkLimitReached(day, limit.Id)).ToList();
    }

    public WeeklyComparison CompareWeeks(DateTime today)
    {
        DateTime start = today.Date.AddDays(-7), end = today.Date.AddDays(-1);
        string first = Tracker.Day(start.AddDays(-7)), last = Tracker.Day(end);
        var observed = history.Observed(first, last).ToHashSet();
        var currentDays = new HashSet<string>();
        var previousDays = new HashSet<string>();
        for (int i = 0; i < 7; i++)
        {
            DateTime date = start.AddDays(i);
            string current = Tracker.Day(date), previous = Tracker.Day(date.AddDays(-7));
            if (observed.Contains(current) && observed.Contains(previous))
            {
                currentDays.Add(current);
                previousDays.Add(previous);
            }
        }
        var all = history.Read(first, last);
        var currentRows = all.Where(a => currentDays.Contains(a.Day)).ToList();
        var previousRows = all.Where(a => previousDays.Contains(a.Day)).ToList();
        return new WeeklyComparison(start, end, currentDays.Count,
            currentRows.Sum(a => a.End - a.Start), previousRows.Sum(a => a.End - a.Start),
            Changes(currentRows, previousRows, a => a.App), Changes(currentRows, previousRows, a => a.Site));
    }

    private static List<Tally> Changes(List<Activity> current, List<Activity> previous, Func<Activity, string> name) =>
        current.Select(a => (Name: name(a), Time: a.End - a.Start))
            .Concat(previous.Select(a => (Name: name(a), Time: a.Start - a.End)))
            .Where(a => a.Name.Length > 0).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Tally(g.Key, g.Sum(a => a.Time), 0)).Where(a => a.Milliseconds != 0)
            .OrderByDescending(a => Math.Abs(a.Milliseconds)).ThenBy(a => a.Name).Take(5).ToList();

    internal static bool MatchesDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static string[] Entries(string value) => value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Domain(string value)
    {
        string domain = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.StartsWith("www.", StringComparison.Ordinal)) domain = domain[4..];
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns)
            throw new ArgumentException("Enter a domain such as example.com.");
        return new System.Globalization.IdnMapping().GetAscii(domain);
    }

    private static string Required(string text, int max, string message)
    {
        string value = text.Trim();
        return value.Length is > 0 && value.Length <= max ? value : throw new ArgumentException(message);
    }

    private void Save(Rules next)
    {
        history.Set("insight_rules", JsonSerializer.Serialize(next));
        rules = next;
    }
}
