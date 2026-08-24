#!/usr/bin/env node
// Merge the individual audit outputs into a single baseline.json.
//   node merge-baseline.js out/ > baseline.json
const fs = require('node:fs');
const path = require('node:path');
const dir = process.argv[2] || 'out';

function load(name) {
  const p = path.join(dir, name);
  if (!fs.existsSync(p) || fs.statSync(p).size === 0) return null;
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return null; }
}

const baseline = {
  generated: new Date().toISOString(),
  methodology: {
    network: 'curl, 10 itérations/page, médiane+p95. Sandbox derrière proxy CONNECT: dns/connect non significatifs, TLS+TTFB bout-en-bout.',
    browser: 'Playwright + Chromium headless, lib web-vitals injectée (LCP/CLS/INP/TTFB/FCP), axe-core 4.x (wcag2a, wcag2aa, wcag21aa, best-practice), throttling CDP (Fast 3G: 1.6Mbps/150ms, Slow 4G: 9Mbps/60ms).',
    http3: 'Négociation réelle non testable depuis la sandbox (curl sans QUIC, proxy CONNECT HTTP) -> null. Seule la présence d’alt-svc est observée.',
    circuitRam: 'RSS du process serveur local avant/après ouverture de N onglets interactifs.',
  },
  prod: { network: load('net-prod.json'), browser: load('browser-prod.json') },
  local: { network: load('net-local.json'), browser: load('browser-local.json'), circuitRam: load('circuit-ram.json') },
};

process.stdout.write(JSON.stringify(baseline, null, 2));
