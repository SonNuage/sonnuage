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
  applyTheme(localStorage.getItem('localify.theme'));
} catch {
  // Stockage indisponible (navigation privée stricte) : thème du système.
}

window.localify = {
  languages: () => (navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language]).join(','),
  setLanguage: (code) => { document.documentElement.lang = code; },
  setTheme: applyTheme,
};
