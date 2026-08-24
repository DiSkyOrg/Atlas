# Audit UX invisible — DiSky Atlas

**Date** : 2026-08-24 · **Branche** : `claude/blazor-ux-audit-6ir5m7`
**Cibles** : PROD `https://atlas.disky.me` (origine directe, nginx 1.22.1 → Kestrel, sans CDN) · LOCAL (publish Release .NET 10, `ASPNETCORE_ENVIRONMENT=Production`, Kestrel direct)
**Harnais** : `tools/uxaudit/` (curl ×10/page médiane+p95, Playwright+Chromium, web-vitals, axe-core wcag2a/aa/21aa/best-practice, throttling CDP, RSS/circuit). Données brutes : `baseline.json`.

## 0. Limites de mesure (sandbox) — champs `null`, jamais estimés

La sandbox route tout le trafic sortant par un proxy CONNECT qui **re-termine TLS** et **ne tunnelle pas les WebSockets**, et son Chromium est bloqué par empreinte TLS au relais (contourné en re-émettant les requêtes navigateur via la pile Node). Conséquences :

| Mesure | Statut |
|---|---|
| TTFB applicatif PROD (médiane/p95, comparatif) | ✅ fiable |
| Compression, en-têtes, 304, corps de réponse PROD | ✅ fiable |
| Handshake TLS PROD | ⚠️ mesuré jusqu'au relais, pas ton serveur |
| **ALPN h2/h3 réellement négocié par ton nginx** | ❌ `null` — à vérifier depuis un vrai client (§7) |
| WebSocket/SignalR PROD à travers nginx | ❌ `null` — mesuré en LOCAL (Kestrel direct) |
| Vitals throttlés PROD | ❌ `null` — mesurés en LOCAL |

## 1. Baseline (avant corrections)

### Réseau PROD (curl, 10 itérations, ms)

| Page | TTFB total méd/p95 | TTFB applicatif méd/p95 | Total méd | Taille HTML |
|---|---|---|---|---|
| `/` | 388 / 577 | **123 / 128** | 590 | 75 Ko |
| `/events` | 464 / 620 | **133 / 354** | 860 | **413 Ko** |
| `/guild` | 364 / 405 | 124 / 130 | 629 | 124 Ko |
| `/docs/contributing/style-guide` | 364 / 394 | 123 / 137 | 539 | 50 Ko |
| 404 | 368 / 746 | 120 / 128 | 546 | 53 Ko |

Le TTFB applicatif (~125 ms, dont ~1 RTT réseau) est sain : côté serveur l'app répond en < 25 ms (LOCAL : 4–24 ms méd). **La latence perçue vient du transport, pas de l'app** — d'où l'importance de HTTP/2 et du handshake (§7).

### Compression / cache PROD

- HTML : gzip via nginx, ratios 0,20–0,30 (75 Ko → 15 Ko). **Pas de brotli sur le HTML**, pas de `Vary: Accept-Encoding` (`gzip_vary off`).
- Assets fingerprintés .NET : **brotli pré-compressé par Kestrel** (blazor.web.js 200 Ko → 47,5 Ko), `immutable, max-age=1an`, 304 OK (ETag et Last-Modified). Aucun doublon de compression app/nginx. ✅
- **Mais** 6 fichiers maison (`css/theme|tokens|ds-components|site.css`, `js/atlas.js`) étaient servis **non fingerprintés en `no-cache`** → une revalidation par fichier à chaque visite.
- HTML : `no-cache, no-store` ✅ (correct pour Blazor Server : antiforgery + état par circuit).

### Sécurité / protocoles PROD

- ❌ `Strict-Transport-Security` absent (le code appelle `UseHsts()` mais Kestrel voyait du HTTP : pas de ForwardedHeaders), ❌ `X-Content-Type-Options`, ❌ `Referrer-Policy`. ✅ `X-Frame-Options: SAMEORIGIN` + `frame-ancestors 'self'`.
- ❌ Pas d'`alt-svc` → HTTP/3 non proposé (nginx 1.22 ne le supporte pas).
- ❌ `robots.txt` et sitemap inexistants ; **soft-404** : toute URL inconnue répondait `200` + ~50 Ko de HTML (`/{EntityId}` attrape tout).
- ❌ Aucun rate limiting applicatif ni (a priori) proxy — plus de Cloudflare devant.

### Core Web Vitals

- PROD (via relais, non throttlé) : LCP 1,49–1,70 s, TTFB 480–608 ms, **CLS 0–0,047**.
- LOCAL avant correctif : **LCP 12,6 s** — artefact sandbox (Google Fonts injoignable → l'`@import` bloque le premier rendu jusqu'au timeout). C'est un artefact, mais il matérialise le vrai risque : **le CSS de JetBrains Mono était une dépendance tierce bloquante du rendu** (adblock, panne ou lenteur Google = page blanche prolongée), la pire ressource possible sans CDN.

### SignalR (LOCAL, Kestrel direct)

- Transport **WebSocket** (`ws://…/_blazor`), pas de long-polling. Protocole hub : **BlazorPack** (MessagePack binaire) — la reco « MessagePack plutôt que JSON » est déjà satisfaite nativement, rien à faire.
- Clic → première mutation DOM : **4 ms médiane** (5 essais).
- Coupure réseau 4 s : reprise **504 ms** après retour réseau, **état préservé** (texte du champ intact). L'overlay ne s'affiche pas pour une coupure aussi brève (détection par ping) : comportement normal.
- ⚠️ Non vérifiable d'ici : le même transport **à travers ton nginx** (§7).

### Accessibilité (axe-core, total des 5 pages : 618 nœuds)

| Règle | Impact | Nœuds | Cause |
|---|---|---|---|
| `svg-img-alt` | serious | **524** | `LucideIcon` rend `role="img"` sans nom accessible |
| `color-contrast` | serious | 81 | textes atténués (placeholders, labels de groupe, compteurs) |
| `label` | critical | 6 | cases à cocher des task-lists markdown |
| `aria-valid-attr-value` | critical | 5 | onglets Blueprint : `aria-controls` vers un id non rendu (bug lib) |
| `landmark-unique` / `page-has-heading-one` | moderate | 2 | 2 `<nav>` sans label distinct ; 404 sans `h1` |

Clavier : 24–25 arrêts, **focus toujours visible** ✅, ordre cohérent ✅, mais **pas de skip link** ❌ et **bouton « copier » invisible au focus clavier** (`opacity:0` hors survol souris) ❌. Navigation SPA : `FocusOnNavigate` fonctionne (focus sur le `h1`, annoncé par les lecteurs d'écran) ✅ ; recherche sans annonce du nombre de résultats ❌ et **sans debounce** (1 frappe = 1 aller-retour serveur + re-rendu de 60 lignes) ❌. `prefers-reduced-motion` ignoré ❌.

### Dégradation sans JavaScript

**Prerendering actif** ✅ : les 5 pages servent leur contenu complet sans JS (accueil 2 799 car. de texte, doc 6 846, liens réels). Les crawlers voient tout. Pas de refetch après hydratation (données singleton en mémoire) → `PersistentComponentState` sans objet ; aucune I/O par page → `[StreamRendering]` sans objet.

### Render modes (inventaire exhaustif)

`grep` exhaustif : **2 seuls** `@rendermode` dans le repo — `<Routes>` et `<HeadOutlet>` dans `App.razor`, en `InteractiveServer` **global** (confirmé). Aucune page n'a de mode propre.

| Route | Mode (hérité) | Interactif réel sur la page |
|---|---|---|
| `/` | InteractiveServer | rien en propre (layout seulement) |
| `/{entity}` | InteractiveServer | FilterBar, dialog subtypes, copy |
| `/events`, `/core/{kind}` | InteractiveServer | FilterBar, copy |
| `/docs[/{slug}]` | InteractiveServer | toggles, tabs, TOC, copy |
| `/not-found`, `/Error` | InteractiveServer | rien |
| Layout (toutes) | — | palette ⌘K, theme, sidebar mobile, settings |

**RAM mesurée : 3,6 Mo (avant) / 4,4 Mo (après) de RSS par circuit** (20 onglets /guild + /events ; ordre de grandeur, bruit GC inclus — retenir « ~4 Mo »). 100 lecteurs simultanés ≈ **400 Mo** + circuits déconnectés retenus (défaut : 100 × 3 min). Chaque visiteur, crawler JS compris, ouvre un WebSocket sur ton serveur.

**Recommandation** (non appliquée — c'est le changement le plus risqué et tu as demandé une validation page par page) : le vrai gain serait `/not-found` + les pages de lecture en SSR statique avec **îlots** interactifs (palette, theme, filtres). Mais l'interactivité est dans le **layout** (palette/sidebar/theme) : il faudrait d'abord extraire ces composants en îlots `@rendermode InteractiveServer` individuels, puis retirer le mode global. Estimation : ~1–2 jours de refonte + tests, gain ≈ 4 Mo et 1 WebSocket par lecteur passif. À faire seulement si la charge le justifie ; la config actuelle tient largement pour une doc.

## 2. Diagnostic priorisé (impact × coût)

| # | Problème | Impact | Coût | Statut |
|---|---|---|---|---|
| 1 | Police tierce bloquante (Google Fonts `@import`) | Élevé | Faible | ✅ corrigé |
| 2 | HSTS/nosniff/Referrer-Policy absents + scheme non forwardé | Élevé | Faible | ✅ corrigé |
| 3 | Soft-404 + ni robots.txt ni sitemap | Élevé (SEO) | Faible | ✅ corrigé |
| 4 | 524 icônes sans nom accessible + copy invisible au clavier + pas de skip link | Élevé (a11y) | Faible | ✅ corrigé |
| 5 | CSS/JS maison non fingerprintés (`no-cache`) | Moyen | Faible | ✅ corrigé |
| 6 | HTTP/2 incertain / HTTP/3 absent côté nginx | Élevé | Moyen (infra) | 📋 diff proposé |
| 7 | Recherche sans debounce ni annonce | Moyen | Faible | ✅ corrigé |
| 8 | Outfit en TTF (110 Ko) | Moyen | Faible | ✅ corrigé (woff2 45 Ko) |
| 9 | Polices en `no-cache` (non fingerprintées) | Moyen | Faible | ✅ corrigé (7 j) |
| 10 | Circuits déconnectés : défauts 100×3 min | Faible-Moyen | Faible | ✅ corrigé (40×2 min) |
| 11 | Rate limiting inexistant | Moyen (robustesse) | Moyen | 📋 proposé, **pas appliqué** (ton accord requis) |
| 12 | Contraste des textes atténués (81 nœuds) | Moyen (a11y) | — | 📋 **non corrigé** : changerait le design ; liste dans `baseline.json` |
| 13 | `aria-controls` des tabs Blueprint | Faible | — | 📋 bug de la lib (à remonter upstream) |
| 14 | `/events` = 413 Ko de HTML (7 s de LCP en Fast 3G) | Moyen | Moyen | 📋 non corrigé : pagination/virtualisation = changement de contenu |
| 15 | `blazorblueprint.css` (RCL net8) non fingerprintable | Faible | — | 📋 revalidation 304 (déjà pas cher) ; vendorer si tu veux l'immutable |

## 3. Corrections appliquées (un commit par lot)

| Commit | Lot |
|---|---|
| `4a7afef` | Harnais `tools/uxaudit/` |
| `43fa792` | **Polices** : Outfit woff2 (110→45 Ko), JetBrains Mono auto-hébergée (4 woff2, 73 Ko, latin+latin-ext variable), preload, suppression de l'origine Google ; CSS/JS via `@Assets` → fingerprint + immutable |
| `225e8f7` | **Serveur** : ForwardedHeaders (→ HSTS émis), nosniff + Referrer-Policy, cache 7 j des polices, circuits 40×2 min, robots.txt + `/sitemap.xml` (59 URLs, généré des catalogues en mémoire) |
| `cb13931` | **Vrais 404** sur `/{entity}`, `/docs/{slug}`, `/core/{kind}`, `/not-found` (HttpContext cascadé, SSR statique uniquement) |
| `5b9ae1b` | **A11y 1** : skip link, `#main-content`, copy visible au `:focus-visible` + `role=status`, debounce + aria-live de la palette, overlay reconnexion labellisé + live, `prefers-reduced-motion` (CSS + atlas.js) |
| `ae292ba` | **A11y 2** : 52 `LucideIcon` en `aria-hidden`, task-boxes labellisées, landmarks nommés, `EmptyState` en h1 sur les pages vides, logo avec width/height |
| `28d714e`, `e32876a` | Proposition nginx commentée + baseline figée |

## 4. Avant / après (LOCAL, harnais identique)

| Mesure | Avant | Après |
|---|---|---|
| Violations axe (5 pages, nœuds) | **618** (6 règles) | **86** (2 règles : 81 contraste — signalé, 5 bug lib Blueprint) |
| CLS | 0 – 0,047 | **0 partout** |
| LCP local non throttlé | 12,6 s (artefact fonts tierces) | **92 – 156 ms** |
| LCP Fast 3G (1,6 Mb/s, 150 ms RTT) | non comparable (artefact) | 0,76 s (guild/docs) · 2,5–2,7 s (home/404) · **7,1 s (/events**, 413 Ko §2.14**)** |
| Statut HTTP d'une URL inconnue (sans JS) | 200 + 50 Ko | **404** + page d'erreur rendue |
| Skip link / h1 sur 404 / img sans dimensions | ✗ / ✗ / 2 | ✓ / ✓ / 0 |
| CSS/JS maison | 6× `no-cache` (revalidation) | fingerprintés `immutable` 1 an ; polices 7 j |
| Requêtes tierces (fonts) | 2 origines Google | **0** |
| SignalR : transport / clic→DOM / reprise / état | ws / 4 ms / 504 ms / ✓ | ws / 4 ms / 504 ms / ✓ (inchangé ✅) |
| RSS par circuit | ~3,6 Mo | ~4,4 Mo (bruit GC ; ordre de grandeur ~4 Mo stable) |
| TTFB applicatif local méd | 4–24 ms | 4–24 ms (inchangé ✅) |

Le poids de `/` transféré baisse aussi : HTML 15 Ko gzip + CSS/JS en cache immutable après première visite + 118 Ko de polices en cache 7 j au lieu d'une revalidation (et ~176 Ko → ~118 Ko de polices à la première visite).

## 5. Actions manuelles restantes (ton côté)

**nginx** — diff commenté dans `tools/uxaudit/nginx-proposal.conf`, à relire puis appliquer + `nginx -t` + reload (je n'ai rien touché) :
1. `http2` sur la ligne `listen 443 ssl` (impossible de vérifier l'ALPN réel depuis la sandbox : contrôle en 30 s depuis chez toi : `curl -sI --http2 https://atlas.disky.me/ -o /dev/null -w '%{http_version}\n'` → doit afficher `2`).
2. `gzip_vary on;` (Vary absent mesuré).
3. Bloc `location /_blazor` explicite : `Upgrade`/`Connection`, `proxy_buffering off`, `proxy_read_timeout 3600s`. Vérifie ensuite dans l'onglet Réseau que `_blazor` fait bien `101 Switching Protocols`.
4. `ssl_session_cache` + OCSP stapling.
5. HTTP/3 : nécessite nginx ≥ 1.25 (paquet nginx.org) + `443/udp` ouvert — optionnel, HTTP/2 d'abord.
6. Rate limiting : zone `limit_req` proposée en commentaire — **décision à toi** (exclure `/_blazor` du limit_req, garder `limit_conn`).

**Déploiement app** : rebuild l'image Docker (les en-têtes X-Forwarded-* sont déjà transmis par ton nginx ? le correctif les lit ; s'ils manquent, ajoute les `proxy_set_header` du diff). Après déploiement, re-vérifie : `curl -sI https://atlas.disky.me/nonexistent` → 404 ; `strict-transport-security` présent.

**Design (avec ta validation, car visible)** : les 81 contrastes insuffisants (sélecteurs exacts dans `baseline.json` → `local.browser.axe`) ; pagination ou découpage de `/events` (413 Ko).

**Upstream** : bug `aria-controls` des tabs BlazorBlueprint ; advisory NU1902 sur AngleSharp 0.17.1 (dépendance transitive, modérée) — surveiller une mise à jour de Blueprint.

**Re-mesure PROD** : après déploiement + nginx, relance `node tools/uxaudit/net-audit.js prod` et compare à `baseline.json` (les gains HSTS/404/polices/fingerprint apparaîtront côté PROD ; l'ALPN, vérifie-le depuis un vrai poste).
