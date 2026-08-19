// DiSky Atlas — browser interop: theme, clipboard, scroll, ⌘K hotkey.
window.atlas = (function () {
  const THEME_KEY = "disky-theme";

  function applyTheme(theme) {
    const root = document.documentElement;
    root.setAttribute("data-theme", theme);
    root.classList.toggle("dark", theme === "dark");
  }

  function getTheme() {
    return document.documentElement.getAttribute("data-theme") || "dark";
  }

  function setTheme(theme) {
    applyTheme(theme);
    try { localStorage.setItem(THEME_KEY, theme); } catch (e) { /* ignore */ }
    return theme;
  }

  function toggleTheme() {
    return setTheme(getTheme() === "dark" ? "light" : "dark");
  }

  // Runs from an inline <head> script too (see App.razor) to avoid a flash.
  function initTheme() {
    let theme = "dark";
    try { theme = localStorage.getItem(THEME_KEY) || "dark"; } catch (e) { /* ignore */ }
    applyTheme(theme);
    return theme;
  }

  function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).then(() => true).catch(() => fallbackCopy(text));
    }
    return Promise.resolve(fallbackCopy(text));
  }

  function fallbackCopy(text) {
    try {
      const ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand("copy");
      document.body.removeChild(ta);
      return ok;
    } catch (e) { return false; }
  }

  function scrollToId(id, smooth) {
    const el = document.getElementById(id);
    if (el) {
      if (el.classList.contains("record")) {
        el.classList.add("force-open");
        flashElement(el);
      }
      el.scrollIntoView({ behavior: smooth ? "smooth" : "auto", block: "start" });
    }
    return !!el;
  }

  function flashElement(el) {
    el.classList.remove("atlas-flash");
    void el.offsetWidth; // force reflow so the animation restarts on a repeat click
    el.classList.add("atlas-flash");
    el.addEventListener("animationend", function handler(e) {
      if (e.animationName === "atlas-flash") {
        el.classList.remove("atlas-flash");
        el.removeEventListener("animationend", handler);
      }
    });
  }

  function clearForceOpen(id) {
    const el = document.getElementById(id);
    if (el) el.classList.remove("force-open");
  }

  function focusSelector(selector) {
    const el = document.querySelector(selector);
    if (el) el.focus();
  }

  function scrollTop(selector, smooth) {
    const el = document.querySelector(selector);
    if (el) el.scrollTo({ top: 0, behavior: smooth ? "smooth" : "auto" });
  }

  // ---- Settings (display mode + badge visibility), persisted to localStorage ----
  const SETTINGS_KEY = "disky-settings";
  const BADGE_KEYS = ["kind", "return", "change", "async", "shared", "since", "intents"];
  const SETTINGS_DEFAULTS = { display: "compact", badges: {} };
  BADGE_KEYS.forEach(k => SETTINGS_DEFAULTS.badges[k] = true);

  function readSettings() {
    let raw = {};
    try { raw = JSON.parse(localStorage.getItem(SETTINGS_KEY) || "{}"); } catch (e) { /* ignore */ }
    const badges = {};
    BADGE_KEYS.forEach(k => { badges[k] = (raw.badges && k in raw.badges) ? !!raw.badges[k] : true; });
    return { display: raw.display || SETTINGS_DEFAULTS.display, advanced: !!raw.advanced, badges: badges };
  }

  function applySettings(s) {
    const root = document.documentElement;
    root.setAttribute("data-syntax-display", s.display);
    root.setAttribute("data-advanced", s.advanced ? "on" : "off");
    BADGE_KEYS.forEach(k => root.setAttribute("data-badge-" + k, s.badges[k] ? "on" : "off"));
  }

  function persistSettings(s) {
    try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(s)); } catch (e) { /* ignore */ }
    applySettings(s);
  }

  function getSettings() { const s = readSettings(); applySettings(s); return s; }

  function setDisplay(mode) { const s = readSettings(); s.display = mode; persistSettings(s); return s; }

  function setBadge(key, on) { const s = readSettings(); s.badges[key] = !!on; persistSettings(s); return s; }

  function setAdvanced(on) { const s = readSettings(); s.advanced = !!on; persistSettings(s); return s; }

  // ---- ⌘K / Ctrl+K global hotkey, dispatched to a .NET object ----
  let hotkeyHandler = null;

  function registerHotkey(dotNetRef) {
    unregisterHotkey();
    hotkeyHandler = function (e) {
      const k = (e.key || "").toLowerCase();
      if ((e.metaKey || e.ctrlKey) && k === "k") {
        e.preventDefault();
        dotNetRef.invokeMethodAsync("OpenPalette");
      }
    };
    document.addEventListener("keydown", hotkeyHandler);
  }

  function unregisterHotkey() {
    if (hotkeyHandler) {
      document.removeEventListener("keydown", hotkeyHandler);
      hotkeyHandler = null;
    }
  }

  return {
    initTheme, getTheme, setTheme, toggleTheme,
    copyText, scrollToId, clearForceOpen, focusSelector, scrollTop,
    getSettings, setDisplay, setBadge, setAdvanced,
    registerHotkey, unregisterHotkey
  };
})();
