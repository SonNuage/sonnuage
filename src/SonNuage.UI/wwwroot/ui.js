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
  setTheme: applyTheme,
  // Paroles : la ligne en cours au milieu de la liste, sans faire défiler le reste de la page.
  scrollLyric: (list, index) => {
    const line = list && list.children[index];
    if (line) list.scrollTo({ top: line.offsetTop - (list.clientHeight - line.offsetHeight) / 2, behavior: 'smooth' });
  },
};
