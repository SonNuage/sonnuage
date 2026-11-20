// Petits utilitaires d'interface partagés web/Android.

// Remplissage des curseurs (lecture, volume) jusqu'au pouce, y compris pendant qu'on le fait glisser,
// avant même que Blazor ne réaffiche la valeur.
(() => {
  const fill = (el) => {
    const min = Number(el.min) || 0;
    const max = Number(el.max) || 100;
    const pct = max > min ? ((Number(el.value) - min) / (max - min)) * 100 : 0;
    el.style.setProperty('--pct', `${Math.min(100, Math.max(0, pct))}%`);
  };
  document.addEventListener('input', (e) => {
    if (e.target instanceof HTMLInputElement && e.target.classList.contains('slider')) fill(e.target);
  });
})();

// Position dans la page : une nouvelle page s'ouvre en haut ; un retour (ou une avance) dans l'historique retrouve
// la position qu'on avait quittée, une fois le contenu revenu (il se recharge souvent après coup).
(() => {
  const box = () => document.querySelector('.content-scroll');
  const keyOf = () => `${history.state && history.state._index != null ? history.state._index : ''}|${location.href}`;
  const saved = new Map();
  let current = keyOf();
  let target = null;
  let until = 0;
  let movedAt = 0;

  const apply = () => {
    const el = box();
    if (target == null || !el) return;
    el.scrollTop = target;
    if ((target === 0 && el.scrollTop === 0) || Date.now() > until) target = null;
  };
  const go = (top) => {
    movedAt = Date.now();
    target = top;
    until = movedAt + (top > 0 ? 3000 : 400);
    requestAnimationFrame(apply);
  };
  // Position gardée au fil du défilement de l'utilisateur. Juste après un changement de page, le défilement vient
  // du contenu remplacé (ou de la position qu'on rend) : il ne compte pas. Blazor fait aussi des allers-retours
  // dans l'historique avant d'accepter un retour : on ne lit donc jamais la position sur un « popstate ».
  document.addEventListener('scroll', (e) => {
    if (target == null && Date.now() - movedAt > 500 && e.target instanceof Element && e.target.classList.contains('content-scroll'))
      saved.set(current, e.target.scrollTop);
  }, { capture: true, passive: true });
  // Le contenu de la page arrive par morceaux : on réapplique la position à chaque changement, tant qu'il faut.
  new MutationObserver(() => { if (target != null) apply(); })
    .observe(document.documentElement, { childList: true, subtree: true });
  // Un geste de l'utilisateur l'emporte sur la position retrouvée.
  for (const type of ['wheel', 'touchstart', 'keydown', 'mousedown'])
    addEventListener(type, () => { target = null; }, { capture: true, passive: true });

  const push = history.pushState.bind(history);
  history.pushState = (...args) => {
    // La page quittée est encore affichée : sa position est sûre.
    const el = box();
    if (el) saved.set(current, el.scrollTop);
    push(...args);
    current = keyOf();
    window.__sonnuageBack = false;
    go(0);
  };
  const replace = history.replaceState.bind(history);
  history.replaceState = (...args) => {
    // Même page (adresse mise à jour, par exemple la recherche tapée) : la position la suit.
    const top = saved.get(current);
    replace(...args);
    current = keyOf();
    if (top != null) saved.set(current, top);
  };
  addEventListener('popstate', () => {
    window.__sonnuageBack = true;
    current = keyOf();
    go(saved.get(current) ?? 0);
  });
})();

// « / » ouvre la recherche de l'en-tête, sauf pendant une saisie.
document.addEventListener('keydown', (e) => {
  if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
  const t = e.target;
  if (t instanceof HTMLElement && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName))) return;
  const button = document.querySelector('.topbar-search');
  if (!button) return;
  e.preventDefault();
  button.click();
});

// Langue : celles du navigateur ou du téléphone, et l'attribut lang du document (lecteurs d'écran, césure).
// Thème : appliqué dès le chargement pour ne pas afficher l'autre thème le temps du démarrage.
const applyTheme = (mode) => {
  if (mode === 'light' || mode === 'dark') document.documentElement.dataset.theme = mode;
  else delete document.documentElement.dataset.theme;
};
try {
  applyTheme(localStorage.getItem('sonnuage.theme'));
} catch {
  // Stockage indisponible (navigation privée stricte) : thème du système.
}

// Extraits YouTube (page Demandes) : une balise audio à part, hors du lecteur. L'état remonte à PreviewPlayer.
const preview = (() => {
  let audio = null;
  let owner = null;
  const send = (state) => { if (owner) owner.invokeMethodAsync('OnPreviewAudio', state).catch(() => {}); };
  const stop = () => {
    if (!audio) return;
    audio.onplaying = audio.onended = audio.onerror = null;
    audio.pause();
    audio.removeAttribute('src');
    audio.load();
    audio = null;
  };
  return {
    play: (url, dotnet) => {
      stop();
      owner = dotnet;
      audio = new Audio(url);
      audio.onplaying = () => send('playing');
      audio.onended = () => send('ended');
      audio.onerror = () => send('error');
      audio.play().catch((e) => { if (!e || e.name !== 'AbortError') send('error'); });
    },
    stop,
  };
})();

// Copie dans le presse-papiers. Hors contexte sécurisé (http sur le réseau local), navigator.clipboard n'existe pas :
// on passe alors par une zone de texte temporaire.
const copy = async (text) => {
  if (navigator.clipboard && window.isSecureContext) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Refusé (permission, page sans le focus) : on tente l'ancienne méthode.
    }
  }
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.style.position = 'fixed';
  area.style.opacity = '0';
  document.body.appendChild(area);
  area.select();
  try {
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    area.remove();
  }
};

window.sonnuage = {
  preview,
  copy,
  languages: () => (navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language]).join(','),
  // Fuseau IANA de l'appareil (heures calmes, sauvegardes), comme le voit le navigateur.
  timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
  setLanguage: (code) => { document.documentElement.lang = code; },
  // Carrousel : fait défiler la ligne d'à peu près sa largeur visible.
  scrollRow: (track, direction) => {
    if (track) track.scrollBy({ left: direction * Math.max(200, track.clientWidth * 0.85), behavior: 'smooth' });
  },
  // Carrousel : note sur son cadre si la ligne est au début ou à la fin, pour cacher la flèche inutile.
  watchRow: (track) => {
    if (!track || !track.parentElement) return;
    const update = () => {
      track.parentElement.dataset.start = track.scrollLeft <= 2;
      track.parentElement.dataset.end = track.scrollLeft + track.clientWidth >= track.scrollWidth - 2;
    };
    if (!track.dataset.watched) {
      track.dataset.watched = 'true';
      track.addEventListener('scroll', update, { passive: true });
      new ResizeObserver(update).observe(track);
    }
    update();
  },
  setTheme: applyTheme,
  // La page affichée vient-elle d'un retour (ou d'une avance) dans l'historique ? Voir PageMemory.
  cameBack: () => !!window.__sonnuageBack,
  // Amène un élément en haut de la vue (par exemple l'en-tête d'une liste qui vient de remplacer une autre).
  reveal: el => el && el.scrollIntoView({ block: 'start' }),
  // Paroles : la ligne en cours au milieu de la liste, sans faire défiler le reste de la page.
  scrollLyric: (list, index) => {
    const line = list && list.children[index];
    if (line) list.scrollTo({ top: line.offsetTop - (list.clientHeight - line.offsetHeight) / 2, behavior: 'smooth' });
  },
};
