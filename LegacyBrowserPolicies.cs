using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Win32;

namespace AfterHours;

internal static class LegacyBrowserPolicies
{
    public static bool Remove(History history)
    {
        bool success = true;
        foreach (string browser in new[] { @"Google\Chrome", @"Microsoft\Edge" })
        foreach (string list in new[] { "URLBlocklist", "URLAllowlist" })
        {
            try { RemoveOwned(history, $@"Software\Policies\{browser}\{list}"); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException or JsonException)
            {
                success = false;
            }
        }
        return success;
    }

    private static void RemoveOwned(History history, string path)
    {
        string ledger = "browser_policy:" + path;
        string createdKey = ledger + ":created";
        var owned = JsonSerializer.Deserialize<Dictionary<string, string>>(history.Get(ledger, "{}")) ?? new();
        bool created = history.Get(createdKey, "0") == "1";
        if (owned.Count == 0 && !created) return;

        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        if (key != null)
        {
            foreach (var (name, value) in owned)
                if (Equals(key.GetValue(name), value)) key.DeleteValue(name, throwOnMissingValue: false);
            if (created && key.ValueCount == 0 && key.SubKeyCount == 0)
            {
                key.Close();
                Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
            }
        }
        history.RemoveSetting(ledger);
        history.RemoveSetting(createdKey);
    }
}
