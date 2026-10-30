// Démarrage de Blazor. dotnet.js n'a pas d'empreinte dans son nom : on lui ajoute la version du client (?v=…,
// injectée par le serveur dans index.html), sinon un cache intermédiaire (Cloudflare : 4 h) garde l'ancienne
// version après un déploiement. Les autres fichiers .NET ont déjà une empreinte.
(() => {
  const v = new URL(document.currentScript.src).searchParams.get('v');
  Blazor.start({
    loadBootResource: (type, name, uri) => (type === 'dotnetjs' && v ? `${uri}?v=${v}` : undefined),
  });
})();
