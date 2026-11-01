# SonNuage

Serveur de musique auto-hébergé façon Spotify : bibliothèque scannée depuis vos dossiers, lecteur web,
application Android (avec Android Auto), appli de bureau (Windows, macOS, Linux), playlists, favoris, transcodage à la volée, administration.

*SonNuage* : SoundCloud, traduit.

| Projet | Rôle |
|---|---|
| `src/SonNuage.Server` | API ASP.NET Core (.NET 10), scan, transcodage ffmpeg, auth JWT, héberge le client web |
| `src/SonNuage.UI` | Pages Razor partagées web / Android / bureau (lecteur + admin) |
| `src/SonNuage.Web` | Client Blazor WebAssembly |
| `tests/SonNuage.Server.Tests` | Tests unitaires et d'intégration (xUnit v3) |
| `legacy/` | Anciens POC Node.js / Capacitor, gardés pour référence |

Les autres parties du projet ont leur propre dépôt :

| Dépôt | Contenu |
|---|---|
| [sonnuage](https://git.pmjb.org/sonnuage/sonnuage) (celui-ci) | Serveur, client web, bibliothèques partagées `SonNuage.Contracts` et `SonNuage.UI` |
| [sonnuage-android](https://git.pmjb.org/sonnuage/sonnuage-android) | Application Android (.NET MAUI Blazor Hybrid, Media3, Android Auto) |
| [sonnuage-desktop](https://git.pmjb.org/sonnuage/sonnuage-desktop) | Appli de bureau Windows, macOS, Linux (Photino : webview du système, icône de la barre système) |
| [sonnuage-extension](https://git.pmjb.org/sonnuage/sonnuage-extension) | Extension de navigateur (import YouTube, envoi des cookies) |

`SonNuage.Contracts` et `SonNuage.UI` sont publiés en paquets NuGet dans le registre de ce dépôt à chaque build de
`main` (version `0.1.0-ci.N`) et à chaque tag `vX.Y.Z`. L'application Android et l'appli de bureau les utilisent.

Le plan de refonte et les choix d'architecture sont dans [docs/PLAN.md](docs/PLAN.md).

## Fonctionnalités

- **Bibliothèque** : scan incrémental (tags TagLib#, repli sur `Artiste/Album/NN titre`), surveillance du dossier,
  pochettes (dossier, intégrées, envoyées depuis l'admin), dédoublonnage des copies « (1) ».
- **Lecture** : file d'attente, aléatoire (la piste en cours reste en tête, l'ordre d'origine est restauré),
  répétition, favoris, historique, reprise de la file au redémarrage.
- **Qualité** : originale, FLAC, MP3 320/192/128/64. Le transcodage est diffusé pendant l'encodage
  (premier octet en ~0,1 s) et mis en cache (purge LRU). Un fichier n'est jamais « surclassé ».
- **Réseau instable** : pré-chargement des pistes suivantes (web : en mémoire, Android : cache disque de 1 Go
  et tampon de 5 min), reprise automatique sur erreur réseau, qualité distincte en données mobiles sur Android.
- **Android Auto** : onglets Playlists / Albums / Artistes / Récents, recherche, « Ok Google, mets … ».
- **Admin** (`/admin`) : tableau de bord (provenance des titres, historique des ajouts, disque), rescan,
  envoi d'albums par morceaux de 8 Mo (reprise possible), import depuis YouTube, édition des tags et pochettes,
  gestion des comptes.
- **Langues** : français et anglais. Chaque compte choisit sa langue (Réglages), sinon celle du navigateur ou du téléphone.

## Traductions

Les textes de l'interface et les messages du serveur sont dans `src/SonNuage.Contracts/Localization/{code}.json`
(un fichier par langue, embarqué dans l'assembly et découvert automatiquement). **Ajouter une langue** :

1. copier `en.json` en `de.json` (code ISO 639-1) et traduire les valeurs, sans toucher aux clés.
2. remplir `_meta` : `name` (nom affiché dans le sélecteur, dans la langue elle-même), séparateurs décimal et
   de milliers, noms des mois et des jours.
3. si les pluriels de la langue ne suivent pas la règle anglaise (`one` pour 1, sinon `other`), ajouter sa règle
   dans `PluralRules` ([Translations.cs](src/SonNuage.Contracts/Localization/Translations.cs)).
4. `dotnet test` : un test vérifie que chaque langue a exactement les clés et les variables (`{count}`, `{name}`…) de l'anglais.

Une clé absente retombe sur l'anglais. Format des valeurs : variables nommées `{name}`, pluriels en objet
`{ "one": "…", "other": "…" }` (catégories CLDR) choisis par la variable `count`. Côté Razor :
`@L["home.recentlyAdded"]`, `@L["common.tracks", ("count", n)]`, `L.Date(date)`. Côté serveur, un message
destiné à l'utilisateur s'écrit `Tx.Key("errors.nameRequired")` : il est traduit selon `Accept-Language` dans les
`ProblemDetails`, ou à l'affichage (`L.Server(texte)`) quand il est stocké, comme les erreurs d'import YouTube.

## Import depuis YouTube

Réservé aux administrateurs (`/admin/youtube`), avec [yt-dlp](https://github.com/yt-dlp/yt-dlp) :

- **Explorer** : recherche YouTube Music (meilleures métadonnées) ou YouTube, ou adresse collée d'une vidéo,
  d'une playlist, d'un album ou d'une chaîne. Sélection puis import.
- **File d'attente** : chaque vidéo devient une suite de tâches (téléchargement → empreinte AcoustID →
  métadonnées et rangement, voir [Tâches](#tâches)). Plusieurs téléchargements en parallèle (3 par défaut),
  progression en direct. La meilleure piste audio est gardée telle quelle (Opus ou AAC, aucun réencodage.
  MP3 en option). Quand YouTube refuse (trop de requêtes, « confirmez que vous n'êtes pas un robot »), les
  téléchargements se mettent en pause et reprennent seuls (30 min, puis le double).
- **Métadonnées** recomposées automatiquement : champs YouTube Music, recherche MusicBrainz notée sur titre,
  artiste et durée (l'album studio officiel est préféré aux compilations), empreinte sonore AcoustID en option
  (clé gratuite + `fpcalc`). Pochette de Cover Art Archive, sinon miniature recadrée au carré. Tags complets
  (identifiants MusicBrainz, lien de la vidéo) et rangement dans `uploads/YouTube/<id de la vidéo>/` :
  le titre, texte libre, ne vit que dans les tags et ne sert jamais de nom de fichier.
  Sous le seuil de confiance réglable, l'import est signalé « à vérifier » : l'admin choisit parmi les
  propositions, cherche sur MusicBrainz ou saisit à la main. Le fichier est retagué et déplacé, le morceau
  garde sa place dans les playlists.
- **Abonnements** : playlists, albums ou chaînes synchronisés périodiquement (6 h par défaut).
- **Réglages** : mise à jour de yt-dlp / Deno en un clic (releases GitHub officielles, SHA-256 vérifiés),
  cookies YouTube (format cookies.txt), format audio, seuil, clé AcoustID.

L'image Docker embarque yt-dlp, Deno et fpcalc. Ne téléchargez que ce que vous avez le droit de conserver
(œuvres libres, vos propres contenus, ou selon le droit applicable chez vous) : vérifier cela vous incombe.

### Extension de navigateur

L'extension (Chrome, Edge, Firefox) a son propre dépôt : [sonnuage-extension](https://git.pmjb.org/sonnuage/sonnuage-extension).
Elle s'authentifie avec une clé créée dans *Administration → Clés d'API*. Cette clé ne permet que d'ajouter des
vidéos à la file, de lire une adresse YouTube, de s'abonner, d'envoyer des cookies YouTube et de lire l'état d'une
vidéo.

Derrière Cloudflare Access, autorisez l'en-tête `Authorization: ApiKey …` (règle *Bypass* ou jeton de
service) pour les chemins `/api/admin/youtube/*` et `/api/extension/*`.

## Tâches

**Admin → Tâches** montre les files d'exécution, les suites de tâches (en cours, en attente, en échec,
terminées), leurs étapes et le journal de chaque étape en direct. On y annule, relance et crée des tâches.

- Chaque file a un plafond de tâches simultanées : téléchargements (réglage YouTube, 1 à 6), **AcoustID : une
  seule à la fois**, imports et rescans confondus (et une requête par seconde au plus, imposée aussi par le
  client), métadonnées MusicBrainz : une à la fois.
- **Rescan AcoustID** de toute la bibliothèque ou d'un artiste : empreinte de chaque morceau, comparaison avec
  MusicBrainz, correction automatique des correspondances sûres (au-dessus du seuil réglé dans YouTube, jamais
  sur un morceau modifié à la main) ou simples propositions dans le journal.
- La file vit dans la base : les tâches interrompues par un arrêt reprennent au redémarrage, et plusieurs
  instances partageant la base peuvent consommer la file sans dépasser les plafonds
  (`SonNuage__Jobs__Worker`, `SonNuage__Jobs__WorkerName`). Suites terminées gardées 14 jours
  (`SonNuage__Jobs__RetentionDays`).

Ce qui vient ensuite : [docs/ROADMAP.md](docs/ROADMAP.md).

## Recherche

Sans configuration, la recherche se fait en base : sous-chaînes, insensible à la casse et aux accents.
Avec **Elasticsearch** (8.x ou 9.x), elle devient instantanée et tolérante :

- résultats dès les premières lettres de chaque mot (`hig volt` → High Voltage)
- fautes de frappe (`gilmor ratle` → Rattle That Lock) et « Vouliez-vous dire … ? »
- suggestions pendant la saisie, y compris au milieu d'un nom (`gilm` → David Gilmour)
- `AC/DC`, `T.N.T.` trouvés en tapant `acdc`, `tnt`
- titres les plus écoutés favorisés
- recherche sémantique vectorielle en option (`SonNuage__Search__SemanticInferenceId` : identifiant d'un endpoint
  d'inférence Elasticsearch, par exemple un modèle E5 multilingue. Selon la licence de votre cluster).

L'index est reconstruit automatiquement après chaque scan, chaque modification depuis l'admin, au retour
d'Elasticsearch après une panne et toutes les 30 minutes.

**Résilience** : une requête qui n'aboutit pas en 2 s (`SonNuage__Search__TimeoutMilliseconds`) est servie
par la recherche en base. La requête Elasticsearch continue en arrière-plan avec nouvelles tentatives et
son résultat sert la frappe suivante. Après des échecs répétés, Elasticsearch est mis de côté (réponse en
base immédiate) et sondé en arrière-plan jusqu'à son retour. L'état est visible dans l'admin.

| Variable | Rôle |
|---|---|
| `SonNuage__Search__Url` | URL d'Elasticsearch. Vide = recherche en base |
| `SonNuage__Search__ApiKey` ou `__Username` / `__Password` | Authentification |
| `SonNuage__Search__CertificateFingerprint` | Empreinte SHA-256 du certificat auto-signé d'Elasticsearch |
| `SonNuage__Search__Index` | Préfixe des index (`sonnuage`) |
| `SonNuage__Search__TimeoutMilliseconds` | Délai avant repli sur la base (`2000`) |

En local : `docker run -d -p 127.0.0.1:9200:9200 -e discovery.type=single-node -e xpack.security.enabled=false docker.elastic.co/elasticsearch/elasticsearch:9.2.0`
(la configuration de dev pointe déjà sur `http://localhost:9200`).

## Développement

Prérequis : SDK .NET 10, ffmpeg (dans le `PATH` ou `bin/ffmpeg.exe`).

```bash
dotnet run --project src/SonNuage.Server --launch-profile SonNuage.Server
```

Puis http://localhost:5080. La configuration de dev ([appsettings.Development.json](src/SonNuage.Server/appsettings.Development.json))
lit la musique dans `data/`, écrit base et caches dans `var/` et crée le compte `admin` au premier démarrage.

Tests (`SONNUAGE_TEST_FFMPEG` active les tests de transcodage, `SONNUAGE_TEST_ELASTICSEARCH` ceux de la recherche avancée) :

```bash
dotnet test --project tests/SonNuage.Server.Tests
```

### Application Android

Elle a son propre dépôt : [sonnuage-android](https://git.pmjb.org/sonnuage/sonnuage-android). Elle compile contre les paquets
`SonNuage.Contracts` et `SonNuage.UI` publiés par ce dépôt, ou directement contre ses sources si les deux dépôts
sont clonés côte à côte (voir son README).

## Déploiement derrière Cloudflare

Le serveur est prévu pour être joint uniquement par un **tunnel Cloudflare** (aucun port ouvert) :

1. Cloudflare Zero Trust → Networks → Tunnels : créer un tunnel, router le nom d'hôte
   (ex. `musique.example.org`) vers `http://sonnuage:8080`, copier le jeton.
2. Sur la machine hôte :
   ```bash
   cd deploy && cp .env.example .env   # renseigner image, dossier musique, mot de passe admin, jeton
   docker compose up -d
   ```

Points spécifiques Cloudflare, déjà gérés :
- l'IP client vient de `CF-Connecting-IP` (limitation des tentatives de connexion par IP)
- les envois de fichiers sont découpés en morceaux de 8 Mo (limite de 100 Mo par requête)
- les flux audio sont en `Cache-Control: private, no-store` et protégés par un jeton en query :
  Cloudflare ne met rien en cache côté CDN.
- le transcodage envoie ses premiers octets immédiatement (pas de timeout 524 sur les longs fichiers)
- `/healthz` pour les sondes.

`SonNuage__BehindProxy=true` (défaut) fait confiance aux en-têtes du proxy : ne pas exposer le port 8080
directement sur Internet dans ce mode.

### Configuration (variables d'environnement)

| Variable | Défaut | Rôle |
|---|---|---|
| `SonNuage__MusicPaths__0` | `/music` | Dossier(s) de musique (lecture seule) |
| `SonNuage__UploadPath` | `/uploads` | Destination des envois de l'admin |
| `SonNuage__DataPath` | `/config` | Base SQLite, clé de signature, caches |
| `SonNuage__InitialAdminUser` / `__InitialAdminPassword` | `admin` / aucun | Compte créé si la base est vide |
| `SonNuage__TranscodeCacheMaxMegabytes` | `10240` | Taille du cache de transcodage |
| `SonNuage__MaxConcurrentTranscodes` | `4` | Encodages ffmpeg simultanés |
| `SonNuage__ScanIntervalMinutes` | `360` | Rescan complet périodique (0 = jamais) |
| `SonNuage__BehindProxy` | `true` | Faire confiance à `CF-Connecting-IP` / `X-Forwarded-*` |

## CI/CD GitLab

[.gitlab-ci.yml](.gitlab-ci.yml), environ 1 min 30 : tests serveur et, en parallèle, image Docker poussée sur le
registre du projet (`:sha`), vérifiée au démarrage. Une fois les tests passés : `:latest` sur la branche par
défaut, `:X.Y.Z` sur tag, et paquets NuGet `SonNuage.Contracts` / `SonNuage.UI`. Sur `main`, semantic-release crée
ensuite le tag `vX.Y.Z` et sa release (voir Versions). L'image est assemblée par `crane` : base d'exécution en cache (`runtime-base`, reconstruite quand le
Dockerfile change) + une couche avec l'application publiée par le job.

Le runner doit accepter docker-in-docker (`privileged = true`) : procédure complète dans [docs/CI-RUNNER.md](docs/CI-RUNNER.md).

### Versions

Les versions sont publiées par [semantic-release](https://semantic-release.gitbook.io/) ([.releaserc.json](.releaserc.json)),
d'après les messages de commit au format [Conventional Commits](https://www.conventionalcommits.org/fr/) :
`fix: …` donne un correctif (1.0.1), `feat: …` une version mineure (1.1.0), `feat!: …` ou un pied de page
`BREAKING CHANGE:` une version majeure (2.0.0). `docs`, `chore`, `ci`, `refactor`, `test` ne publient rien.
Sur `main`, le job `semantic-release` crée le tag `vX.Y.Z` et la release GitLab avec ses notes. Le pipeline du
tag publie ensuite la version. Il utilise `GITLAB_TOKEN`, jeton du groupe `sonnuage` (variable masquée et protégée,
tags `v*` protégés).

### GitHub Actions

[.github/workflows/ci.yml](.github/workflows/ci.yml) fait la même chose sur GitHub : tests (avec ffmpeg et
Elasticsearch), image Docker sur `ghcr.io/<propriétaire>/sonnuage`, paquets NuGet dans le registre GitHub du
propriétaire, release sur les tags `vX.Y.Z`. Aucun secret à configurer : le jeton fourni par GitHub suffit.

## Licence

SonNuage est un logiciel libre sous licence [GNU AGPL v3.0 ou ultérieure](LICENSE) : chacun peut l'utiliser,
l'étudier, le modifier et le redistribuer. Une version modifiée, y compris proposée comme service en ligne, doit
publier son code source sous la même licence.

Copyright © 2026 pmj.bailly et les contributeurs de SonNuage.
