#!/usr/bin/env node
// Browser-level audit (Playwright + Chromium):
//  - Core Web Vitals (web-vitals lib injected): LCP, CLS, TTFB, INP
//  - network throttling profiles (none / Fast 3G / 4G)
//  - SignalR circuit: transport (WebSocket vs long-polling), click→DOM latency,
//    network-cut reconnection behaviour (CDP), state preservation
//  - axe-core (wcag2a, wcag2aa, wcag21aa, best-practice)
//  - heading hierarchy, images without dimensions
//  - keyboard walk on a doc page (focus visibility, skip link, focus after SPA nav)
//  - no-JS rendering (prerender output)
//
//   node browser-audit.js [prod|local] > out/browser-<target>.json

const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const cfg = require('./pages.json');

const target = process.argv[2] || 'local';
const BASE = cfg[target];
const webVitalsSrc = fs.readFileSync(path.join(__dirname, 'node_modules/web-vitals/dist/web-vitals.iife.js'), 'utf8');
const axeSrc = fs.readFileSync(path.join(__dirname, 'node_modules/axe-core/axe.min.js'), 'utf8');

const THROTTLE = {
  none: null,
  fast3g: { downloadThroughput: 1.6 * 1024 * 1024 / 8, uploadThroughput: 750 * 1024 / 8, latency: 150 },
  slow4g: { downloadThroughput: 9 * 1024 * 1024 / 8, uploadThroughput: 1.5 * 1024 * 1024 / 8, latency: 60 },
};

// Sandbox: the egress relay TLS-fingerprints clients and resets Chromium's
// (BoringSSL) connections while curl/openssl/Node pass. For PROD the browser's
// requests are therefore re-originated through Playwright's Node-side fetch
// (route interception). Consequences, recorded in the output: network
// emulation does not apply to fulfilled routes (no throttled profiles on
// PROD) and WebSockets cannot be relayed (no SignalR transport test on PROD).
const RELAY = BASE.startsWith('https');

async function withPage(browser, fn, { js = true } = {}) {
  const context = await browser.newContext({
    javaScriptEnabled: js, viewport: { width: 1366, height: 900 },
    ...(RELAY ? { proxy: { server: process.env.HTTPS_PROXY } } : {}),
  });
  if (RELAY) {
    await context.route('**/*', async (route) => {
      try {
        const response = await context.request.fetch(route.request(), { maxRedirects: 0, timeout: 60_000 });
        await route.fulfill({ response });
      } catch { await route.abort(); }
    });
  }
  const page = await context.newPage();
  try { return await fn(page, context); } finally { await context.close(); }
}

async function applyThrottle(page, profile) {
  if (!profile) return;
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', { offline: false, ...profile });
}

// ── Core Web Vitals; INP via a real click on an expandable element ──
async function vitals(browser, pagePath, profile) {
  return withPage(browser, async (page) => {
    await applyThrottle(page, THROTTLE[profile]);
    await page.addInitScript(webVitalsSrc + `;
      window.__vitals = {};
      webVitals.onLCP(m => window.__vitals.LCP = m.value, {reportAllChanges: true});
      webVitals.onCLS(m => window.__vitals.CLS = m.value, {reportAllChanges: true});
      webVitals.onTTFB(m => window.__vitals.TTFB = m.value);
      webVitals.onINP(m => window.__vitals.INP = m.value, {reportAllChanges: true});
      webVitals.onFCP(m => window.__vitals.FCP = m.value);
    `);
    await page.goto(BASE + pagePath, { waitUntil: 'load', timeout: 90_000 });
    await page.waitForTimeout(2500); // let the circuit come up
    // one real interaction so INP has data: click something interactive if present
    const clickable = page.locator('button, [role="button"], a[href^="#"], summary').first();
    if (await clickable.count()) { try { await clickable.click({ timeout: 3000 }); } catch {} }
    await page.waitForTimeout(1500);
    await page.evaluate(() => { document.body.dispatchEvent(new Event('visibilitychange')); });
    return await page.evaluate(() => {
      const v = window.__vitals;
      const r = {};
      for (const k of ['LCP', 'CLS', 'TTFB', 'INP', 'FCP']) r[k] = v[k] == null ? null : Math.round(v[k] * 1000) / 1000;
      return r;
    });
  });
}

// ── SignalR transport + click→DOM-mutation latency + reconnection ──
async function signalr(browser) {
  return withPage(browser, async (page, context) => {
    const wsUrls = [];
    const pollUrls = [];
    page.on('websocket', ws => wsUrls.push(ws.url()));
    page.on('request', req => { if (req.url().includes('_blazor') && !req.url().includes('negotiate')) pollUrls.push(req.url().split('?')[0]); });
    await page.goto(BASE + '/guild', { waitUntil: 'load', timeout: 90_000 });
    await page.waitForTimeout(3000);
    const transport = wsUrls.some(u => u.includes('_blazor')) ? 'websocket'
      : pollUrls.length > 1 ? 'long-polling' : 'unknown';

    // click→first DOM mutation on an interactive element (server round-trip)
    let clickLatencies = [];
    const search = page.locator('button, [role="combobox"], input[type="search"], [role="button"]').first();
    for (let i = 0; i < 5; i++) {
      const lat = await page.evaluate(async () => {
        const el = document.querySelector('button, [role="combobox"], [role="button"]');
        if (!el) return null;
        return new Promise(resolve => {
          const obs = new MutationObserver(() => { obs.disconnect(); resolve(performance.now() - t0); });
          obs.observe(document.body, { childList: true, subtree: true, attributes: true, characterData: true });
          const t0 = performance.now();
          el.click();
          setTimeout(() => { obs.disconnect(); resolve(null); }, 5000);
        });
      });
      if (lat != null) clickLatencies.push(Math.round(lat));
      await page.keyboard.press('Escape').catch(() => {});
      await page.waitForTimeout(400);
    }

    // network cut via CDP → observe reconnection overlay + recovery time + state
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    // put some state in: type into a search box if present
    let statePut = false;
    try {
      const input = page.locator('input').first();
      if (await input.count()) { await input.fill('ban', { timeout: 3000 }); statePut = true; }
    } catch {}
    await cdp.send('Network.emulateNetworkConditions', { offline: true, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
    await page.waitForTimeout(4000);
    const overlayDuringCut = await page.evaluate(() => {
      const el = document.querySelector('#components-reconnect-modal, [data-nosnippet] dialog, dialog');
      if (!el) return null;
      const cs = getComputedStyle(el);
      return { visible: cs.display !== 'none', role: el.getAttribute('role') || el.tagName.toLowerCase(),
               ariaLive: el.getAttribute('aria-live'), text: (el.textContent || '').trim().slice(0, 120) };
    });
    const tCut = Date.now();
    await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
    let recoveredInMs = null;
    for (let i = 0; i < 60; i++) {
      await page.waitForTimeout(500);
      const gone = await page.evaluate(() => {
        const el = document.querySelector('#components-reconnect-modal, dialog[open]');
        return !el || getComputedStyle(el).display === 'none';
      });
      if (gone) { recoveredInMs = Date.now() - tCut; break; }
    }
    let statePreserved = null;
    if (statePut) {
      try { statePreserved = (await page.locator('input').first().inputValue()) === 'ban'; } catch { statePreserved = null; }
    }
    return {
      transport, blazorWsUrls: wsUrls.filter(u => u.includes('_blazor')).map(u => u.split('?')[0]),
      clickToDomMutationMs: clickLatencies.length ? { samples: clickLatencies, median: clickLatencies.sort((a, b) => a - b)[Math.floor(clickLatencies.length / 2)] } : null,
      reconnect: { overlayDuringCut, recoveredInMs, statePreserved },
    };
  });
}

// ── axe-core ──
async function axe(browser, pagePath) {
  return withPage(browser, async (page) => {
    await page.goto(BASE + pagePath, { waitUntil: 'load', timeout: 90_000 });
    await page.waitForTimeout(2000);
    await page.evaluate(axeSrc);
    const res = await page.evaluate(async () => {
      const r = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice'] } });
      return r.violations.map(v => ({
        id: v.id, impact: v.impact, help: v.help, tags: v.tags.filter(t => t.startsWith('wcag') || t === 'best-practice'),
        nodes: v.nodes.slice(0, 5).map(n => ({ target: n.target.join(' '), html: n.html.slice(0, 200) })),
        nodeCount: v.nodes.length,
      }));
    });
    return res;
  });
}

// ── structure: headings, images, aria-live regions, lang ──
async function structure(browser, pagePath) {
  return withPage(browser, async (page) => {
    await page.goto(BASE + pagePath, { waitUntil: 'load', timeout: 90_000 });
    await page.waitForTimeout(1500);
    return await page.evaluate(() => {
      const hs = [...document.querySelectorAll('h1,h2,h3,h4,h5,h6')].map(h => ({ level: +h.tagName[1], text: h.textContent.trim().slice(0, 60) }));
      let jumps = [];
      for (let i = 1; i < hs.length; i++) if (hs[i].level > hs[i - 1].level + 1) jumps.push(`${hs[i - 1].level}→${hs[i].level} avant “${hs[i].text}”`);
      const imgs = [...document.querySelectorAll('img')].map(i => ({ src: (i.getAttribute('src') || '').slice(0, 80), hasDims: i.hasAttribute('width') && i.hasAttribute('height') }));
      return {
        lang: document.documentElement.lang || null,
        h1Count: hs.filter(h => h.level === 1).length,
        headingCount: hs.length, levelJumps: jumps,
        imagesWithoutDims: imgs.filter(i => !i.hasDims).map(i => i.src),
        ariaLiveRegions: [...document.querySelectorAll('[aria-live], [role="status"], [role="alert"]')].map(e => ({ tag: e.tagName.toLowerCase(), live: e.getAttribute('aria-live') || e.getAttribute('role') })),
        skipLink: !!document.querySelector('a[href="#main-content"], a[href^="#"][class*="skip"], .skip-link'),
      };
    });
  });
}

// ── keyboard walk + SPA nav focus/announcement ──
async function keyboard(browser) {
  return withPage(browser, async (page) => {
    await page.goto(BASE + '/docs/contributing/style-guide', { waitUntil: 'load', timeout: 90_000 });
    await page.waitForTimeout(2500);
    const steps = [];
    for (let i = 0; i < 25; i++) {
      await page.keyboard.press('Tab');
      const info = await page.evaluate(() => {
        const el = document.activeElement;
        if (!el || el === document.body) return { tag: 'body' };
        const cs = getComputedStyle(el);
        const visibleOutline = cs.outlineStyle !== 'none' || cs.boxShadow !== 'none';
        const r = el.getBoundingClientRect();
        return { tag: el.tagName.toLowerCase(), text: (el.textContent || el.getAttribute('aria-label') || '').trim().slice(0, 40), visibleOutline, inViewport: r.top >= 0 && r.top < innerHeight, w: Math.round(r.width), h: Math.round(r.height) };
      });
      steps.push(info);
    }
    // SPA navigation: click a nav link, check focus location + announcement
    const before = await page.evaluate(() => document.activeElement?.tagName || 'BODY');
    const navLink = page.locator('a[href^="/"]:visible').first();
    let spa = null;
    if (await navLink.count()) {
      await navLink.click();
      await page.waitForTimeout(2500);
      spa = await page.evaluate(() => ({
        activeAfterNav: document.activeElement ? document.activeElement.tagName.toLowerCase() + (document.activeElement.id ? '#' + document.activeElement.id : '') : null,
        liveRegionContent: [...document.querySelectorAll('[aria-live], [role="status"]')].map(e => e.textContent.trim()).filter(Boolean),
        title: document.title,
      }));
      spa.focusMovedToContent = !['body', 'a'].includes((spa.activeAfterNav || 'body').split('#')[0]);
    }
    const focusable = steps.filter(s => s.tag !== 'body');
    return {
      tabStops: steps,
      summary: {
        stopsReached: focusable.length,
        withoutVisibleFocus: focusable.filter(s => !s.visibleOutline).length,
        firstStop: steps[0] || null,
        skipLinkFirst: steps[0] && /skip|aller au contenu/i.test(steps[0].text || ''),
      },
      spaNavigation: spa,
    };
  });
}

// ── no-JS rendering ──
async function nojs(browser, pagePath) {
  return withPage(browser, async (page) => {
    const resp = await page.goto(BASE + pagePath, { waitUntil: 'load', timeout: 90_000 });
    await page.waitForTimeout(500);
    return await page.evaluate((status) => {
      const main = document.querySelector('main') || document.body;
      const text = main.innerText.replace(/\s+/g, ' ').trim();
      return {
        status, textLength: text.length, textSample: text.slice(0, 150),
        h1: document.querySelector('h1')?.textContent.trim() || null,
        linkCount: document.querySelectorAll('a[href]').length,
        looksEmpty: text.length < 200,
      };
    }, resp.status());
  }, { js: false });
}

(async () => {
  // In this sandbox all outbound HTTPS must go through the local CONNECT proxy;
  // Chromium does not read HTTPS_PROXY on its own. Local traffic bypasses it.
  const proxy = process.env.HTTPS_PROXY && BASE.startsWith('https')
    ? { server: process.env.HTTPS_PROXY, bypass: '127.0.0.1,localhost' } : undefined;
  const browser = await chromium.launch({ proxy });
  const result = { target, base: BASE, date: new Date().toISOString(), webVitals: {}, signalr: null, axe: {}, structure: {}, keyboard: null, noJs: {} };

  const profiles = RELAY ? ['none'] : ['none', 'fast3g', 'slow4g'];
  for (const p of cfg.pages) {
    result.webVitals[p.id] = {};
    for (const profile of profiles) {
      try { result.webVitals[p.id][profile] = await vitals(browser, p.path, profile); }
      catch (e) { result.webVitals[p.id][profile] = { error: String(e).slice(0, 200) }; }
      console.error(`vitals ${p.id} ${profile} ok`);
    }
    if (RELAY) {
      const reason = 'throttling CDP inopérant sur routes relayées (fetch Node) — mesuré en LOCAL uniquement';
      result.webVitals[p.id].fast3g = { null: true, reason };
      result.webVitals[p.id].slow4g = { null: true, reason };
    }
    try { result.axe[p.id] = await axe(browser, p.path); } catch (e) { result.axe[p.id] = { error: String(e).slice(0, 200) }; }
    console.error(`axe ${p.id} ok`);
    try { result.structure[p.id] = await structure(browser, p.path); } catch (e) { result.structure[p.id] = { error: String(e).slice(0, 200) }; }
    try { result.noJs[p.id] = await nojs(browser, p.path); } catch (e) { result.noJs[p.id] = { error: String(e).slice(0, 200) }; }
    console.error(`structure+nojs ${p.id} ok`);
  }
  if (RELAY) {
    result.signalr = { null: true, reason: 'WebSocket non tunnelable par le proxy sandbox (limitation documentée) — transport/reconnexion mesurés en LOCAL uniquement' };
  } else {
    try { result.signalr = await signalr(browser); } catch (e) { result.signalr = { error: String(e).slice(0, 300) }; }
    console.error('signalr ok');
  }
  try { result.keyboard = await keyboard(browser); } catch (e) { result.keyboard = { error: String(e).slice(0, 300) }; }
  console.error('keyboard ok');

  await browser.close();
  process.stdout.write(JSON.stringify(result, null, 2));
})();
