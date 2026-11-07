# AfterHours

AfterHours tracks time and sessions on a Windows PC. It keeps its history in SQLite under `%LOCALAPPDATA%\AfterHours` and works offline.

## Open the app

On Windows 11 with the .NET 10 Desktop Runtime, open `AfterHours.exe` from the published folder. Keep the other files in that folder alongside the executable.

From source, run `dotnet run --project AfterHours.csproj -c Release`. To build a folder you can move as a unit, run `dotnet publish AfterHours.csproj -c Release -o publish`.

The dashboard closes to the tray. Right-click the tray icon to pause, resume, or quit. Settings has an optional start-with-Windows switch.

To upgrade from an earlier folder, quit the old AfterHours from the tray before opening the new executable. If you use website tracking, turn it off and back on in Settings, then replace the old unpacked extension with the companion from the new folder.

## Add website tracking

1. In AfterHours, open **Settings** and enable **Track website domains**.
2. Open **Extensions** in Chrome or Edge and turn on **Developer mode**.
3. Choose **Load unpacked** and select the folder shown by **Open companion folder** in AfterHours.
4. Bring a normal website to the foreground. AfterHours shows the connection on the **Websites** page. Repeat in any browser profile you want to track.

The companion shares domains over a local connection. It skips private windows and browser pages. If you move the app folder, turn website tracking off and on again so Windows can find the new executable.

## How to read the numbers

- Active time counts until the idle cutoff. Locking or sleeping stops the clock. Extended reading or video playback without input can reach the cutoff.
- A session is a stretch of active use. App switches do not start new sessions.
- The daily average uses completed days with tracking data. The graph distinguishes observed days from dates with no data.
- App time uses the foreground app. A window close is recorded when the last tracked main window for that app is destroyed. App exits can count too.
- Sites can be sorted by visits or time. Visits count when a domain comes into focus. Website time sits within app time.
- Excluded names are omitted from future breakdowns while their time still counts toward the PC total. Settings can export the history or delete a selected date or all dates.

## Day details, recap, categories, and limits

- Select a day in the Overview chart, or use the calendar in **Day details**, to see apps, websites, and gaps in order.
- **Weekly recap** compares the last seven completed days to the preceding seven, pairing weekdays that have tracking data in both periods.
- **Categories** group app names and domains. The most specific matching website rule takes precedence over an app rule, so category totals count each span once.
- **Time limits** gives you one reminder per day when you reach the budget for an app, website domain, or category.

## Upgrading from a version with blocking

Your existing time limits become reminders. When the new version starts, it clears Chrome and Edge policy entries that AfterHours previously added. Replace the browser companion using the steps above to clear its temporary rules, then reopen any affected sites. Chrome or Edge may need a restart to pick up the policy changes.

## Count video past the idle cutoff

1. Load the companion from this version in Chrome or Edge.
2. In AfterHours **Settings**, turn on **Track website domains** and **Count foreground video**.
3. On the website you want to track, open the AfterHours browser extension and choose **Allow on this site**.

Video tracking uses a playback signal from visible HTML video in the focused tab. It covers sites where the companion can see the video in the page, with support varying for embedded players. It shares the domain and a playback flag locally. You can remove site access from the extension popup.
