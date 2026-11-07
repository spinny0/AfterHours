const status = document.querySelector("#status");
const button = document.querySelector("#permission");

async function setup() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.url || tab.incognito || !/^https?:/.test(tab.url)) {
    status.textContent = "Open a website in a regular window to allow video tracking.";
    return;
  }
  const url = new URL(tab.url);
  const access = { origins: [url.protocol + "//" + url.hostname + "/*"] };
  let granted = false;
  async function refresh() {
    granted = await chrome.permissions.contains(access);
    status.textContent = granted
      ? "Video detection allowed on " + url.hostname + ". Turn on Count foreground video in AfterHours Settings."
      : "Allow AfterHours to check for a playing video on " + url.hostname + ".";
    button.textContent = granted ? "Remove access" : "Allow on this site";
    button.hidden = false;
  }
  await refresh();
  button.addEventListener("click", async () => {
    try {
      if (granted) await chrome.permissions.remove(access);
      else await chrome.permissions.request(access);
      await refresh();
      chrome.runtime.sendMessage("afterhours-check").catch(() => {});
    } catch {
      status.textContent = "Site access could not be changed.";
    }
  });
}

setup().catch(() => { status.textContent = "This tab is unavailable."; });
