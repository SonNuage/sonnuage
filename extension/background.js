// Localify : toutes les requêtes vers les serveurs partent d'ici (le script de page n'y a pas accès, CORS).
// Une clé d'extension n'ouvre que : POST /imports, GET /resolve, POST /subscriptions, PUT /cookies et GET /api/extension/status.
//
// Stockage local :
//   servers         [{ id, name, url, apiKey, sendCookies }]  instances Localify
//   defaultServerId instance qui reçoit les clics sur les boutons YouTube
//   cookieHours     intervalle d'envoi automatique des cookies
//   cookieState     { [id]: { at, error } } dernier envoi par instance (séparé de servers : écrit ici, lu par les options)

const COOKIE_ALARM = "localify-cookies";
const t = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String));

class LocalifyError extends Error {
  constructor(message, status = 0, code = null) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function migrate() {
  // Version 1.0 : une seule instance (serverUrl + apiKey + cookieSync).
  const old = await chrome.storage.local.get(["serverUrl", "apiKey", "cookieSync", "servers"]);
  if (old.servers || !old.serverUrl) return;
  const id = crypto.randomUUID();
  await chrome.storage.local.set({
    servers: [{ id, name: new URL(old.serverUrl).host, url: old.serverUrl, apiKey: old.apiKey ?? "", sendCookies: !!old.cookieSync }],
    defaultServerId: id,
  });
  await chrome.storage.local.remove(["serverUrl", "apiKey", "cookieSync", "lastCookieSync", "lastCookieError"]);
}

async function settings() {
  await migrate();
  const s = await chrome.storage.local.get({ servers: [], defaultServerId: null, cookieHours: 24, cookieState: {} });
  s.servers = s.servers.filter((x) => x.url && x.apiKey);
  return s;
}

async function server(id) {
  const s = await settings();
  const found = s.servers.find((x) => x.id === (id ?? s.defaultServerId)) ?? (id ? null : s.servers[0]);
  if (!found) throw new LocalifyError(t("errNotConfigured"), 0, "notConfigured");
  return found;
}

async function call(target, method, path, body) {
  let response;
  try {
    response = await fetch(target.url + path, {
      method,
      headers: { Authorization: `ApiKey ${target.apiKey}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: "omit",
      cache: "no-store",
    });
  } catch {
    throw new LocalifyError(t("errUnreachable", target.name), 0, "unreachable");
  }
  if (response.ok) {
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }
  let detail = null;
  try {
    detail = (await response.json()).detail;
  } catch {
    // Réponse sans corps JSON (401, page Cloudflare…).
  }
  const s = response.status;
  const message =
    s === 401 ? t("errKeyRejected", target.name)
    : s === 429 ? t("errThrottled")
    : detail ? t("errServerDetail", target.name, detail)
    : s === 403 ? t("errForbidden", target.name)
    : t("errServer", target.name, s);
  throw new LocalifyError(message, s);
}

const q = (params) => new URLSearchParams(Object.entries(params).filter(([, v]) => v != null)).toString();

async function importVideo({ videoId, url, title, channel }) {
  const target = await server();
  // yt-dlp donne le titre exact, la durée et la miniature ; à défaut, ce qu'affiche la page.
  let video = { videoId, title: title || videoId, channel: channel || null, durationSeconds: null, thumbnailUrl: null, importStatus: null };
  try {
    const listing = await call(target, "GET", `/api/admin/youtube/resolve?${q({ url })}`);
    const entry = listing?.entries?.find((e) => e.videoId === videoId);
    if (entry) video = entry;
  } catch (e) {
    if (e.status === 401 || e.status === 0) throw e;
  }
  return call(target, "POST", "/api/admin/youtube/imports", { videos: [video], albumHint: null });
}

// ---------- Cookies (facultatif, désactivé par défaut, par instance) ----------

async function cookiesText() {
  if (!(await chrome.permissions.contains({ permissions: ["cookies"] }))) throw new LocalifyError(t("errCookiePermission"));
  const all = [];
  for (const domain of ["youtube.com", "google.com"]) all.push(...(await chrome.cookies.getAll({ domain })));
  if (!all.some((c) => c.domain.endsWith("youtube.com"))) throw new LocalifyError(t("errNoYoutubeCookies"));
  const lines = ["# Netscape HTTP Cookie File", "# Localify browser extension", ""];
  for (const c of all) {
    const domain = (c.httpOnly ? "#HttpOnly_" : "") + (c.hostOnly ? c.domain.replace(/^\./, "") : c.domain.startsWith(".") ? c.domain : "." + c.domain);
    const expiry = c.session || !c.expirationDate ? 0 : Math.floor(c.expirationDate);
    lines.push([domain, c.hostOnly ? "FALSE" : "TRUE", c.path, c.secure ? "TRUE" : "FALSE", expiry, c.name, c.value].join("\t"));
  }
  return lines.join("\n") + "\n";
}

async function recordCookieState(id, state) {
  const { cookieState = {} } = await chrome.storage.local.get("cookieState");
  await chrome.storage.local.set({ cookieState: { ...cookieState, [id]: state } });
}

/** Envoie les cookies à une instance (serverId) ou à toutes celles qui les reçoivent automatiquement. */
async function uploadCookies({ serverId } = {}) {
  const s = await settings();
  const targets = serverId ? s.servers.filter((x) => x.id === serverId) : s.servers.filter((x) => x.sendCookies);
  if (!targets.length) throw new LocalifyError(t("errNotConfigured"), 0, "notConfigured");
  let content;
  try {
    content = await cookiesText();
  } catch (e) {
    for (const target of targets) await recordCookieState(target.id, { at: null, error: e.message });
    throw e;
  }
  const errors = [];
  for (const target of targets) {
    try {
      await call(target, "PUT", "/api/admin/youtube/cookies", { content });
      await recordCookieState(target.id, { at: Date.now(), error: null });
    } catch (e) {
      await recordCookieState(target.id, { at: null, error: e.message });
      errors.push(e.message);
    }
  }
  if (errors.length) throw new LocalifyError(errors.join("\n"));
}

async function scheduleCookies() {
  const s = await settings();
  await chrome.alarms.clear(COOKIE_ALARM);
  if (s.servers.some((x) => x.sendCookies)) {
    chrome.alarms.create(COOKIE_ALARM, { periodInMinutes: Math.max(1, Number(s.cookieHours) || 24) * 60, delayInMinutes: 1 });
  }
}

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === COOKIE_ALARM) uploadCookies().catch(() => {});
});
chrome.runtime.onInstalled.addListener(scheduleCookies);
chrome.runtime.onStartup.addListener(scheduleCookies);
chrome.storage.onChanged.addListener((changes) => {
  if (changes.servers || changes.cookieHours) scheduleCookies();
});

chrome.action.onClicked.addListener(() => chrome.runtime.openOptionsPage());

// ---------- Messages du script de page et des options ----------

const handlers = {
  status: async ({ videoId, url }) => {
    const target = await server();
    return { ...(await call(target, "GET", `/api/extension/status?${q({ videoId, url })}`)), serverName: target.name };
  },
  import: importVideo,
  subscribe: async ({ url, isAlbum, importExisting }) =>
    call(await server(), "POST", "/api/admin/youtube/subscriptions", { url, isAlbum: !!isAlbum, importExisting: !!importExisting }),
  uploadCookies,
  test: async ({ serverId }) => call(await server(serverId), "GET", "/api/extension/status?videoId=dQw4w9WgXcQ"),
  openOptions: () => chrome.runtime.openOptionsPage(),
  migrate,
};

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  // Seuls nos propres scripts parlent au service worker.
  if (sender.id !== chrome.runtime.id) return false;
  const handler = handlers[message?.type];
  if (!handler) return false;
  Promise.resolve()
    .then(() => handler(message))
    .then(
      (data) => sendResponse({ ok: true, data }),
      (e) => sendResponse({ ok: false, error: e.message, status: e.status ?? 0, code: e.code ?? null }),
    );
  return true;
});
