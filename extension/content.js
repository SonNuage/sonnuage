// Bouton Localify sur YouTube / YouTube Music : « Ajouter à Localify » sur une vidéo,
// « S'abonner dans Localify » sur une playlist ou une chaîne. YouTube est une SPA : on suit les changements d'adresse.
// Les actions vont à l'instance par défaut (réglages de l'extension).

(() => {
  const PENDING = ["Queued", "Downloading", "Processing"];
  const t = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String));
  let current = null; // { key, kind, … } de la page affichée
  let pollTimer = null;

  // ---------- Interface (Shadow DOM : les styles de YouTube n'y entrent pas) ----------

  const host = document.createElement("div");
  host.id = "localify-extension";
  const root = host.attachShadow({ mode: "closed" });
  root.innerHTML = `
    <style>
      :host { all: initial; }
      .box { position: fixed; left: 16px; bottom: 16px; z-index: 2147483647; display: flex; flex-direction: column; gap: 6px;
             font: 500 13px/1.3 Roboto, Arial, sans-serif; color: #f1f1f1; max-width: 320px; }
      .row { display: flex; gap: 6px; align-items: center; }
      button { font: inherit; border: 0; border-radius: 18px; padding: 8px 14px; cursor: pointer; background: #1db954; color: #000; box-shadow: 0 2px 8px rgba(0,0,0,.35); }
      button:hover:not(:disabled) { filter: brightness(1.1); }
      button:disabled { cursor: default; background: #3a3a3a; color: #ccc; }
      button.ghost { background: #272727; color: #f1f1f1; padding: 8px 10px; }
      .msg { background: #212121; border-radius: 8px; padding: 6px 10px; box-shadow: 0 2px 8px rgba(0,0,0,.35); white-space: pre-line; }
      .msg.bad { background: #5c1a1a; }
      .opts { background: #212121; border-radius: 8px; padding: 8px 10px; display: flex; flex-direction: column; gap: 4px; box-shadow: 0 2px 8px rgba(0,0,0,.35); }
      label { display: flex; gap: 6px; align-items: center; cursor: pointer; }
      [hidden] { display: none !important; }
    </style>
    <div class="box" hidden>
      <div class="opts" hidden>
        <label><input type="checkbox" name="album" /> <span data-i18n="optAlbum"></span></label>
        <label><input type="checkbox" name="existing" /> <span data-i18n="optExisting"></span></label>
      </div>
      <div class="msg" hidden></div>
      <div class="row">
        <button class="main"></button>
        <button class="ghost more" data-i18n-title="titleMoreOptions" hidden>⋯</button>
        <button class="ghost close" data-i18n-title="titleHide">✕</button>
      </div>
    </div>`;
  root.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));
  root.querySelectorAll("[data-i18n-title]").forEach((el) => (el.title = t(el.dataset.i18nTitle)));
  const $ = (s) => root.querySelector(s);
  const box = $(".box"), main = $(".main"), msg = $(".msg"), opts = $(".opts"), more = $(".more");

  function render({ label, disabled = false, message = null, bad = false }) {
    if (current?.dismissed) return;
    box.hidden = false;
    main.textContent = label;
    main.disabled = disabled;
    main.title = current?.serverName ? t("titleTarget", current.serverName) : "";
    msg.hidden = !message;
    msg.textContent = message ?? "";
    msg.classList.toggle("bad", bad);
  }

  $(".close").addEventListener("click", () => {
    box.hidden = true;
    if (current) current.dismissed = true;
  });
  more.addEventListener("click", () => (opts.hidden = !opts.hidden));
  main.addEventListener("click", () => current?.onClick?.());

  // ---------- Échanges avec le service worker ----------

  async function send(message) {
    let r;
    try {
      r = await chrome.runtime.sendMessage(message);
    } catch (e) {
      // Extension rechargée : ce script n'est plus relié à elle.
      throw new Error(/context invalidated/i.test(e.message) ? t("errReloaded") : e.message);
    }
    if (!r) throw new Error(t("errNoResponse"));
    if (!r.ok) throw Object.assign(new Error(r.error), { status: r.status, code: r.code });
    return r.data;
  }

  // ---------- Lecture de la page ----------

  function pageInfo() {
    const url = new URL(location.href);
    const music = url.hostname === "music.youtube.com";
    const origin = music ? "https://music.youtube.com" : "https://www.youtube.com";
    const videoId = url.pathname === "/watch" ? url.searchParams.get("v") : url.pathname.match(/^\/shorts\/([\w-]{11})/)?.[1];
    if (videoId && /^[\w-]{11}$/.test(videoId)) {
      return { kind: "video", key: `v:${videoId}`, videoId, url: `${origin}/watch?v=${videoId}` };
    }
    const list = url.pathname === "/playlist" ? url.searchParams.get("list") : null;
    if (list) return { kind: "playlist", key: `p:${list}`, url: `${origin}/playlist?list=${encodeURIComponent(list)}`, album: /^OLAK5uy_/.test(list) };
    const channel = url.pathname.match(/^\/(@[^/]+|channel\/[^/]+|c\/[^/]+|user\/[^/]+)/)?.[1];
    if (channel) return { kind: "channel", key: `c:${channel}`, url: `https://www.youtube.com/${channel}` };
    return null;
  }

  function videoTitle() {
    const el = document.querySelector("h1.ytd-watch-metadata, ytmusic-player-bar .title, #title h1");
    return el?.textContent?.trim() || document.title.replace(/^\(\d+\)\s*/, "").replace(/\s+-\s+YouTube( Music)?$/, "").trim();
  }

  function videoChannel() {
    const el = document.querySelector("ytd-watch-metadata #owner #channel-name a, ytd-video-owner-renderer #channel-name a, ytmusic-player-bar .byline a");
    return el?.textContent?.trim() || null;
  }

  // ---------- Vidéo ----------

  function showVideoStatus(status) {
    if (!status?.status || status.status === "Skipped") {
      render({ label: t("btnAdd") });
      current.onClick = importVideo;
    } else if (status.status === "Done") {
      render({ label: t(status.needsReview ? "btnImportedReview" : "btnImported"), disabled: true });
    } else if (PENDING.includes(status.status)) {
      render({ label: t(status.status === "Queued" ? "btnQueued" : "btnImporting"), disabled: true });
      schedulePoll();
    } else if (status.status === "Failed") {
      render({ label: t("btnRetryImport"), message: status.error ? t("msgFailedReason", status.error) : t("msgFailed"), bad: true });
      current.onClick = importVideo;
    }
  }

  async function refreshVideo(page) {
    try {
      const status = await send({ type: "status", videoId: page.videoId });
      page.serverName = status.serverName;
      if (current === page) showVideoStatus(status);
    } catch (e) {
      if (current === page) showError(e);
    }
  }

  async function importVideo() {
    const page = current;
    render({ label: t("btnAdding"), disabled: true });
    try {
      const result = await send({ type: "import", videoId: page.videoId, url: page.url, title: videoTitle(), channel: videoChannel() });
      if (current !== page) return;
      render(result.queued
        ? { label: t("btnQueued"), disabled: true, message: t("msgQueued") }
        : { label: t("btnAlreadyKnown"), disabled: true });
      schedulePoll();
    } catch (e) {
      if (current === page) showError(e, importVideo);
    }
  }

  function schedulePoll() {
    clearTimeout(pollTimer);
    const page = current;
    pollTimer = setTimeout(() => current === page && refreshVideo(page), 5000);
  }

  // ---------- Playlist / chaîne ----------

  async function refreshList(page) {
    more.hidden = false;
    $("input[name=album]").checked = !!page.album;
    $("input[name=existing]").checked = false;
    try {
      const status = await send({ type: "status", url: page.url });
      page.serverName = status.serverName;
      if (current !== page) return;
      if (status.subscribed) {
        more.hidden = true;
        opts.hidden = true;
        render({ label: t("btnSubscribed"), disabled: true });
      } else {
        render({ label: t("btnSubscribe") });
        current.onClick = subscribe;
      }
    } catch (e) {
      if (current === page) showError(e);
    }
  }

  async function subscribe() {
    const page = current;
    const isAlbum = $("input[name=album]").checked;
    const importExisting = $("input[name=existing]").checked;
    opts.hidden = true;
    render({ label: t("btnSubscribing"), disabled: true, message: t("msgSubscribing") });
    try {
      await send({ type: "subscribe", url: page.url, isAlbum, importExisting });
      if (current !== page) return;
      more.hidden = true;
      render({ label: t("btnSubscribed"), disabled: true, message: t(importExisting ? "msgSubscribedExisting" : "msgSubscribedFuture") });
    } catch (e) {
      if (current === page) showError(e, subscribe);
    }
  }

  // ---------- Commun ----------

  function showError(e, retry) {
    if (e.code === "notConfigured") {
      render({ label: t("btnConfigure"), message: e.message, bad: true });
      current.onClick = () => send({ type: "openOptions" });
      return;
    }
    render({ label: retry ? t("btnRetry") : t("extName"), disabled: !retry, message: e.message, bad: true });
    current.onClick = retry;
  }

  function update() {
    const page = pageInfo();
    if (page?.key === current?.key) return;
    clearTimeout(pollTimer);
    current = page;
    opts.hidden = true;
    more.hidden = true;
    if (!page) {
      box.hidden = true;
      return;
    }
    if (!host.isConnected) document.documentElement.appendChild(host);
    render({ label: t("btnLoading"), disabled: true });
    if (page.kind === "video") refreshVideo(page);
    else refreshList(page);
  }

  // yt-navigate-finish (youtube.com) n'existe pas sur YouTube Music : on surveille aussi l'adresse.
  document.addEventListener("yt-navigate-finish", update);
  window.addEventListener("popstate", update);
  setInterval(update, 1000);
  update();
})();
