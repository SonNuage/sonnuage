// Lecture d'une adresse YouTube / YouTube Music : vidéo, playlist (ou album) et chaîne.
// Travaille sur une chaîne d'adresse (onglet actif lu par le menu), pas sur `location`.
// Exposé en global : LocalifyPage.parse(href) → { video, list, channel } ou null hors de YouTube.

(() => {
  const HOSTS = new Set(["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"]);
  const VIDEO_ID = /^[\w-]{11}$/;
  // Listes propres à un compte ou générées à la volée (mix « RD… » sauf playlists RDCLAK de YouTube Music,
  // « J'aime », « À regarder plus tard », titres aimés de YouTube Music) : rien à synchroniser.
  const PERSONAL_LIST = /^(RD(?!CLAK)|LL$|WL$|LM$)/;
  const CHANNEL_PATH = /^\/((?:@|%40)[^/]+|channel\/UC[\w-]+|c\/[^/]+|user\/[^/]+)(?:\/|$)/;

  function channelFrom(path) {
    const root = path.match(CHANNEL_PATH)?.[1]?.replace(/^%40/, "@");
    if (!root) return null;
    let label = root;
    try {
      label = decodeURIComponent(root.replace(/^(channel|c|user)\//, ""));
    } catch {
      // Encodage invalide : on garde le chemin tel quel.
    }
    // Racine de la chaîne (sans onglet /videos, /releases…) : le serveur choisit lui-même les onglets à lire.
    return { url: `https://www.youtube.com/${root}`, label };
  }

  function parse(href) {
    let u;
    try {
      u = new URL(href);
    } catch {
      return null;
    }
    const host = u.hostname.toLowerCase();
    if (u.protocol !== "https:" && u.protocol !== "http:") return null;
    if (!HOSTS.has(host)) return null;
    const music = host === "music.youtube.com";
    const origin = music ? "https://music.youtube.com" : "https://www.youtube.com";
    const path = u.pathname;
    const info = { music, video: null, list: null, channel: null };

    const videoId =
      host === "youtu.be" ? path.slice(1).split("/")[0]
      : path === "/watch" ? u.searchParams.get("v")
      : path.match(/^\/(?:shorts|live)\/([^/]+)/)?.[1];
    if (videoId && VIDEO_ID.test(videoId)) info.video = { videoId, url: `${origin}/watch?v=${videoId}` };

    const list = u.searchParams.get("list");
    if (list && /^[\w-]+$/.test(list) && !PERSONAL_LIST.test(list)) {
      info.list = { id: list, url: `${origin}/playlist?list=${list}`, album: list.startsWith("OLAK5uy_") };
    }

    // YouTube Music : /browse/MPREb_… est une page d'album, /browse/UC… une page d'artiste.
    const browse = music ? path.match(/^\/browse\/([\w-]+)/)?.[1] : null;
    if (browse?.startsWith("MPREb_")) info.list = { id: browse, url: `${origin}/browse/${browse}`, album: true };
    if (browse?.startsWith("UC")) info.channel = { url: `https://www.youtube.com/channel/${browse}`, label: browse };

    if (!info.video && !info.list && !info.channel) info.channel = channelFrom(path);
    return info;
  }

  /** Chaîne d'une adresse de propriétaire lue dans la page (« /@nom », « https://music.youtube.com/channel/UC… »). */
  function channelOf(href) {
    try {
      return channelFrom(new URL(href, "https://www.youtube.com").pathname);
    } catch {
      return null;
    }
  }

  globalThis.LocalifyPage = { parse, channelOf };
})();
