# Localify

Serveur de musique auto-hébergé façon Spotify : bibliothèque scannée depuis vos dossiers, lecteur web,
application Android (avec Android Auto), playlists, favoris, transcodage à la volée, administration.

| Projet | Rôle |
|---|---|
| `src/Localify.Server` | API ASP.NET Core (.NET 10), scan, transcodage ffmpeg, auth JWT, héberge le client web |
| `src/Localify.UI` | Pages Razor partagées web / Android (lecteur + admin) |
| `src/Localify.Web` | Client Blazor WebAssembly |
| `src/Localify.Mobile` | App Android .NET MAUI Blazor Hybrid + service de lecture Media3 (notification, Android Auto) |
| `tests/Localify.Server.Tests` | Tests unitaires et d'intégration (xUnit v3) |
| `legacy/` | Anciens POC Node.js / Capacitor, gardés pour référence |

Le plan de refonte et les choix d'architecture sont dans [docs/PLAN.md](docs/PLAN.md).

## Fonctionnalités

- **Bibliothèque** : scan incrémental (tags TagLib#, repli sur `Artiste/Album/NN titre`), surveillance du dossier,
  pochettes (dossier, intégrées, envoyées depuis l'admin), dédoublonnage des copies « (1) ».
- **Lecture** : file d'attente, aléatoire (la piste en cours reste en tête, l'ordre d'origine est restauré),
  répétition, favoris, historique, reprise de la file au redémarrage.
- **Qualité** : originale, FLAC, MP3 320/192/128/64. Le transcodage est diffusé pendant l'encodage
  (premier octet en ~0,1 s) et mis en cache (purge LRU). Un fichier n'est jamais « surclassé ».
- **Réseau instable** : pré-chargement des pistes suivantes (web : en mémoire ; Android : cache disque de 1 Go
  et tampon de 5 min), reprise automatique sur erreur réseau, qualité distincte en données mobiles sur Android.
- **Android Auto** : onglets Playlists / Albums / Artistes / Récents, recherche, « Ok Google, mets … ».
- **Admin** (`/admin`) : tableau de bord (provenance des titres, historique des ajouts, disque), rescan,
  envoi d'albums par morceaux de 8 Mo (reprise possible), import depuis YouTube, édition des tags et pochettes,
  gestion des comptes.
- **Langues** : français et anglais ; chaque compte choisit sa langue (Réglages), sinon celle du navigateur ou du téléphone.

## Traductions

Les textes de l'interface et les messages du serveur sont dans `src/Localify.Contracts/Localization/{code}.json`
(un fichier par langue, embarqué dans l'assembly et découvert automatiquement). **Ajouter une langue** :

1. copier `en.json` en `de.json` (code ISO 639-1) et traduire les valeurs, sans toucher aux clés ;
2. remplir `_meta` : `name` (nom affiché dans le sélecteur, dans la langue elle-même), séparateurs décimal et
   de milliers, noms des mois et des jours ;
3. si les pluriels de la langue ne suivent pas la règle anglaise (`one` pour 1, sinon `other`), ajouter sa règle
   dans `PluralRules` ([Translations.cs](src/Localify.Contracts/Localization/Translations.cs)) ;
4. `dotnet test` : un test vérifie que chaque langue a exactement les clés et les variables (`{count}`, `{name}`…) de l'anglais.

Une clé absente retombe sur l'anglais. Format des valeurs : variables nommées `{name}`, pluriels en objet
`{ "one": "…", "other": "…" }` (catégories CLDR) choisis par la variable `count`. Côté Razor :
`@L["home.recentlyAdded"]`, `@L["common.tracks", ("count", n)]`, `L.Date(date)`. Côté serveur, un message
destiné à l'utilisateur s'écrit `Tx.Key("errors.nameRequired")` : il est traduit selon `Accept-Language` dans les
`ProblemDetails`, ou à l'affichage (`L.Server(texte)`) quand il est stocké, comme les erreurs d'import YouTube.

## Import depuis YouTube

Réservé aux administrateurs (`/admin/youtube`), avec [yt-dlp](https://github.com/yt-dlp/yt-dlp) :

- **Explorer** : recherche YouTube Music (meilleures métadonnées) ou YouTube, ou adresse collée d'une vidéo,
  d'une playlist, d'un album ou d'une chaîne ; sélection puis import.
- **File d'attente** : chaque vidéo devient une suite de tâches (téléchargement → empreinte AcoustID →
  métadonnées et rangement, voir [Tâches](#tâches)) ; plusieurs téléchargements en parallèle (3 par défaut),
  progression en direct. La meilleure piste audio est gardée telle quelle (Opus ou AAC, aucun réencodage ;
  MP3 en option). Quand YouTube refuse (trop de requêtes, « confirmez que vous n'êtes pas un robot »), les
  téléchargements se mettent en pause et reprennent seuls (30 min, puis le double).
- **Métadonnées** recomposées automatiquement : champs YouTube Music, recherche MusicBrainz notée sur titre,
  artiste et durée (l'album studio officiel est préféré aux compilations), empreinte sonore AcoustID en option
  (clé gratuite + `fpcalc`). Pochette de Cover Art Archive, sinon miniature recadrée au carré. Tags complets
  (identifiants MusicBrainz, lien de la vidéo) et rangement dans `uploads/YouTube/Artiste/Album/`.
  Sous le seuil de confiance réglable, l'import est signalé « à vérifier » : l'admin choisit parmi les
  propositions, cherche sur MusicBrainz ou saisit à la main ; le fichier est retagué et déplacé, le morceau
  garde sa place dans les playlists.
- **Abonnements** : playlists, albums ou chaînes synchronisés périodiquement (6 h par défaut).
- **Réglages** : mise à jour de yt-dlp / Deno en un clic (releases GitHub officielles, SHA-256 vérifiés),
  cookies YouTube (format cookies.txt), format audio, seuil, clé AcoustID.

L'image Docker embarque yt-dlp, Deno et fpcalc. Ne téléchargez que ce que vous avez le droit de conserver
(œuvres libres, vos propres contenus, ou selon le droit applicable chez vous) : vérifier cela vous incombe.

### Extension de navigateur

Le dossier [`extension/`](extension/) contient une extension Manifest V3 (Chrome, Edge, Firefox 128+). Un clic sur
son icône, dans la barre d'outils, ouvre un menu qui agit sur l'onglet YouTube / YouTube Music affiché, sans rien
ajouter dans les pages de YouTube :

- **Vidéo** (`watch?v=…`, `/shorts/…`, `youtu.be/…`) : *Importer cette vidéo*, avec son état sur le serveur
  (en file, téléchargement, traitement, importée, échec, ignorée).
- **Playlist ou album** (paramètre `list=` ; album si l'identifiant commence par `OLAK5uy_`, ou page
  `music.youtube.com/browse/MPREb_…`) : *Synchroniser cette playlist / cet album* (abonnement) ou *Importer une fois*,
  avec les options *C'est un album* et *Importer aussi les vidéos déjà publiées*. Une vidéo ouverte dans une playlist
  propose les deux. Les mix et listes personnelles (`RD…`, `LL`, `WL`, `LM`) sont ignorés.
- **Chaîne** (`/@nom`, `/channel/UC…`, `/c/…`, `/user/…`, y compris leurs onglets) : *Synchroniser toute la
  chaîne*, abonnement à la racine de la chaîne (le serveur en lit les albums, EP et singles). Sur une page de vidéo,
  la chaîne de la vidéo est aussi proposée quand la page la donne.

Le menu affiche aussi l'instance visée et son état (*Connecté*, *Injoignable*, *Clé refusée*), l'état de l'envoi des
cookies (« envoyés il y a … · prochain envoi dans … », bouton *Envoyer les cookies maintenant*) et un lien vers les
réglages. Permissions : `storage`, `alarms` (envoi périodique des cookies), `activeTab` (lire l'adresse de
l'onglet au clic sur l'icône), `scripting` (lire le titre et la chaîne de la vidéo dans la page YouTube), accès à
www / music / m.youtube.com ; `cookies` et l'accès à chaque serveur Localify sont demandés à la configuration.

1. Dans Localify : *Administration → YouTube → Réglages → Extension de navigateur*, créer une clé et la copier
   (elle n'est affichée qu'une fois ; seule son empreinte est conservée). La clé ne permet que quatre actions :
   ajouter des vidéos à la file, lire une adresse YouTube, s'abonner, envoyer des cookies YouTube ; plus
   l'état d'une vidéo. Rien d'autre de l'API n'est accessible avec elle. Révoquez-la depuis le même écran.
2. Charger l'extension non empaquetée :
   - **Chrome / Edge** : `chrome://extensions` → activer le *mode développeur* → *Charger l'extension non
     empaquetée* → choisir le dossier `extension/`.
   - **Firefox** : `about:debugging#/runtime/this-firefox` → *Charger un module complémentaire temporaire* →
     choisir `extension/manifest.json` (retiré au redémarrage de Firefox).
3. Cliquer sur l'icône de l'extension puis sur la roue dentée (ou *Options*), saisir le nom, l'adresse du serveur (par exemple
   `https://musique.exemple.fr`, ou `http://localhost:5080` en développement) et la clé, puis
   *Enregistrer et tester* : le navigateur demande l'accès à ce seul serveur.

**Plusieurs instances** : *Ajouter une instance* enregistre d'autres serveurs Localify (chacun avec sa propre
clé). Le menu de l'extension vise l'instance cochée *Instance par défaut* ; une liste déroulante y permet d'en
choisir une autre le temps d'une action.

**Envoi des cookies** (facultatif, désactivé par défaut, au choix pour chaque instance) : l'extension exporte
périodiquement les cookies youtube.com / google.com du navigateur au format Netscape vers
`PUT /api/admin/youtube/cookies` des instances cochées, ce qui évite les blocages « confirmez que vous n'êtes pas
un robot ». L'intervalle est commun ; *Envoyer les cookies maintenant* fait un envoi ponctuel. Ces cookies
ouvrent votre compte Google à chaque serveur qui les reçoit : utilisez un profil connecté avec un compte
secondaire. La permission « cookies » est rendue dès qu'aucune instance ne les reçoit plus.

**Langues** : l'extension suit la langue du navigateur, en français ou en anglais (anglais par défaut pour les
autres langues). Les textes sont dans `extension/_locales/<langue>/messages.json` (format `chrome.i18n`, avec une
description par message pour les traducteurs) ; pour ajouter une langue, copier `en/` vers le code de la
langue (`de`, `es`, `pt_BR`…) et traduire les champs `message` en gardant les `$PARAMÈTRES$`. Les messages
d'erreur venus du serveur restent en français.

Derrière Cloudflare Access, autorisez l'en-tête `Authorization: ApiKey …` (règle *Bypass* ou jeton de
service) pour les chemins `/api/admin/youtube/*` et `/api/extension/*`.

## Tâches

**Admin → Tâches** montre les files d'exécution, les suites de tâches (en cours, en attente, en échec,
terminées), leurs étapes et le journal de chaque étape en direct ; on y annule, relance et crée des tâches.

- Chaque file a un plafond de tâches simultanées : téléchargements (réglage YouTube, 1 à 6), **AcoustID : une
  seule à la fois**, imports et rescans confondus (et une requête par seconde au plus, imposée aussi par le
  client), métadonnées MusicBrainz : une à la fois.
- **Rescan AcoustID** de toute la bibliothèque ou d'un artiste : empreinte de chaque morceau, comparaison avec
  MusicBrainz, correction automatique des correspondances sûres (au-dessus du seuil réglé dans YouTube, jamais
  sur un morceau modifié à la main) ou simples propositions dans le journal.
- La file vit dans la base : les tâches interrompues par un arrêt reprennent au redémarrage, et plusieurs
  instances partageant la base peuvent consommer la file sans dépasser les plafonds
  (`Localify__Jobs__Worker`, `Localify__Jobs__WorkerName`). Suites terminées gardées 14 jours
  (`Localify__Jobs__RetentionDays`).

Ce qui vient ensuite : [docs/ROADMAP.md](docs/ROADMAP.md).

## Recherche

Sans configuration, la recherche se fait en base : sous-chaînes, insensible à la casse et aux accents.
Avec **Elasticsearch** (8.x ou 9.x), elle devient instantanée et tolérante :

- résultats dès les premières lettres de chaque mot (`hig volt` → High Voltage) ;
- fautes de frappe (`gilmor ratle` → Rattle That Lock) et « Vouliez-vous dire … ? » ;
- suggestions pendant la saisie, y compris au milieu d'un nom (`gilm` → David Gilmour) ;
- `AC/DC`, `T.N.T.` trouvés en tapant `acdc`, `tnt` ;
- titres les plus écoutés favorisés ;
- recherche sémantique vectorielle en option (`Localify__Search__SemanticInferenceId` : identifiant d'un endpoint
  d'inférence Elasticsearch, par exemple un modèle E5 multilingue ; selon la licence de votre cluster).

L'index est reconstruit automatiquement après chaque scan, chaque modification depuis l'admin, au retour
d'Elasticsearch après une panne et toutes les 30 minutes.

**Résilience** : une requête qui n'aboutit pas en 2 s (`Localify__Search__TimeoutMilliseconds`) est servie
par la recherche en base ; la requête Elasticsearch continue en arrière-plan avec nouvelles tentatives et
son résultat sert la frappe suivante. Après des échecs répétés, Elasticsearch est mis de côté (réponse en
base immédiate) et sondé en arrière-plan jusqu'à son retour. L'état est visible dans l'admin.

| Variable | Rôle |
|---|---|
| `Localify__Search__Url` | URL d'Elasticsearch ; vide = recherche en base |
| `Localify__Search__ApiKey` ou `__Username` / `__Password` | Authentification |
| `Localify__Search__CertificateFingerprint` | Empreinte SHA-256 du certificat auto-signé d'Elasticsearch |
| `Localify__Search__Index` | Préfixe des index (`localify`) |
| `Localify__Search__TimeoutMilliseconds` | Délai avant repli sur la base (`2000`) |

En local : `docker run -d -p 127.0.0.1:9200:9200 -e discovery.type=single-node -e xpack.security.enabled=false docker.elastic.co/elasticsearch/elasticsearch:9.2.0`
(la configuration de dev pointe déjà sur `http://localhost:9200`).

## Développement

Prérequis : SDK .NET 10, ffmpeg (dans le `PATH` ou `bin/ffmpeg.exe`).

```bash
dotnet run --project src/Localify.Server --launch-profile Localify.Server
```

Puis http://localhost:5080. La configuration de dev ([appsettings.Development.json](src/Localify.Server/appsettings.Development.json))
lit la musique dans `data/`, écrit base et caches dans `var/` et crée le compte `admin` au premier démarrage.

Tests (`LOCALIFY_TEST_FFMPEG` active les tests de transcodage, `LOCALIFY_TEST_ELASTICSEARCH` ceux de la recherche avancée) :

```bash
dotnet test --project tests/Localify.Server.Tests
```

### APK Android sans Android Studio

```bash
docker build -f build/android-sdk.Dockerfile -t localify-android-sdk build
```

```bash
docker run --rm -v "$PWD:/host:ro" -v "$PWD/out:/out" -v localify-nuget:/root/.nuget/packages localify-android-sdk sh /host/build/build-apk.sh Release
```

Un seul fichier est produit : `out/localify-release.apk`, signé et installable. `dotnet publish` génère aussi un
`org.pmjb.localify.apk` **non signé** : c'est un intermédiaire, Android refuse de l'installer (« application non
installée » / paquet invalide), il n'est donc plus copié. Sans keystore, l'APK est signé avec la clé de debug de
l'image : pour mettre à jour par-dessus un APK de la CI (autre clé), il faut d'abord désinstaller l'appli.

### Application Android

- **Comptes** : au premier lancement, l'application demande l'adresse du serveur puis l'identifiant. Plusieurs
  comptes (un utilisateur sur un serveur, éventuellement plusieurs serveurs) peuvent être connectés en même temps ;
  **Réglages › Comptes** affiche le compte actif, permet d'en ajouter un, de changer de compte actif ou de se
  déconnecter d'un compte précis. Le compte actif fixe la bibliothèque, la lecture, Android Auto et la notification ;
  en changer arrête la lecture et recharge la file sauvegardée de l'autre compte.
- Chaque compte a ses propres jetons (SecureStorage, renouvelés un par un avec un verrou par compte), sa file
  d'attente, ses réglages de lecture et ses entrées du cache audio. Une installation mono-compte antérieure est
  migrée automatiquement en un premier compte (session, file et réglages compris).
- **Plantages** : toute exception non gérée (.NET, tâches, Java) est écrite dans `crash.log` (dossier privé de
  l'appli) et dans logcat (`adb logcat -s Localify`). Au lancement suivant, un écran natif affiche le rapport avec un
  bouton « Copier le rapport » ; si le démarrage lui-même échoue, cet écran remplace l'interface.
- Le web reste mono-compte (même origine que le serveur) et garde ses clés de stockage.

## Déploiement derrière Cloudflare

Le serveur est prévu pour être joint uniquement par un **tunnel Cloudflare** (aucun port ouvert) :

1. Cloudflare Zero Trust → Networks → Tunnels : créer un tunnel, router le nom d'hôte
   (ex. `musique.example.org`) vers `http://localify:8080`, copier le jeton.
2. Sur la machine hôte :
   ```bash
   cd deploy && cp .env.example .env   # renseigner image, dossier musique, mot de passe admin, jeton
   docker compose up -d
   ```

Points spécifiques Cloudflare, déjà gérés :
- l'IP client vient de `CF-Connecting-IP` (limitation des tentatives de connexion par IP) ;
- les envois de fichiers sont découpés en morceaux de 8 Mo (limite de 100 Mo par requête) ;
- les flux audio sont en `Cache-Control: private, no-store` et protégés par un jeton en query :
  Cloudflare ne met rien en cache côté CDN ;
- le transcodage envoie ses premiers octets immédiatement (pas de timeout 524 sur les longs fichiers) ;
- `/healthz` pour les sondes.

`Localify__BehindProxy=true` (défaut) fait confiance aux en-têtes du proxy : ne pas exposer le port 8080
directement sur Internet dans ce mode.

### Configuration (variables d'environnement)

| Variable | Défaut | Rôle |
|---|---|---|
| `Localify__MusicPaths__0` | `/music` | Dossier(s) de musique (lecture seule) |
| `Localify__UploadPath` | `/uploads` | Destination des envois de l'admin |
| `Localify__DataPath` | `/config` | Base SQLite, clé de signature, caches |
| `Localify__InitialAdminUser` / `__InitialAdminPassword` | `admin` / — | Compte créé si la base est vide |
| `Localify__TranscodeCacheMaxMegabytes` | `10240` | Taille du cache de transcodage |
| `Localify__MaxConcurrentTranscodes` | `4` | Encodages ffmpeg simultanés |
| `Localify__ScanIntervalMinutes` | `360` | Rescan complet périodique (0 = jamais) |
| `Localify__BehindProxy` | `true` | Faire confiance à `CF-Connecting-IP` / `X-Forwarded-*` |

## CI/CD GitLab

[.gitlab-ci.yml](.gitlab-ci.yml) : tests serveur → image Docker poussée sur le registre du projet
(`:sha`, `:latest` sur la branche par défaut, `:X.Y.Z` sur tag) et APK Android en artefact → release
GitLab avec l'APK sur les tags `vX.Y.Z`.

Le runner doit accepter docker-in-docker (`privileged = true`) : procédure complète dans [docs/CI-RUNNER.md](docs/CI-RUNNER.md). Pour signer l'APK de release, définir
`ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` et `ANDROID_KEY_PASSWORD`
(variables masquées). Sans elles, la CI signe avec une clé de secours générée une fois et conservée dans son cache
(les APK successifs restent installables les uns par-dessus les autres tant que ce cache vit). L'artefact contient un
seul APK, `dist/localify-X.Y.Z.apk`, dont la signature est vérifiée (`apksigner verify`). Un keystore se crée avec :

```bash
keytool -genkeypair -v -keystore localify.keystore -alias localify -keyalg RSA -keysize 4096 -validity 10000
```
