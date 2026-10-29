const t = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String));
const $ = (id) => document.getElementById(id);
// www/music/m.youtube.com sont déjà accordés (boutons) ; youtube.com et google.com servent aux cookies de domaine.
const COOKIE_PERMISSION = { permissions: ["cookies"], origins: ["https://youtube.com/*", "https://*.google.com/*"] };
const locale = chrome.i18n.getUILanguage();

// ---------- Traduction de la page ----------

function translate(root) {
  root.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));
  root.querySelectorAll("[data-i18n-placeholder]").forEach((el) => (el.placeholder = t(el.dataset.i18nPlaceholder)));
}

document.documentElement.lang = locale;
document.title = t("optionsTitle");
translate(document);

// ---------- Stockage ----------

async function stored() {
  return chrome.storage.local.get({ servers: [], defaultServerId: null, cookieHours: 24, cookieState: {} });
}

/** Modifie (patch) ou retire (null) une instance enregistrée ; sans effet sur une instance pas encore enregistrée. */
async function persist(id, patch) {
  const { servers } = await stored();
  const index = servers.findIndex((x) => x.id === id);
  if (index < 0) return false;
  if (patch === null) servers.splice(index, 1);
  else servers[index] = { ...servers[index], ...patch };
  await chrome.storage.local.set({ servers });
  return true;
}

function normalizeServer(value) {
  const url = new URL(value.trim());
  if (url.protocol !== "https:" && url.protocol !== "http:") throw new Error();
  return url.origin + url.pathname.replace(/\/+$/, "");
}

async function send(message) {
  const r = await chrome.runtime.sendMessage(message);
  if (!r?.ok) throw new Error(r?.error ?? t("errNoResponse"));
  return r.data;
}

function show(el, text, kind) {
  el.textContent = text;
  el.className = `status ${kind ?? ""}`;
}

function showCookieState(card, state) {
  const el = card.querySelector("[data-role=cookies]");
  if (state?.error) show(el, t("cookiesLastError", state.error), "bad");
  else if (state?.at) show(el, t("cookiesLastSent", new Date(state.at).toLocaleString(locale)), "muted");
  else show(el, t("cookiesNeverSent"), "muted");
}

// Plus aucune instance ne reçoit de cookies : on rend la permission.
async function releaseCookiePermission() {
  const { servers } = await stored();
  if (!servers.some((x) => x.sendCookies)) await chrome.permissions.remove(COOKIE_PERMISSION).catch(() => {});
}

// ---------- Cartes d'instance ----------

function addCard(server, isDefault, cookieState) {
  const card = $("instance").content.firstElementChild.cloneNode(true);
  translate(card);
  const field = (name) => card.querySelector(`[name=${name}]`);
  const status = card.querySelector("[data-role=status]");
  field("name").value = server.name;
  field("url").value = server.url;
  field("apiKey").value = server.apiKey;
  field("default").checked = isDefault;
  field("sendCookies").checked = server.sendCookies;
  showCookieState(card, cookieState);

  field("default").addEventListener("change", async () => {
    document.querySelectorAll("#instances [name=default]").forEach((r) => (r.checked = r === field("default")));
    await chrome.storage.local.set({ defaultServerId: server.id });
  });

  field("sendCookies").addEventListener("change", () => {
    if (!field("sendCookies").checked) {
      server.sendCookies = false;
      persist(server.id, { sendCookies: false }).then(releaseCookiePermission);
      return;
    }
    // La demande de permission doit partir directement du geste de l'utilisateur.
    chrome.permissions.request(COOKIE_PERMISSION).then(async (granted) => {
      field("sendCookies").checked = granted;
      server.sendCookies = granted;
      if (granted) await persist(server.id, { sendCookies: true });
      else show(status, t("errCookiePermission"), "bad");
    });
  });

  field("save").addEventListener("click", () => {
    let url;
    try {
      url = normalizeServer(field("url").value);
    } catch {
      show(status, t("statusInvalidUrl"), "bad");
      return;
    }
    const apiKey = field("apiKey").value.trim();
    if (!apiKey) {
      show(status, t("statusMissingKey"), "bad");
      return;
    }
    chrome.permissions.request({ origins: [`${new URL(url).origin}/*`] }).then(async (granted) => {
      if (!granted) {
        show(status, t("statusPermissionDenied"), "bad");
        return;
      }
      Object.assign(server, { name: field("name").value.trim() || new URL(url).host, url, apiKey });
      field("name").value = server.name;
      field("url").value = url;
      const s = await stored();
      const servers = s.servers.some((x) => x.id === server.id) ? s.servers.map((x) => (x.id === server.id ? server : x)) : [...s.servers, server];
      const defaultServerId = servers.some((x) => x.id === s.defaultServerId) ? s.defaultServerId : server.id;
      await chrome.storage.local.set({ servers, defaultServerId });
      field("default").checked = defaultServerId === server.id;
      show(status, t("statusTesting"));
      try {
        await send({ type: "test", serverId: server.id });
        show(status, t("statusConnected"), "good");
      } catch (e) {
        show(status, e.message, "bad");
      }
    });
  });

  field("cookiesNow").addEventListener("click", () => {
    chrome.permissions.request(COOKIE_PERMISSION).then(async (granted) => {
      if (!granted) {
        show(status, t("errCookiePermission"), "bad");
        return;
      }
      show(status, t("statusSending"));
      try {
        await send({ type: "uploadCookies", serverId: server.id });
        show(status, "");
      } catch (e) {
        show(status, e.message, "bad");
      }
      showCookieState(card, (await stored()).cookieState[server.id]);
      // Permission demandée pour un envoi ponctuel : rendue si aucune instance n'envoie automatiquement.
      await releaseCookiePermission();
    });
  });

  field("remove").addEventListener("click", async () => {
    if (!confirm(t("confirmRemove", server.name || t("instanceNew")))) return;
    await persist(server.id, null);
    const s = await stored();
    if (s.defaultServerId === server.id) await chrome.storage.local.set({ defaultServerId: s.servers[0]?.id ?? null });
    await releaseCookiePermission();
    card.remove();
    await render();
  });

  $("instances").append(card);
}

async function render() {
  const s = await stored();
  $("instances").replaceChildren();
  s.servers.forEach((server) => addCard(server, server.id === s.defaultServerId, s.cookieState[server.id]));
  if (!s.servers.length) addCard({ id: crypto.randomUUID(), name: "", url: "", apiKey: "", sendCookies: false }, true, null);
  $("cookieHours").value = s.cookieHours;
}

$("add").addEventListener("click", () => {
  addCard({ id: crypto.randomUUID(), name: t("instanceNew"), url: "", apiKey: "", sendCookies: false }, false, null);
});

$("cookieHours").addEventListener("change", () => {
  const hours = Math.min(168, Math.max(1, Math.round(Number($("cookieHours").value) || 24)));
  $("cookieHours").value = hours;
  chrome.storage.local.set({ cookieHours: hours });
});

// Ancienne version (une seule instance) : le service worker convertit les réglages au premier message.
send({ type: "migrate" }).catch(() => {}).finally(render);
