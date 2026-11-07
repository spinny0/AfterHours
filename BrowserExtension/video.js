(() => {
  function playing() {
    return document.visibilityState === "visible" && Array.from(document.querySelectorAll("video")).some(video => {
      if (video.paused || video.ended || video.readyState < 2 || video.playbackRate <= 0) return false;
      const rect = video.getBoundingClientRect();
      const style = getComputedStyle(video);
      return rect.width >= 100 && rect.height >= 60 && rect.right > 0 && rect.bottom > 0 &&
        rect.left < innerWidth && rect.top < innerHeight && style.visibility === "visible" && style.opacity !== "0";
    });
  }

  const current = playing();
  if (!globalThis.afterHoursVideoObserver) {
    let previous = current;
    const update = () => {
      const next = playing();
      if (next === previous) return;
      previous = next;
      chrome.runtime.sendMessage("afterhours-video-changed").catch(() => {
        // An extension reload invalidates the old isolated world.
      });
    };
    globalThis.afterHoursVideoObserver = true;
    for (const event of ["playing", "pause", "ended", "waiting", "emptied", "visibilitychange"]) {
      document.addEventListener(event, update, true);
    }
    addEventListener("scroll", update, { passive: true });
  }
  return current;
})()
