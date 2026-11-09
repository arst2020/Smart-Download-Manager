// Smart Download Manager — browser extension (Manifest V3).
// Hands browser downloads to the desktop app through native messaging, together with the
// cookies, referrer and user agent, so servers see the same session as the browser.

const HOST = "com.smartdownloadmanager.host";

const DEFAULTS = {
  enabled: true,
  minSizeMB: 1,
  excludedSites: "",
  fileTypes: "zip rar 7z gz tar iso exe msi apk dmg mkv mp4 avi mov webm mp3 flac m4a pdf epub srt",
};

async function getSettings() {
  return Object.assign({}, DEFAULTS, await chrome.storage.sync.get(DEFAULTS));
}

/** Sends one message to the native host; never throws. */
function sendToApp(message) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendNativeMessage(HOST, message, (reply) => {
        if (chrome.runtime.lastError) resolve({ ok: false, error: chrome.runtime.lastError.message });
        else resolve(reply || { ok: false, error: "No reply" });
      });
    } catch (e) {
      resolve({ ok: false, error: String(e) });
    }
  });
}

async function cookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

function isExcluded(url, excludedSites) {
  let host;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return true;
  }
  return excludedSites
    .split(/[\s,،]+/)
    .map((d) => d.trim().toLowerCase().replace(/^\*?\./, ""))
    .filter(Boolean)
    .some((d) => host === d || host.endsWith("." + d));
}

function baseName(path) {
  return (path || "").split(/[\\/]/).pop() || "";
}

async function activeTabTitle() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    return tab?.title || "";
  } catch {
    return "";
  }
}

function flashBadge(text, color, title) {
  chrome.action.setBadgeBackgroundColor({ color });
  chrome.action.setBadgeText({ text });
  if (title) chrome.action.setTitle({ title });
  setTimeout(() => {
    chrome.action.setBadgeText({ text: "" });
    chrome.action.setTitle({ title: "Smart Download Manager" });
  }, 4000);
}

// Downloads we let the browser finish on purpose (app unreachable) — never capture them twice.
const passThrough = new Set();

// ---------------------------------------------------------------- capture browser downloads
chrome.downloads.onCreated.addListener(async (item) => {
  const url = item.finalUrl || item.url;
  if (passThrough.has(item.id) || !/^https?:/i.test(url)) return; // blob:, data:, file: stay in the browser

  const settings = await getSettings();
  if (!settings.enabled || isExcluded(url, settings.excludedSites)) return;

  const size = item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : -1;
  if (size > 0 && size < settings.minSizeMB * 1024 * 1024) return;

  // Pause first: if the app cannot be reached, the browser simply continues.
  try {
    await chrome.downloads.pause(item.id);
  } catch {
    /* already finished or not pausable */
  }

  const reply = await sendToApp({
    type: "download",
    url,
    referrer: item.referrer || "",
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
    fileName: baseName(item.filename),
    fileSize: size > 0 ? size : undefined,
    pageTitle: await activeTabTitle(),
  });

  if (reply.ok) {
    try {
      await chrome.downloads.cancel(item.id);
      await chrome.downloads.erase({ id: item.id });
    } catch {
      /* ignore */
    }
    flashBadge("✓", "#2E7D32");
  } else {
    passThrough.add(item.id);
    try {
      await chrome.downloads.resume(item.id);
    } catch {
      /* ignore */
    }
    flashBadge("!", "#B7791F", "Smart Download Manager: " + (reply.error || "not reachable"));
  }
});

// ---------------------------------------------------------------- context menus
function createMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: "sdm-link", title: chrome.i18n.getMessage("menuLink"), contexts: ["link", "video", "audio"] });
    chrome.contextMenus.create({ id: "sdm-selection", title: chrome.i18n.getMessage("menuSelection"), contexts: ["selection"] });
    chrome.contextMenus.create({ id: "sdm-page", title: chrome.i18n.getMessage("menuPage"), contexts: ["page"] });
  });
}
chrome.runtime.onInstalled.addListener(createMenus);
chrome.runtime.onStartup.addListener(createMenus);

/** Collects link targets in the page (runs inside the tab). */
function collectLinks(selectionOnly) {
  const urls = new Set();
  const add = (u) => {
    try {
      if (u) urls.add(new URL(u, location.href).href);
    } catch {
      /* ignore */
    }
  };
  if (selectionOnly) {
    const sel = window.getSelection();
    for (let i = 0; i < sel.rangeCount; i++) {
      const frag = sel.getRangeAt(i).cloneContents();
      frag.querySelectorAll("a[href]").forEach((a) => add(a.getAttribute("href")));
      (frag.textContent || "").match(/(?:https?|ftp):\/\/[^\s<>"']+/g)?.forEach(add);
    }
  } else {
    document.querySelectorAll("a[href]").forEach((a) => add(a.href));
    document.querySelectorAll("video[src], audio[src], source[src]").forEach((m) => add(m.src));
  }
  return [...urls];
}

function matchesTypes(url, fileTypes) {
  const types = fileTypes.split(/[\s,]+/).map((t) => t.replace(/^\./, "").toLowerCase()).filter(Boolean);
  try {
    const path = new URL(url).pathname.toLowerCase();
    return types.some((t) => path.endsWith("." + t));
  } catch {
    return false;
  }
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const settings = await getSettings();
  const pageUrl = info.pageUrl || tab?.url || "";

  if (info.menuItemId === "sdm-link") {
    const url = info.linkUrl || info.srcUrl;
    if (!url) return;
    const reply = await sendToApp({
      type: "download",
      url,
      referrer: pageUrl,
      cookies: await cookieHeader(url),
      userAgent: navigator.userAgent,
      pageTitle: tab?.title || "",
    });
    flashBadge(reply.ok ? "✓" : "!", reply.ok ? "#2E7D32" : "#B7791F", reply.ok ? "" : reply.error);
    return;
  }

  if (!tab?.id) return;
  const selectionOnly = info.menuItemId === "sdm-selection";
  let links = [];
  try {
    const [result] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: collectLinks, args: [selectionOnly] });
    links = result?.result || [];
  } catch (e) {
    flashBadge("!", "#B7791F", String(e));
    return;
  }
  // The selection is taken as-is; for a whole page keep only downloadable file types.
  if (!selectionOnly) links = links.filter((u) => matchesTypes(u, settings.fileTypes));
  links = links.filter((u) => /^(https?|ftp):/i.test(u));
  if (links.length === 0) {
    flashBadge("0", "#5B6676", chrome.i18n.getMessage("noLinks"));
    return;
  }

  const reply = await sendToApp({
    type: "batch",
    links,
    referrer: pageUrl,
    cookies: await cookieHeader(pageUrl),
    userAgent: navigator.userAgent,
    pageTitle: tab.title || "",
  });
  flashBadge(reply.ok ? String(links.length) : "!", reply.ok ? "#2E7D32" : "#B7791F", reply.ok ? "" : reply.error);
});

// ---------------------------------------------------------------- popup status
chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === "ping") {
    sendToApp({ type: "ping" }).then(sendResponse);
    return true; // async response
  }
  return false;
});
