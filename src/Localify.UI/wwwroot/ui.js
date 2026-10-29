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

// Langue : celles du navigateur ou du téléphone, et l'attribut lang du document (lecteurs d'écran, césure).
window.localify = {
  languages: () => (navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language]).join(','),
  setLanguage: (code) => { document.documentElement.lang = code; },
};
