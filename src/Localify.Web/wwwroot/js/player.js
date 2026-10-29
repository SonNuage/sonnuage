// Moteur audio du lecteur web. La file d'attente est gérée en C# (PlayQueue) ;
// ce module joue une piste, pré-télécharge les suivantes en mémoire et relaie MediaSession.

const MAX_RETRIES = 6;
const MAX_PREFETCHED = 4;

let dotnet = null;
let audio = null;
let currentKey = null;
let retries = 0;
let lastReportedSecond = -1;
let resumeAt = 0;

// key -> { url: objectURL | null, controller, done: Promise }
const prefetched = new Map();

export function init(dotnetRef) {
  dotnet = dotnetRef;
  audio = new Audio();
  audio.preload = 'auto';

  audio.addEventListener('playing', () => { retries = 0; notify('OnPlaying'); });
  audio.addEventListener('pause', () => notify('OnPaused'));
  audio.addEventListener('waiting', () => notify('OnBuffering'));
  audio.addEventListener('ended', () => notify('OnEnded'));
  audio.addEventListener('timeupdate', () => {
    const s = Math.floor(audio.currentTime);
    if (s !== lastReportedSecond) {
      lastReportedSecond = s;
      notify('OnTime', audio.currentTime);
      updatePositionState();
    }
  });
  audio.addEventListener('error', onError);

  if ('mediaSession' in navigator) {
    const ms = navigator.mediaSession;
    const handlers = {
      play: () => notify('OnRemote', 'play', 0),
      pause: () => notify('OnRemote', 'pause', 0),
      previoustrack: () => notify('OnRemote', 'previous', 0),
      nexttrack: () => notify('OnRemote', 'next', 0),
      seekto: (d) => notify('OnRemote', 'seek', d.seekTime ?? 0),
      seekbackward: (d) => notify('OnRemote', 'seek', Math.max(0, audio.currentTime - (d.seekOffset || 10))),
      seekforward: (d) => notify('OnRemote', 'seek', audio.currentTime + (d.seekOffset || 10)),
    };
    for (const [action, handler] of Object.entries(handlers)) {
      try { ms.setActionHandler(action, handler); } catch { /* action non supportée */ }
    }
  }

  // Retour du réseau : reprise d'une lecture coupée.
  window.addEventListener('online', () => {
    if (currentKey && audio.error) reloadCurrent();
  });
}

function notify(method, ...args) {
  if (dotnet) dotnet.invokeMethodAsync(method, ...args).catch(() => {});
}

let currentUrl = null;
let expectedDuration = 0;

export async function load(key, url, startAt, autoplay, duration) {
  currentKey = key;
  currentUrl = url;
  expectedDuration = duration || 0;
  retries = 0;
  resumeAt = startAt || 0;
  lastReportedSecond = -1;

  const entry = prefetched.get(key);
  audio.src = entry && entry.url ? entry.url : url;
  if (resumeAt > 0) {
    audio.addEventListener('loadedmetadata', () => { audio.currentTime = resumeAt; }, { once: true });
  }
  if (autoplay) await safePlay();
  evict(new Set([key]));
}

async function safePlay() {
  try {
    await audio.play();
  } catch (e) {
    // Lecture automatique refusée par le navigateur (pas encore d'interaction) : on signale la pause.
    notify('OnPaused');
  }
}

function onError() {
  const err = audio.error;
  if (!currentKey || !err) return;
  // Le blob local est corrompu ou révoqué : on repasse par le réseau.
  if (audio.src.startsWith('blob:')) {
    prefetched.delete(currentKey);
    audio.src = currentUrl;
    safePlay();
    return;
  }
  if (err.code === MediaError.MEDIA_ERR_NETWORK || err.code === MediaError.MEDIA_ERR_DECODE) {
    if (retries < MAX_RETRIES) {
      retries++;
      const delay = Math.min(1000 * 2 ** (retries - 1), 15000);
      notify('OnBuffering');
      setTimeout(reloadCurrent, delay);
      return;
    }
  }
  notify('OnError', err.code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED ? 'Format non lu par ce navigateur' : 'Lecture impossible (réseau)');
}

function reloadCurrent() {
  const position = audio.currentTime || resumeAt;
  resumeAt = position;
  audio.src = currentUrl;
  audio.addEventListener('loadedmetadata', () => { audio.currentTime = position; }, { once: true });
  safePlay();
}

export function play() { return safePlay(); }
export function pause() { audio.pause(); }
export function stop() {
  audio.pause();
  audio.removeAttribute('src');
  audio.load();
  currentKey = null;
}
export function seek(seconds) {
  if (!isFinite(seconds)) return;
  audio.currentTime = Math.max(0, seconds);
}
export function setVolume(v) { audio.volume = Math.min(1, Math.max(0, v)); }

// Télécharge complètement les pistes suivantes (une à la fois) pour encaisser les coupures réseau.
// items : [{ key, url }]
let prefetchChain = Promise.resolve();
export function prefetch(items) {
  const wanted = new Set(items.map((i) => i.key));
  if (currentKey) wanted.add(currentKey);
  evict(wanted);

  for (const item of items.slice(0, MAX_PREFETCHED)) {
    if (prefetched.has(item.key)) continue;
    const controller = new AbortController();
    const entry = { url: null, controller };
    prefetched.set(item.key, entry);
    prefetchChain = prefetchChain.then(async () => {
      if (prefetched.get(item.key) !== entry) return;
      try {
        const response = await fetch(item.url, { signal: controller.signal });
        if (!response.ok) throw new Error(response.status);
        const blob = await response.blob();
        if (prefetched.get(item.key) === entry) entry.url = URL.createObjectURL(blob);
      } catch {
        if (prefetched.get(item.key) === entry) prefetched.delete(item.key);
      }
    });
  }
}

function evict(keep) {
  for (const [key, entry] of prefetched) {
    if (keep.has(key)) continue;
    entry.controller.abort();
    if (entry.url && audio.src !== entry.url) URL.revokeObjectURL(entry.url);
    prefetched.delete(key);
  }
}

export function setMetadata(meta) {
  if (!('mediaSession' in navigator)) return;
  if (!meta) {
    navigator.mediaSession.metadata = null;
    return;
  }
  navigator.mediaSession.metadata = new MediaMetadata({
    title: meta.title,
    artist: meta.artist,
    album: meta.album,
    artwork: meta.artwork ? [{ src: meta.artwork, sizes: '600x600', type: 'image/jpeg' }] : [],
  });
}

export function setPlaybackState(playing) {
  if ('mediaSession' in navigator) navigator.mediaSession.playbackState = playing ? 'playing' : 'paused';
}

function updatePositionState() {
  if (!('mediaSession' in navigator) || !navigator.mediaSession.setPositionState) return;
  const duration = isFinite(audio.duration) && audio.duration > 0 ? audio.duration : expectedDuration;
  if (!duration) return;
  try {
    navigator.mediaSession.setPositionState({
      duration,
      position: Math.min(audio.currentTime, duration),
      playbackRate: audio.playbackRate,
    });
  } catch { /* valeurs transitoires invalides */ }
}
