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
    if (el) el.scrollIntoView({ behavior: smooth ? "smooth" : "auto", block: "start" });
    return !!el;
  }

  function focusSelector(selector) {
    const el = document.querySelector(selector);
    if (el) el.focus();
  }

  function scrollTop(selector) {
    const el = document.querySelector(selector);
    if (el) el.scrollTop = 0;
  }

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
    copyText, scrollToId, focusSelector, scrollTop,
    registerHotkey, unregisterHotkey
  };
})();
