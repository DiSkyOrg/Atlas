#!/usr/bin/env node
// Network-level audit (curl-based): timings, compression, cache headers,
// conditional requests, security headers. Run against PROD by default.
//
//   node net-audit.js [prod|local] > out/net.json
//
// Caveats in this sandbox (recorded in the output):
//  - all outbound HTTPS goes through a local CONNECT proxy: DNS/TCP-connect
//    timings measure the local proxy, not the origin. TLS handshake and TTFB
//    are end-to-end and meaningful (median/p95 across iterations).
//  - the sandbox curl has no HTTP/3 support, and QUIC cannot traverse an HTTP
//    CONNECT proxy anyway: "negotiated HTTP/3" is therefore null.

const { execFileSync } = require('node:child_process');
const cfg = require('./pages.json');

const target = process.argv[2] || 'prod';
const BASE = cfg[target];
const ITERATIONS = 10;

const CURL_FMT = JSON.stringify({
  dns: '%{time_namelookup}', connect: '%{time_connect}', appconnect: '%{time_appconnect}',
  ttfb: '%{time_starttransfer}', total: '%{time_total}', code: '%{http_code}',
  httpVersion: '%{http_version}', sizeDownload: '%{size_download}',
}).replace(/"(%\{[^}]+\})"/g, '$1');

function curl(args) {
  return execFileSync('curl', ['-sS', '--max-time', '60', ...args], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function timeOnce(url) {
  const out = curl(['-o', '/dev/null', '-w', CURL_FMT, url]);
  const t = JSON.parse(out);
  for (const k of ['dns', 'connect', 'appconnect', 'ttfb', 'total']) t[k] = Math.round(parseFloat(t[k]) * 1000);
  return t;
}

const ms = (v) => v == null ? null : Math.round(v);
function stats(values) {
  const s = [...values].sort((a, b) => a - b);
  const q = (p) => s[Math.min(s.length - 1, Math.ceil(p * s.length) - 1)];
  return { median: ms(q(0.5)), p95: ms(q(0.95)), min: ms(s[0]), max: ms(s[s.length - 1]) };
}

function headers(url, extraArgs = []) {
  const raw = curl(['-o', '/dev/null', '-D', '-', ...extraArgs, url]);
  const lines = raw.split(/\r?\n/);
  const h = {};
  let status = null;
  for (const line of lines) {
    const m = line.match(/^HTTP\/[\d.]+ (\d+)/);
    if (m) { status = parseInt(m[1], 10); continue; }
    const kv = line.match(/^([\w-]+):\s*(.*)$/);
    if (kv) h[kv[1].toLowerCase()] = kv[2];
  }
  return { status, headers: h };
}

function downloadSize(url, encoding) {
  const size = parseInt(curl(['-o', '/dev/null', '-H', `Accept-Encoding: ${encoding}`, '-w', '%{size_download}', url]), 10);
  const enc = headers(url, ['-H', `Accept-Encoding: ${encoding}`]).headers['content-encoding'] || 'identity';
  return { size, encoding: enc };
}

function discoverAssets(html) {
  const urls = new Set();
  for (const m of html.matchAll(/(?:href|src)="([^"]+\.(?:css|js|woff2?|json))(?:[?#][^"]*)?"/g)) {
    const u = m[1];
    if (u.startsWith('http') && !u.startsWith(BASE)) continue; // external
    urls.add(u.startsWith('http') ? u : BASE + (u.startsWith('/') ? u : '/' + u));
  }
  return [...urls];
}

const result = {
  target, base: BASE, date: new Date().toISOString(), iterations: ITERATIONS,
  caveats: [
    'Sandbox: trafic via proxy CONNECT local. dns/connect mesurent le proxy local, pas l’origine (marqués proxyAffected).',
    'TLS (appconnect-connect) et TTFB sont bout-en-bout et fiables en relatif.',
    'HTTP/3 : négociation non testable (curl sans QUIC + proxy CONNECT) -> null.',
  ],
  pages: {}, compression: [], cache: [], conditionalRequests: [], securityHeaders: null, http3: null,
};

// ── timings per page ──
for (const p of cfg.pages) {
  const url = BASE + p.path;
  const runs = [];
  for (let i = 0; i < ITERATIONS; i++) runs.push(timeOnce(url));
  const pick = (k) => stats(runs.map(r => r[k]));
  const tls = runs.map(r => r.appconnect > 0 ? r.appconnect - r.connect : 0);
  const appTtfb = runs.map(r => r.ttfb - Math.max(r.appconnect, r.connect));
  result.pages[p.id] = {
    path: p.path, status: runs[0].code, httpVersion: runs[0].httpVersion, sizeDownload: runs[0].sizeDownload,
    dnsMs: { ...pick('dns'), proxyAffected: true },
    connectMs: { ...pick('connect'), proxyAffected: true },
    tlsHandshakeMs: stats(tls),
    ttfbTotalMs: pick('ttfb'),
    appTtfbMs: stats(appTtfb), // TTFB minus connection setup = server think time + 1 RTT
    totalMs: pick('total'),
  };
}

// ── compression + cache per resource ──
const homeHtml = curl(['-H', 'Accept-Encoding: identity', BASE + '/']);
const assets = discoverAssets(homeHtml).slice(0, 12);
const resources = [{ url: BASE + '/', kind: 'html' }, { url: BASE + '/docs/contributing/style-guide', kind: 'html' },
  ...assets.map(u => ({ url: u, kind: u.match(/\.(\w+)(?:$|\?)/)?.[1] || '?' }))];

for (const r of resources) {
  const identity = downloadSize(r.url, 'identity');
  const br = downloadSize(r.url, 'br');
  const gzip = downloadSize(r.url, 'gzip');
  const h = headers(r.url, ['-H', 'Accept-Encoding: br, gzip']);
  result.compression.push({
    url: r.url.replace(BASE, ''), kind: r.kind, contentType: h.headers['content-type'] || null,
    identityBytes: identity.size,
    brotli: br.encoding === 'br' ? { bytes: br.size, ratio: +(br.size / identity.size).toFixed(3) } : null,
    gzip: gzip.encoding === 'gzip' ? { bytes: gzip.size, ratio: +(gzip.size / identity.size).toFixed(3) } : null,
    servedEncoding: h.headers['content-encoding'] || 'identity',
  });
  result.cache.push({
    url: r.url.replace(BASE, ''), kind: r.kind, status: h.status,
    cacheControl: h.headers['cache-control'] || null, etag: h.headers['etag'] || null,
    lastModified: h.headers['last-modified'] || null, expires: h.headers['expires'] || null,
    vary: h.headers['vary'] || null, age: h.headers['age'] || null,
  });
  // conditional request check — same Accept-Encoding as the original response,
  // because the ETag varies per representation (encoding)
  const ae = ['-H', 'Accept-Encoding: br, gzip'];
  let cond = { url: r.url.replace(BASE, ''), ifNoneMatch: null, ifModifiedSince: null };
  if (h.headers['etag']) cond.ifNoneMatch = headers(r.url, [...ae, '-H', `If-None-Match: ${h.headers['etag']}`]).status;
  if (h.headers['last-modified']) cond.ifModifiedSince = headers(r.url, [...ae, '-H', `If-Modified-Since: ${h.headers['last-modified']}`]).status;
  result.conditionalRequests.push(cond);
}

// ── security headers + alt-svc on / ──
const home = headers(BASE + '/');
const sec = (k) => home.headers[k] || null;
result.securityHeaders = {
  strictTransportSecurity: sec('strict-transport-security'),
  xContentTypeOptions: sec('x-content-type-options'),
  referrerPolicy: sec('referrer-policy'),
  xFrameOptions: sec('x-frame-options'),
  contentSecurityPolicy: sec('content-security-policy'),
  permissionsPolicy: sec('permissions-policy'),
  server: sec('server'),
};
result.http3 = {
  altSvc: sec('alt-svc'),
  negotiated: null,
  negotiatedNullReason: 'curl sandbox sans support QUIC + proxy CONNECT HTTP (UDP non tunnelable)',
};
result.negotiatedHttpVersion = result.pages.home.httpVersion;

process.stdout.write(JSON.stringify(result, null, 2));
