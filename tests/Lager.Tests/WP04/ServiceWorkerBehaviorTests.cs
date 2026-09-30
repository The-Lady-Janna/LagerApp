namespace Lager.Tests.WP04;

/// <summary>
/// Verhalten von frontend/lager-ui/public/sw.js. Das Skript läuft in Node in einer nachgebauten
/// Service-Worker-Umgebung (Cache-API, fetch, Lifecycle-Events) — geprüft werden die Cache-Regeln:
/// Navigation network-first, Assets stale-while-revalidate, nur 200-Antworten, nie /api oder Nicht-GET,
/// alte Caches werden abgeräumt. Ohne passendes Node (>= 22.18) werden die Tests als übersprungen gemeldet.
/// </summary>
public class ServiceWorkerBehaviorTests
{
    private static System.Text.Json.JsonElement Run()
    {
        var sw = Path.Combine(FrontendFixture.UiDir, "public", "sw.js");
        return FrontendFixture.RunNode(Harness, sw).RootElement.Clone();
    }

    private static (int Status, string Body) Ok(System.Text.Json.JsonElement r)
    {
        Assert.True(r.GetProperty("intercepted").GetBoolean());
        Assert.False(r.TryGetProperty("error", out var err), $"unerwarteter Fehler: {err}");
        return (r.GetProperty("status").GetInt32(), r.GetProperty("body").GetString()!);
    }

    private static void AssertNetworkError(System.Text.Json.JsonElement r) =>
        Assert.True(r.TryGetProperty("error", out _), "Erwartet: Netzwerkfehler (kein Fallback auf index.html für Assets).");

    [NodeFact]
    public void Install_ist_best_effort_und_Activate_raeumt_alte_Caches_ab()
    {
        var r = Run();

        Assert.True(r.GetProperty("installSkipWaiting").GetBoolean());
        // Das Manifest antwortet mit 500: nicht gecacht, der Install bricht trotzdem nicht ab.
        Assert.Equal(new[] { "/favicon.svg", "/index.html" }, r.GetProperty("installCache").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.True(r.GetProperty("cacheNameHasVersion").GetBoolean());
        Assert.True(r.GetProperty("activateOldGone").GetBoolean());   // lager-shell-v1 und fremde Caches sind weg
        Assert.Equal(1, r.GetProperty("activateKeys").GetInt32());
        Assert.True(r.GetProperty("activateClaim").GetBoolean());
    }

    [NodeFact]
    public void Navigation_ist_network_first_mit_Cache_nur_als_Fehler_Fallback()
    {
        var r = Run();

        Assert.Equal((200, "INDEX-V2"), Ok(r.GetProperty("navRoot")));      // frisch vom Netz, nicht der Precache (V1)
        Assert.Equal((200, "INDEX-DEEP"), Ok(r.GetProperty("navDeep")));    // Deep-Link: ebenfalls frisch
        Assert.Equal((200, "SWAGGER"), Ok(r.GetProperty("navSwagger")));
        Assert.Equal((200, "INDEX-V2"), Ok(r.GetProperty("navOffline")));   // offline: Shell der Startseite, nicht Swagger/Deep-Link
        Assert.Equal((200, "INDEX-V2"), Ok(r.GetProperty("nav503")));       // Server/Proxy krank (Deployment): Shell aus dem Cache
        Assert.Equal((404, "weg"), Ok(r.GetProperty("nav404")));            // 404 wird durchgereicht und nie gecacht
        Assert.Single(r.GetProperty("shellKeys").EnumerateArray());
    }

    [NodeFact]
    public void Assets_sind_stale_while_revalidate_und_es_werden_nur_echte_200_Antworten_gecacht()
    {
        var r = Run();

        Assert.Equal((200, "JS1"), Ok(r.GetProperty("assetMiss")));
        Assert.Equal((200, "JS1"), Ok(r.GetProperty("assetStale")));   // sofort aus dem Cache ...
        Assert.Equal((200, "JS2"), Ok(r.GetProperty("assetFresh")));   // ... und im Hintergrund aktualisiert

        Assert.Equal(404, Ok(r.GetProperty("asset404")).Status);
        AssertNetworkError(r.GetProperty("asset404Offline"));           // 404 nie gecacht

        // Fehlendes Chunk + SPA-Fallback: HTML darf nie als JS/CSS gecacht werden.
        Assert.Equal((200, "INDEX-HTML"), Ok(r.GetProperty("assetHtmlFallback")));
        AssertNetworkError(r.GetProperty("assetHtmlOffline"));
        AssertNetworkError(r.GetProperty("assetOfflineNoCache"));       // kein index.html als Ersatz für ein JS-Modul
    }

    [NodeFact]
    public void API_und_Nicht_GET_Requests_werden_nie_gecacht()
    {
        var r = Run();

        Assert.Equal((200, "[]"), Ok(r.GetProperty("apiGet")));
        Assert.Equal((200, "[]"), Ok(r.GetProperty("apiPost")));
        Assert.Equal((503, "offline"), Ok(r.GetProperty("apiOffline")));
        Assert.False(r.GetProperty("postOther").GetProperty("intercepted").GetBoolean());
        Assert.False(r.GetProperty("crossOrigin").GetProperty("intercepted").GetBoolean());
        Assert.Empty(r.GetProperty("cachedApi").EnumerateArray());
        Assert.Equal(new[] { "/assets/app-abc.js", "/favicon.svg", "/index.html" }, r.GetProperty("allCached").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // Node-Skript: process.argv[2] = Pfad zu sw.js.
    private const string Harness = """
import { readFileSync } from 'node:fs'

// Führt public/sw.js in einer nachgebauten Service-Worker-Umgebung aus (Cache-API, fetch, Events).
const swPath = process.argv[2]
const origin = 'https://lager.test'
const src = readFileSync(swPath, 'utf8')

const listeners = {}
let skipped = false
let claimed = false
const self = {
  location: { origin },
  addEventListener: (type, fn) => { listeners[type] = fn },
  skipWaiting() { skipped = true },
  clients: { claim() { claimed = true; return Promise.resolve() } },
}

const keyOf = (req) => (typeof req === 'string' ? new URL(req, origin).href : req.url)
class FakeCache {
  constructor() { this.map = new Map() }
  async match(req) { const r = this.map.get(keyOf(req)); return r ? r.clone() : undefined }
  async put(req, res) {
    if (res.status !== 200) throw new TypeError('Cache.put: nur 200 erlaubt')
    this.map.set(keyOf(req), res)
  }
}
const store = new Map()
const caches = {
  async open(name) { if (!store.has(name)) store.set(name, new FakeCache()); return store.get(name) },
  async keys() { return [...store.keys()] },
  async delete(name) { return store.delete(name) },
}

// Netzwerk-Attrappe
const net = { online: true, routes: new Map(), log: [] }
const respond = ({ status = 200, body = '', type = 'text/plain' }) => {
  const res = new Response(body, { status, headers: { 'content-type': type } })
  Object.defineProperty(res, 'type', { value: 'basic' })
  return res
}
const fakeFetch = async (req) => {
  const url = new URL(typeof req === 'string' ? req : req.url, origin)
  net.log.push(url.pathname)
  if (!net.online) throw new TypeError('Failed to fetch')
  const r = net.routes.get(url.pathname)
  return respond(r ?? { status: 404, body: 'nicht gefunden' })
}
class FakeRequest { constructor(url, init = {}) { this.url = new URL(url, origin).href; this.method = init.method ?? 'GET'; this.mode = init.mode ?? 'cors'; this.cache = init.cache } }

new Function('self', 'caches', 'fetch', 'Request', src)(self, caches, fakeFetch, FakeRequest)

const waits = []
const dispatchLifecycle = async (type) => {
  const pending = []
  listeners[type]({ waitUntil: (p) => pending.push(p) })
  await Promise.all(pending)
}
const request = async (url, { method = 'GET', mode = 'cors' } = {}) => {
  const ev = { request: { url: new URL(url, origin).href, method, mode }, responded: false, respondWith(p) { this.responded = true; this.p = p }, waitUntil(p) { waits.push(p) } }
  listeners.fetch(ev)
  if (!ev.responded) return { intercepted: false }
  let res
  try { res = await ev.p } catch (e) { await Promise.all(waits.splice(0)); return { intercepted: true, error: String(e) } }
  await Promise.all(waits.splice(0))
  return { intercepted: true, status: res.status, body: await res.clone().text() }
}
const cacheKeys = () => {
  const out = []
  for (const c of store.values()) for (const k of c.map.keys()) out.push(new URL(k).pathname)
  return out.sort()
}
const currentName = () => [...store.keys()].find((k) => k.startsWith('lager-shell-v'))

const out = {}

// 1) Install: Precache best-effort; ein 500 auf das Manifest bricht den Install nicht ab
net.routes.set('/', { body: 'INDEX-V1', type: 'text/html' })
net.routes.set('/favicon.svg', { body: '<svg/>', type: 'image/svg+xml' })
net.routes.set('/manifest.webmanifest', { status: 500, body: 'boom' })
await dispatchLifecycle('install')
out.installSkipWaiting = skipped
out.installCache = cacheKeys()

// 2) Activate räumt alte Caches ab
store.set('lager-shell-v1', new FakeCache())
store.set('fremd', new FakeCache())
await dispatchLifecycle('activate')
out.activateKeys = [...store.keys()].length
out.activateOldGone = !store.has('lager-shell-v1') && !store.has('fremd')
out.activateClaim = claimed
out.cacheNameHasVersion = /^lager-shell-v\d+-/.test(currentName())

// 3) Navigation: network-first
net.routes.set('/', { body: 'INDEX-V2', type: 'text/html' })
out.navRoot = await request('/', { mode: 'navigate' })
net.routes.set('/orders/1', { body: 'INDEX-DEEP', type: 'text/html' })
out.navDeep = await request('/orders/1', { mode: 'navigate' })
net.routes.set('/swagger', { body: 'SWAGGER', type: 'text/html' })
out.navSwagger = await request('/swagger', { mode: 'navigate' })
net.online = false
out.navOffline = await request('/orders/1', { mode: 'navigate' })
net.online = true
net.routes.set('/orders/2', { status: 503, body: 'bad gateway', type: 'text/html' })
out.nav503 = await request('/orders/2', { mode: 'navigate' })
net.routes.set('/orders/3', { status: 404, body: 'weg', type: 'text/html' })
out.nav404 = await request('/orders/3', { mode: 'navigate' })
out.shellKeys = cacheKeys().filter((k) => k === '/index.html')

// 4) Assets: stale-while-revalidate, nur 200
net.routes.set('/assets/app-abc.js', { body: 'JS1', type: 'text/javascript' })
out.assetMiss = await request('/assets/app-abc.js')
net.routes.set('/assets/app-abc.js', { body: 'JS2', type: 'text/javascript' })
out.assetStale = await request('/assets/app-abc.js')
out.assetFresh = await request('/assets/app-abc.js')
net.routes.delete('/assets/gone.js')
out.asset404 = await request('/assets/gone.js')
net.online = false
out.asset404Offline = await request('/assets/gone.js')
net.online = true
net.routes.set('/assets/spa.js', { status: 200, body: 'INDEX-HTML', type: 'text/html; charset=utf-8' })
out.assetHtmlFallback = await request('/assets/spa.js')
net.online = false
out.assetHtmlOffline = await request('/assets/spa.js')
out.assetOfflineNoCache = await request('/assets/nie-gesehen.js')
net.online = true

// 5) API und Nicht-GET
net.routes.set('/api/articles', { body: '[]', type: 'application/json' })
out.apiGet = await request('/api/articles')
out.apiPost = await request('/api/articles', { method: 'POST' })
net.online = false
out.apiOffline = await request('/api/articles')
net.online = true
out.postOther = await request('/irgendwas', { method: 'POST' })
out.crossOrigin = await request('https://fremd.example/x.js')
out.cachedApi = cacheKeys().filter((k) => k.startsWith('/api'))
out.allCached = cacheKeys()

console.log(JSON.stringify(out))
""";
}
