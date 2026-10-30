// Menu de l'icône Localify : instance cible, actions pour l'onglet YouTube actif, envoi des cookies.
// Toutes les requêtes passent par le service worker (background.js) ; ce script ne fait que l'affichage.

const t = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String));
const $ = (id) => document.getElementById(id);
const locale = chrome.i18n.getUILanguage();
const COOKIE_ALARM = "localify-cookies"; // même nom que dans background.js
const PENDING = ["Queued", "Downloading", "Processing"];
const STATE_CHIPS = {
  Queued: ["stateQueued", "warn"],
  Downloading: ["stateDownloading", "warn"],
  Processing: ["stateProcessing", "warn"],
  Done: ["stateDone", "good"],
  Failed: ["stateFailed", "bad"],
  Skipped: ["stateSkipped", ""],
};

let servers = [];
let serverId = null;
let page = null; // résultat de LocalifyPage.parse pour l'onglet actif
let tabInfo = {}; // titre, chaîne lus dans la page (facultatif)
let generation = 0; // change avec l'instance choisie : les réponses d'avant sont ignorées
let pollTimer = null;

// ---------- Outils ----------

function translate(root) {
  root.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));
  root.querySelectorAll("[data-i18n-title]").forEach((el) => (el.title = t(el.dataset.i18nTitle)));
  root.querySelectorAll("[data-i18n-aria]").forEach((el) => el.setAttribute("aria-label", t(el.dataset.i18nAria)));
}

/** Petit constructeur d'éléments : h("button", { className: "secondary" }, "Texte"). */
function h(tag, props = {}, ...children) {
  const el = Object.assign(document.createElement(tag), props);
  el.append(...children.filter((c) => c != null && c !== false));
  return el;
}

async function send(message) {
  const r = await chrome.runtime.sendMessage({ ...message, serverId });
  if (!r) throw new Error(t("errNoResponse"));
  if (!r.ok) throw Object.assign(new Error(r.error), { status: r.status, code: r.code });
  return r.data;
}

function setMsg(el, text, kind = "") {
  el.hidden = !text;
  el.textContent = text ?? "";
  el.className = `msg ${kind}`;
}

function setChip(el, text, kind = "") {
  el.hidden = !text;
  el.textContent = text ?? "";
  el.className = `chip ${kind}`;
}

/** Erreur au chargement d'un état : celles qui touchent tout le serveur sont déjà sur la carte de l'instance. */
function loadError(el, e) {
  if (e.status === 401 || e.code === "unreachable" || e.code === "notConfigured") return;
  setMsg(el, e.message, "bad");
}

function openOptions() {
  chrome.runtime.openOptionsPage();
  window.close();
}

/** « il y a 5 min », « dans 3 h » dans la langue du navigateur. */
function relative(ms) {
  const rtf = new Intl.RelativeTimeFormat(locale, { style: "short", numeric: "always" });
  const minutes = Math.round(ms / 60000);
  const abs = Math.abs(minutes);
  if (abs < 60) return rtf.format(Math.sign(ms) * Math.max(1, abs), "minute");
  if (abs < 48 * 60) return rtf.format(Math.round(minutes / 60), "hour");
  return rtf.format(Math.round(minutes / 1440), "day");
}

function checkbox(label, checked) {
  const input = h("input", { type: "checkbox", checked });
  return { input, label: h("label", {}, input, h("span", { textContent: label })) };
}

// ---------- Instance ----------

function current() {
  return servers.find((x) => x.id === serverId);
}

function renderServer() {
  const target = current();
  $("serverName").textContent = target.name;
  $("serverName").hidden = servers.length > 1;
  $("serverSelect").hidden = servers.length < 2;
  let host = target.url;
  try {
    host = new URL(target.url).host;
  } catch {
    // Adresse enregistrée invalide : affichée telle quelle.
  }
  $("serverHost").textContent = host;
  $("badge").textContent = t("badgeChecking");
  $("badge").className = "badge";
  $("serverError").hidden = true;
}

async function testServer() {
  const gen = generation;
  try {
    await send({ type: "test" });
    if (gen !== generation) return;
    $("badge").textContent = t("badgeConnected");
    $("badge").className = "badge good";
  } catch (e) {
    if (gen !== generation) return;
    $("badge").textContent = t(e.status === 401 ? "badgeKeyRejected" : e.code === "unreachable" ? "badgeUnreachable" : "badgeError");
    $("badge").className = "badge bad";
    setMsg($("serverError"), e.message, "bad");
  }
}

// ---------- Cookies ----------

async function renderCookies() {
  const target = current();
  $("cookies").hidden = !target?.sendCookies;
  if (!target?.sendCookies) return;
  const { cookieState = {} } = await chrome.storage.local.get("cookieState");
  const state = cookieState[target.id];
  const alarm = await chrome.alarms.get(COOKIE_ALARM).catch(() => null);
  const parts = [];
  if (state?.error) parts.push(t("cookiesLastError", state.error));
  else if (state?.at) parts.push(t("popupCookiesSent", relative(state.at - Date.now())));
  else parts.push(t("cookiesNeverSent"));
  if (alarm?.scheduledTime) parts.push(t("popupCookiesNext", relative(Math.max(60000, alarm.scheduledTime - Date.now()))));
  $("cookieText").textContent = parts.join(" · ");
  $("cookieText").style.color = state?.error ? "var(--bad)" : "var(--muted)";
}

$("cookiesNow").addEventListener("click", async () => {
  const button = $("cookiesNow");
  button.disabled = true;
  setMsg($("cookieMsg"), t("statusSending"));
  try {
    await send({ type: "uploadCookies" });
    setMsg($("cookieMsg"), t("popupCookiesDone"), "good");
  } catch (e) {
    setMsg($("cookieMsg"), e.message, "bad");
  }
  button.disabled = false;
  renderCookies();
});

chrome.storage.onChanged.addListener((changes) => {
  if (changes.cookieState) renderCookies();
});

// ---------- Vidéo ----------

function videoCard(video) {
  const chip = h("span", { className: "chip", hidden: true });
  const button = h("button", { type: "button", textContent: t("popupImportVideo"), disabled: true });
  const msg = h("div", { className: "msg", hidden: true });
  const card = h("section", { className: "card" },
    h("div", { className: "head" }, h("span", { className: "kicker", textContent: t("kindVideo") }), chip),
    h("div", { className: "title", textContent: tabInfo.title || video.videoId }),
    button, msg);

  function show(status) {
    clearTimeout(pollTimer);
    const s = status?.status;
    const [key, kind] = STATE_CHIPS[s] ?? [];
    setChip(chip, key ? t(s === "Done" && status.needsReview ? "stateDoneReview" : key) : "", kind);
    button.disabled = s === "Done" || PENDING.includes(s);
    button.textContent = t(s === "Failed" ? "popupRetryImport" : s === "Done" ? "popupAlreadyImported" : PENDING.includes(s) ? "popupImporting" : "popupImportVideo");
    if (s === "Failed") setMsg(msg, status.error ? t("msgFailedReason", status.error) : t("msgFailed"), "bad");
    if (PENDING.includes(s)) pollTimer = setTimeout(refresh, 4000);
  }

  async function refresh() {
    const gen = generation;
    try {
      const status = await send({ type: "status", videoId: video.videoId });
      if (gen === generation) show(status);
    } catch (e) {
      if (gen === generation) loadError(msg, e);
    }
  }

  button.addEventListener("click", async () => {
    const gen = generation;
    button.disabled = true;
    setMsg(msg, t("popupAdding"));
    try {
      const result = await send({ type: "import", videoId: video.videoId, url: video.url, title: tabInfo.title, channel: tabInfo.channelName });
      if (gen !== generation) return;
      setMsg(msg, t(result?.queued ? "popupQueued" : "popupAlreadyKnown"), "good");
      refresh();
    } catch (e) {
      if (gen !== generation) return;
      setMsg(msg, e.message, "bad");
      button.disabled = false;
    }
  });

  refresh();
  return card;
}

// ---------- Playlist / album ----------

function listCard(list) {
  const album = checkbox(t("optAlbum"), list.album);
  const existing = checkbox(t("optExisting"), false);
  const chip = h("span", { className: "chip", hidden: true });
  const subscribe = h("button", { type: "button", disabled: true });
  const once = h("button", { type: "button", className: "secondary", textContent: t("popupImportOnce") });
  const msg = h("div", { className: "msg", hidden: true });
  const kicker = h("span", { className: "kicker" });
  const card = h("section", { className: "card" },
    h("div", { className: "head" }, kicker, chip),
    h("div", { className: "buttons" }, subscribe, once),
    h("div", { className: "checks" }, album.label, existing.label),
    msg);

  const labels = () => {
    const isAlbum = album.input.checked;
    kicker.textContent = t(isAlbum ? "kindAlbum" : "kindPlaylist");
    if (!subscribe.dataset.done) subscribe.textContent = t(isAlbum ? "popupSyncAlbum" : "popupSyncPlaylist");
  };
  album.input.addEventListener("change", labels);
  labels();

  function subscribed() {
    subscribe.dataset.done = "1";
    subscribe.disabled = true;
    subscribe.textContent = t("popupSynced");
    setChip(chip, t("stateSubscribed"), "good");
    existing.label.hidden = true;
  }

  (async () => {
    const gen = generation;
    try {
      const status = await send({ type: "status", url: list.url });
      if (gen !== generation) return;
      if (status.subscribed) subscribed();
      else subscribe.disabled = false;
    } catch (e) {
      if (gen === generation) loadError(msg, e);
    }
  })();

  subscribe.addEventListener("click", async () => {
    const gen = generation;
    const importExisting = existing.input.checked;
    subscribe.disabled = once.disabled = true;
    setMsg(msg, t("msgSubscribing"));
    try {
      await send({ type: "subscribe", url: list.url, isAlbum: album.input.checked, importExisting });
      if (gen !== generation) return;
      subscribed();
      setMsg(msg, `${t("popupSubscribed")} ${t(importExisting ? "msgSubscribedExisting" : "msgSubscribedFuture")}`, "good");
    } catch (e) {
      if (gen !== generation) return;
      setMsg(msg, e.message, "bad");
      subscribe.disabled = false;
    }
    once.disabled = false;
  });

  once.addEventListener("click", async () => {
    const gen = generation;
    const wasDisabled = subscribe.disabled;
    subscribe.disabled = once.disabled = true;
    setMsg(msg, t("popupReadingList"));
    try {
      const r = await send({ type: "importList", url: list.url, isAlbum: album.input.checked });
      if (gen !== generation) return;
      setMsg(msg, t("popupListQueued", r?.queued ?? 0, r?.alreadyKnown ?? 0), "good");
    } catch (e) {
      if (gen === generation) setMsg(msg, e.message, "bad");
    }
    subscribe.disabled = wasDisabled;
    once.disabled = false;
  });

  return card;
}

// ---------- Chaîne ----------

function channelCard(channel) {
  const existing = checkbox(t("optExistingChannel"), true);
  const chip = h("span", { className: "chip", hidden: true });
  const button = h("button", { type: "button", textContent: t("popupSyncChannel"), disabled: true });
  const msg = h("div", { className: "msg", hidden: true });
  const card = h("section", { className: "card" },
    h("div", { className: "head" }, h("span", { className: "kicker", textContent: t("kindChannel") }), chip),
    h("div", { className: "title", textContent: channel.name || channel.label }),
    h("div", { className: "hint", textContent: t("popupChannelHint") }),
    button,
    h("div", { className: "checks" }, existing.label),
    msg);

  function subscribed() {
    button.disabled = true;
    button.textContent = t("popupChannelSynced");
    setChip(chip, t("stateSubscribed"), "good");
    existing.label.hidden = true;
  }

  (async () => {
    const gen = generation;
    try {
      const status = await send({ type: "status", url: channel.url });
      if (gen !== generation) return;
      if (status.subscribed) subscribed();
      else button.disabled = false;
    } catch (e) {
      if (gen === generation) loadError(msg, e);
    }
  })();

  button.addEventListener("click", async () => {
    const gen = generation;
    const importExisting = existing.input.checked;
    button.disabled = true;
    setMsg(msg, t("msgSubscribing"));
    try {
      await send({ type: "subscribe", url: channel.url, isAlbum: false, importExisting });
      if (gen !== generation) return;
      subscribed();
      setMsg(msg, `${t("popupSubscribed")} ${t(importExisting ? "msgSubscribedExisting" : "msgSubscribedFuture")}`, "good");
    } catch (e) {
      if (gen !== generation) return;
      setMsg(msg, e.message, "bad");
      button.disabled = false;
    }
  });

  return card;
}

// ---------- Page de l'onglet ----------

/** Titre et chaîne de la vidéo affichée, lus dans la page (YouTube, YouTube Music, version mobile). */
function readVideoPage(videoId) {
  const text = (sel) => document.querySelector(sel)?.textContent?.trim() || null;
  const flexy = document.querySelector("ytd-watch-flexy");
  // Après une navigation interne, les balises <meta> restent celles de la première page chargée.
  const metaFresh = document.querySelector('meta[itemprop="identifier"]')?.content === videoId;
  const owner = document.querySelector(
    "ytd-watch-metadata #owner ytd-channel-name a[href], ytd-video-owner-renderer a[href], " +
    "ytmusic-player-bar .byline a[href*='channel/'], ytm-slim-owner-renderer a[href]",
  );
  const fallbackHref = metaFresh ? document.querySelector('span[itemprop="author"] link[itemprop="url"]')?.href : null;
  const fallbackName = metaFresh ? document.querySelector('span[itemprop="author"] link[itemprop="name"]')?.getAttribute("content") : null;
  const fresh = !flexy || !flexy.getAttribute("video-id") || flexy.getAttribute("video-id") === videoId;
  return {
    title: fresh ? text("h1.ytd-watch-metadata, ytmusic-player-bar .title, .slim-video-information-title, #title h1") : null,
    channelName: fresh ? owner?.textContent?.trim() || fallbackName || null : fallbackName,
    channelHref: fresh ? owner?.href || fallbackHref || null : fallbackHref,
  };
}

async function readTab(tab, video) {
  if (!chrome.scripting || tab?.id == null) return {};
  try {
    const [result] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: readVideoPage, args: [video.videoId] });
    return result?.result ?? {};
  } catch {
    // Page non scriptable (permission absente, onglet en chargement) : on se passe du titre et de la chaîne.
    return {};
  }
}

function renderActions() {
  clearTimeout(pollTimer);
  const actions = $("actions");
  actions.replaceChildren();
  if (!page || (!page.video && !page.list && !page.channel)) {
    actions.append(h("section", { className: "card empty-page", textContent: t("popupNoPage") }));
    return;
  }
  if (page.video) actions.append(videoCard(page.video));
  if (page.list) actions.append(listCard(page.list));
  if (page.channel) actions.append(channelCard(page.channel));
}

function selectServer(id) {
  serverId = id;
  generation++;
  renderServer();
  testServer();
  renderCookies();
  setMsg($("cookieMsg"), "");
  renderActions();
}

// ---------- Démarrage ----------

async function init() {
  document.documentElement.lang = locale;
  translate(document);
  $("version").textContent = `Localify ${chrome.runtime.getManifest().version}`;
  $("settings").addEventListener("click", openOptions);
  $("footerSettings").addEventListener("click", openOptions);
  $("configure").addEventListener("click", openOptions);

  // Anciens réglages (une seule instance) : le service worker les convertit.
  await chrome.runtime.sendMessage({ type: "migrate" }).catch(() => {});
  const s = await chrome.storage.local.get({ servers: [], defaultServerId: null });
  servers = s.servers.filter((x) => x.url && x.apiKey);
  if (!servers.length) {
    $("empty").hidden = false;
    return;
  }

  const select = $("serverSelect");
  for (const x of servers) select.append(h("option", { value: x.id, textContent: x.name }));
  select.addEventListener("change", () => selectServer(select.value));
  const initial = servers.some((x) => x.id === s.defaultServerId) ? s.defaultServerId : servers[0].id;
  select.value = initial;
  $("server").hidden = false;

  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  page = LocalifyPage.parse(tab?.url ?? "");
  if (page?.video) {
    tabInfo = await readTab(tab, page.video);
    tabInfo.title ||= tab.title?.replace(/^\(\d+\)\s*/, "").replace(/\s+-\s+YouTube( Music)?$/, "").trim() || null;
    // Page de vidéo : on propose aussi sa chaîne si la page la donne.
    if (!page.channel && tabInfo.channelHref) {
      const channel = LocalifyPage.channelOf(tabInfo.channelHref);
      if (channel) page.channel = { ...channel, name: tabInfo.channelName };
    }
  }
  selectServer(initial);
}

init();
