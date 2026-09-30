// Service-Worker für Offline-Splash und schnellere Starts.
// Echter Offline-Sync für den Mobile-Picker (PUT-Queue, Background-Sync) ist
// Folge-Iteration.
//
// Strategien:
//  - /api/*                 : nie gecacht (Network-only), egal welche Methode.
//  - Nicht-GET / Cross-Origin: wird nicht angefasst.
//  - Navigation / index.html: network-first, Cache nur als Offline-/Fehler-Fallback.
//  - Assets (gehashte Bundles, Icons, Manifest): stale-while-revalidate.
//  - Gecacht werden ausschließlich 200-Antworten (nie 4xx/5xx, keine
//    SPA-Fallback-HTML-Seite für ein fehlendes JS/CSS).
//
// Cache-Versionierung: VERSION manuell hochzählen, wenn sich Strategie oder
// Cache-Inhalt ändern. BUILD_ID setzt `vite build` (vite.config.ts) pro Build
// auf einen Zeitstempel → jedes Deployment liefert einen byte-verschiedenen
// sw.js, der Browser installiert ihn, activate räumt die alten Caches ab und
// die Seite zeigt "Neue Version verfügbar".
const VERSION = 'v3'
const BUILD_ID = '__BUILD_ID__'
const CACHE_PREFIX = 'lager-shell-'
const CACHE = `${CACHE_PREFIX}${VERSION}-${BUILD_ID}`

// Schlüssel der App-Shell im Cache (Offline-Fallback für alle Navigationen).
const SHELL_KEY = '/index.html'
// Best-effort beim Install vorladen; schlägt eine Datei fehl, bricht der Install NICHT ab.
// url = was geladen wird, key = unter welchem Schlüssel es im Cache liegt.
const PRECACHE = [
  { url: '/', key: SHELL_KEY },
  { url: '/favicon.svg', key: '/favicon.svg' },
  { url: '/icon.svg', key: '/icon.svg' },
  { url: '/manifest.webmanifest', key: '/manifest.webmanifest' },
]

self.addEventListener('install', (e) => {
  e.waitUntil(
    caches.open(CACHE).then((cache) =>
      Promise.allSettled(
        PRECACHE.map(async ({ url, key }) => {
          // cache: 'reload' umgeht den HTTP-Cache → frische Dateien.
          const res = await fetch(new Request(url, { cache: 'reload' }))
          if (isCacheable(res)) await cache.put(key, res)
        })
      )
    )
  )
  self.skipWaiting()
})

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  )
})

self.addEventListener('fetch', (e) => {
  const req = e.request
  const url = new URL(req.url)

  // Fremde Origins nicht anfassen.
  if (url.origin !== self.location.origin) return

  // API: nie cachen. Bei Netzfehler kurze Offline-Antwort statt Browser-Fehlerseite.
  if (url.pathname.startsWith('/api/')) {
    e.respondWith(fetch(req).catch(() => new Response('offline', { status: 503 })))
    return
  }

  // POST/PUT/DELETE & Co. laufen unverändert am Service-Worker vorbei.
  if (req.method !== 'GET') return

  if (req.mode === 'navigate' || url.pathname === SHELL_KEY) {
    e.respondWith(networkFirstShell(req, url))
    return
  }

  e.respondWith(staleWhileRevalidate(e, req))
})

// Nur vollständige, gleich-originige, nicht umgeleitete 200-Antworten cachen.
function isCacheable(res) {
  return res.status === 200 && res.type === 'basic' && !res.redirected
}

// Für Assets zusätzlich: eine HTML-Antwort ist hier ein SPA-Fallback für eine
// fehlende Datei (z. B. altes Lazy-Chunk) und darf nie als JS/CSS gecacht werden.
function isCacheableAsset(res) {
  if (!isCacheable(res)) return false
  return !(res.headers.get('content-type') || '').toLowerCase().startsWith('text/html')
}

async function networkFirstShell(req, url) {
  const cache = await caches.open(CACHE)
  try {
    const res = await fetch(req)
    // Nur die Startseite als Shell merken — Navigationen zu anderen Pfaden
    // (z. B. /swagger) dürfen den Offline-Fallback nicht überschreiben.
    if ((url.pathname === '/' || url.pathname === SHELL_KEY) && isCacheable(res)) {
      await cache.put(SHELL_KEY, res.clone())
    }
    if (res.status >= 500) {
      // Server/Proxy gerade nicht gesund (z. B. Deployment läuft) → gecachte Shell, falls vorhanden.
      const cached = await cache.match(SHELL_KEY)
      if (cached) return cached
    }
    return res
  } catch {
    const cached = await cache.match(SHELL_KEY)
    return cached ?? new Response('offline', { status: 503, headers: { 'Content-Type': 'text/plain; charset=utf-8' } })
  }
}

async function staleWhileRevalidate(e, req) {
  const cache = await caches.open(CACHE)
  const cached = await cache.match(req)
  const refresh = fetch(req).then(async (res) => {
    if (isCacheableAsset(res)) await cache.put(req, res.clone())
    return res
  })
  if (cached) {
    // Antwort sofort aus dem Cache, Aktualisierung im Hintergrund.
    e.waitUntil(refresh.catch(() => {}))
    return cached
  }
  // Kein Fallback auf index.html: für JS/CSS wäre das der falsche MIME-Typ.
  return refresh
}
