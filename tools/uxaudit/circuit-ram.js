#!/usr/bin/env node
// Estimate server memory per Blazor circuit: open N concurrent interactive
// tabs against LOCAL, sample the dotnet process RSS before/after.
//
//   node circuit-ram.js <pid> [tabs=20] > out/circuit-ram.json

const { chromium } = require('playwright');
const fs = require('node:fs');
const cfg = require('./pages.json');

const pid = parseInt(process.argv[2], 10);
const TABS = parseInt(process.argv[3] || '20', 10);

function rssKb(p) {
  const status = fs.readFileSync(`/proc/${p}/status`, 'utf8');
  return parseInt(status.match(/VmRSS:\s+(\d+) kB/)[1], 10);
}

(async () => {
  const browser = await chromium.launch();
  // warm-up: one tab so JIT/caches are paid before the baseline sample
  const warm = await browser.newContext();
  const wp = await warm.newPage();
  await wp.goto(cfg.local + '/guild', { waitUntil: 'load' });
  await wp.waitForTimeout(3000);
  await warm.close();
  await new Promise(r => setTimeout(r, 2000));

  const before = rssKb(pid);
  const contexts = [];
  for (let i = 0; i < TABS; i++) {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.goto(cfg.local + (i % 2 === 0 ? '/guild' : '/events'), { waitUntil: 'load' });
    contexts.push(ctx);
  }
  await new Promise(r => setTimeout(r, 5000)); // let all circuits settle
  const after = rssKb(pid);
  for (const c of contexts) await c.close();
  await browser.close();

  const result = {
    date: new Date().toISOString(), pid, tabs: TABS,
    rssBeforeKb: before, rssAfterKb: after,
    perCircuitKb: Math.round((after - before) / TABS),
    note: 'RSS process serveur ; inclut buffers SignalR + arbre de rendu par circuit. Ordre de grandeur, pas une mesure GC précise.',
  };
  process.stdout.write(JSON.stringify(result, null, 2));
})();
