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

## Import depuis YouTube

Réservé aux administrateurs (`/admin/youtube`), avec [yt-dlp](https://github.com/yt-dlp/yt-dlp) :

- **Explorer** : recherche YouTube Music (meilleures métadonnées) ou YouTube, ou adresse collée d'une vidéo,
  d'une playlist, d'un album ou d'une chaîne ; sélection puis import.
- **File d'attente** : un téléchargement à la fois, progression en direct. La meilleure piste audio est gardée
  telle quelle (Opus ou AAC, aucun réencodage ; MP3 en option). Quand YouTube refuse (trop de requêtes,
  « confirmez que vous n'êtes pas un robot »), la file se met en pause et reprend seule (30 min, puis le double).
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

L'APK est dans `out/`. Au premier lancement, l'application demande l'adresse du serveur.

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
(variables masquées). Un keystore se crée avec :

```bash
keytool -genkeypair -v -keystore localify.keystore -alias localify -keyalg RSA -keysize 4096 -validity 10000
```
