const DEFAULTS = {
  enabled: true,
  minSizeMB: 1,
  excludedSites: "",
  fileTypes: "zip rar 7z gz tar iso exe msi apk dmg mkv mp4 avi mov webm mp3 flac m4a pdf epub srt",
};

const t = (key) => chrome.i18n.getMessage(key) || key;

// Localise, and switch to right-to-left for Persian.
document.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));
if (chrome.i18n.getUILanguage().toLowerCase().startsWith("fa")) document.documentElement.dir = "rtl";

const fields = ["enabled", "minSizeMB", "excludedSites", "fileTypes"];
let savedTimer;

chrome.storage.sync.get(DEFAULTS).then((s) => {
  for (const f of fields) {
    const el = document.getElementById(f);
    if (el.type === "checkbox") el.checked = !!s[f];
    else el.value = s[f];
  }
});

for (const f of fields) {
  document.getElementById(f).addEventListener("change", (e) => {
    const el = e.target;
    const value = el.type === "checkbox" ? el.checked : el.type === "number" ? Math.max(0, Number(el.value) || 0) : el.value.trim();
    chrome.storage.sync.set({ [f]: value }).then(() => {
      const saved = document.getElementById("saved");
      saved.hidden = false;
      clearTimeout(savedTimer);
      savedTimer = setTimeout(() => (saved.hidden = true), 1500);
    });
  });
}

// Connection status: the host answers even when the app is closed ("app-not-running").
chrome.runtime.sendMessage({ type: "ping" }).then((reply) => {
  const box = document.getElementById("status");
  const text = document.getElementById("statusText");
  if (reply?.ok) {
    box.classList.add("ok");
    text.textContent = t("popupConnected") + (reply.version ? ` · v${reply.version}` : "");
  } else if (reply?.error === "app-not-running") {
    box.classList.add("ok");
    text.textContent = t("popupHostOnly");
  } else {
    box.classList.add("warn");
    text.textContent = t("popupNotConnected");
    document.getElementById("help").hidden = false;
  }
});
