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
- **Admin** (`/admin`) : statistiques, rescan, envoi d'albums par morceaux de 8 Mo (reprise possible),
  édition des tags et pochettes, gestion des comptes.

## Développement

Prérequis : SDK .NET 10, ffmpeg (dans le `PATH` ou `bin/ffmpeg.exe`).

```bash
dotnet run --project src/Localify.Server --launch-profile Localify.Server
```

Puis http://localhost:5080. La configuration de dev ([appsettings.Development.json](src/Localify.Server/appsettings.Development.json))
lit la musique dans `data/`, écrit base et caches dans `var/` et crée le compte `admin` au premier démarrage.

Tests (définir `LOCALIFY_TEST_FFMPEG` pour activer les tests de transcodage) :

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

Le runner doit accepter docker-in-docker (`privileged = true`). Pour signer l'APK de release, définir
`ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` et `ANDROID_KEY_PASSWORD`
(variables masquées). Un keystore se crée avec :

```bash
keytool -genkeypair -v -keystore localify.keystore -alias localify -keyalg RSA -keysize 4096 -validity 10000
```
