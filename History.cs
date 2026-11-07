using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace AfterHours;

internal sealed record Activity(string Day, long Start, long End, string Session, string App, string Site, int Visits);
internal sealed record Tally(string Name, long Milliseconds, int Count);

internal sealed class History : IDisposable
{
    private readonly IntPtr db;
    private Activity? pending;
    private string? observedDay;

    public History(string path)
    {
        Check(Open(path, out db, 6, IntPtr.Zero));
        Execute("PRAGMA secure_delete=ON");
        Execute("CREATE TABLE IF NOT EXISTS activity (day TEXT, start INTEGER, end INTEGER, session TEXT, app TEXT, site TEXT, visits INTEGER)");
        Execute("CREATE INDEX IF NOT EXISTS activity_day ON activity(day)");
        Execute("CREATE TABLE IF NOT EXISTS coverage (day TEXT PRIMARY KEY)");
        Execute("CREATE TABLE IF NOT EXISTS closes (day TEXT, app TEXT, count INTEGER, PRIMARY KEY(day, app))");
        Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT)");
        Execute("CREATE TABLE IF NOT EXISTS limit_alerts (day TEXT, id TEXT, PRIMARY KEY(day,id))");
    }

    public string Get(string key, string fallback) =>
        Query("SELECT value FROM settings WHERE key=?", s => String(s, 0), key).Find(_ => true) ?? fallback;

    public void Set(string key, string value) =>
        Execute("INSERT OR REPLACE INTO settings VALUES (?,?)", key, value);

    public bool MarkLimitReached(string day, string id)
    {
        Execute("INSERT OR IGNORE INTO limit_alerts VALUES (?,?)", day, id);
        return Query("SELECT changes()", statement => Long(statement, 0))[0] == 1;
    }

    public void RemoveSetting(string key) => Execute("DELETE FROM settings WHERE key=?", key);

    public void Observe(string day)
    {
        if (observedDay == day) return;
        Execute("INSERT OR IGNORE INTO coverage VALUES (?)", day);
        observedDay = day;
    }

    public void Record(Activity next)
    {
        if (pending is { } last && last.Day == next.Day && last.App == next.App && last.Site == next.Site &&
            last.Session == next.Session && next.Visits == 0 && last.End == next.Start)
            pending = last with { End = next.End };
        else
        {
            Flush();
            pending = next;
        }
    }

    public void Flush()
    {
        if (pending is not { } a) return;
        Execute("INSERT INTO activity VALUES (?,?,?,?,?,?,?)", a.Day, a.Start, a.End, a.Session, a.App, a.Site, (long)a.Visits);
        pending = null;
    }

    public void Closed(string day, string app) =>
        Execute("INSERT INTO closes VALUES (?,?,1) ON CONFLICT(day, app) DO UPDATE SET count=count+1", day, app);

    public List<Activity> Read(string first, string last)
    {
        var items = Query("SELECT day,start,end,session,app,site,visits FROM activity WHERE day BETWEEN ? AND ? ORDER BY start",
            s => new Activity(String(s, 0), Long(s, 1), Long(s, 2), String(s, 3), String(s, 4), String(s, 5), (int)Long(s, 6)), first, last);
        if (pending is { } a && string.CompareOrdinal(a.Day, first) >= 0 && string.CompareOrdinal(a.Day, last) <= 0)
            items.Add(a);
        return items;
    }

    public List<string> Observed(string first, string last) =>
        Query("SELECT day FROM coverage WHERE day BETWEEN ? AND ?", s => String(s, 0), first, last);

    public List<Tally> Closes(string first, string last) =>
        Query("SELECT app,sum(count) FROM closes WHERE day BETWEEN ? AND ? GROUP BY app ORDER BY sum(count) DESC,app",
            s => new Tally(String(s, 0), 0, (int)Long(s, 1)), first, last);

    public void Delete(string? day)
    {
        Flush();
        Execute("BEGIN");
        try
        {
            foreach (var table in new[] { "activity", "coverage", "closes", "limit_alerts" })
            {
                if (day == null) Execute($"DELETE FROM {table}");
                else Execute($"DELETE FROM {table} WHERE day=?", day);
            }
            Execute("COMMIT");
        }
        catch
        {
            Execute("ROLLBACK");
            throw;
        }
        observedDay = null;
    }

    public void Export(string path)
    {
        Flush();
        using var file = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        file.WriteLine("type,date,start_utc,end_utc,app,site,visits,window_closes");
        foreach (var a in Read("0000", "9999"))
        {
            string start = DateTimeOffset.FromUnixTimeMilliseconds(a.Start).ToString("O", CultureInfo.InvariantCulture);
            string end = DateTimeOffset.FromUnixTimeMilliseconds(a.End).ToString("O", CultureInfo.InvariantCulture);
            file.WriteLine($"activity,{a.Day},{start},{end},{Csv(a.App)},{Csv(a.Site)},{a.Visits},");
        }
        foreach (var row in Query("SELECT day,app,count FROM closes ORDER BY day,app",
            s => $"close,{String(s, 0)},,,{Csv(String(s, 1))},,,{Long(s, 2)}"))
            file.WriteLine(row);
        foreach (var day in Observed("0000", "9999"))
            file.WriteLine($"coverage,{day},,,,,,");
    }

    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void Execute(string sql, params object[] values) =>
        Query(sql, _ => 0, values);

    private List<T> Query<T>(string sql, Func<IntPtr, T> read, params object[] values)
    {
        Check(Prepare(db, sql, -1, out var stmt, IntPtr.Zero));
        try
        {
            for (int i = 0; i < values.Length; i++)
                Check(values[i] is long n ? BindLong(stmt, i + 1, n) :
                    BindText(stmt, i + 1, (string)values[i], -1, new IntPtr(-1)));
            var result = new List<T>();
            int code;
            while ((code = Step(stmt)) == 100) result.Add(read(stmt));
            if (code != 101) Check(code);
            return result;
        }
        finally { FinalizeStatement(stmt); }
    }

    private void Check(int code)
    {
        if (code != 0)
            throw new IOException("The local activity database could not be updated. " +
                (db == IntPtr.Zero ? code.ToString() : Marshal.PtrToStringUTF8(Error(db))));
    }

    public void Dispose()
    {
        try { Flush(); }
        finally { Close(db); }
    }

    private static string String(IntPtr s, int index) => Marshal.PtrToStringUTF8(ColumnText(s, index)) ?? "";
    private const string Sqlite = "winsqlite3.dll";
    [DllImport(Sqlite, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Sqlite, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Prepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
    [DllImport(Sqlite, EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BindText(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr free);
    [DllImport(Sqlite, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BindLong(IntPtr stmt, int index, long value);
    [DllImport(Sqlite, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    private static extern long Long(IntPtr stmt, int index);
    [DllImport(Sqlite, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ColumnText(IntPtr stmt, int index);
    [DllImport(Sqlite, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Step(IntPtr stmt);
    [DllImport(Sqlite, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FinalizeStatement(IntPtr stmt);
    [DllImport(Sqlite, EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Error(IntPtr db);
    [DllImport(Sqlite, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Close(IntPtr db);
}
