const browser = /\bEdg\//.test(navigator.userAgent) ? "edge" : "chrome";
const alarm = "afterhours-check-in";
let revision = 0;

async function report() {
  const request = ++revision;
  let host = "";
  let focused = false;
  let video = false;
  try {
    const win = await chrome.windows.getLastFocused();
    focused = win.focused && !win.incognito;
    const [tab] = focused ? await chrome.tabs.query({ active: true, windowId: win.id }) : [];
    if (win.focused && !win.incognito && tab?.url && !tab.incognito) {
      const url = new URL(tab.url);
      if (url.protocol === "https:" || url.protocol === "http:") {
        host = url.hostname.toLowerCase().replace(/^www\./, "").replace(/\.$/, "");
        const origin = `${url.protocol}//${url.hostname}/*`;
        if (await chrome.permissions.contains({ origins: [origin] })) {
          try {
            const [frame] = await chrome.scripting.executeScript({
              target: { tabId: tab.id },
              files: ["video.js"]
            });
            video = frame?.result === true;
          } catch {
            // The tab may navigate or close between permission check and execution.
          }
        }
      }
    }
  } catch {
    // A window may disappear while the browser is changing focus.
  }
  if (request !== revision) return;
  try {
    const status = await chrome.runtime.sendNativeMessage("com.afterhours.local",
      { Browser: browser, Host: host, Focused: focused, Video: video });
    if (request === revision) {
      await chrome.action.setBadgeText({ text: status.connected ? "" : "OFF" });
      await chrome.action.setTitle({
        title: status.connected ? "AfterHours · Connected on this PC" : "AfterHours · Local connection unavailable"
      });
    }
  } catch {
    if (request !== revision) return;
    await chrome.action.setBadgeText({ text: "OFF" });
    await chrome.action.setTitle({ title: "AfterHours · Enable website tracking in the desktop app" });
  }
}

async function start() {
  if (!await chrome.alarms.get(alarm)) await chrome.alarms.create(alarm, { periodInMinutes: 0.5 });
  await report();
}

chrome.tabs.onActivated.addListener(report);
chrome.tabs.onUpdated.addListener((_id, change, tab) => {
  if (tab.active && (change.url || change.status === "complete")) report();
});
chrome.windows.onFocusChanged.addListener(report);
chrome.windows.onRemoved.addListener(report);
chrome.alarms.onAlarm.addListener(event => {
  if (event.name === alarm) report();
});
chrome.runtime.onInstalled.addListener(start);
chrome.runtime.onStartup.addListener(start);
chrome.permissions.onAdded.addListener(report);
chrome.permissions.onRemoved.addListener(report);
chrome.runtime.onMessage.addListener((message, sender) => {
  if (message === "afterhours-video-changed" && sender.tab?.active && !sender.tab.incognito) report();
  if (message === "afterhours-check" && sender.url === chrome.runtime.getURL("popup.html")) report();
});
