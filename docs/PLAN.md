# Localify — plan de refonte

## 1. Constat sur les POC (`legacy/`)

| Sujet | POC | Problème |
|---|---|---|
| Serveur | `server.js`, Node `http` brut | Pas de BDD : la bibliothèque est rescannée à **chaque** appel `/api/library` ; IDs = index en mémoire (changent à chaque scan, cassent favoris et liens) |
| Métadonnées | Déduites des noms de fichiers | Tags ID3/Vorbis ignorés, cas spéciaux codés en dur (`AC_DC`), doublons « (1) » gérés par heuristique |
| Durées | Parsing MP3/WAV maison | Faux pour le VBR, rien pour FLAC/OGG/M4A |
| Transcodage | WAV → MP3 192k seulement, après transcodage complet | Premier octet attendu toute la durée de l'encodage (timeout Cloudflare de 100 s sur les gros WAV), aucune purge du cache |
| Sécurité | Aucune authentification, CORS `*` | Exposer ça derrière Cloudflare = bibliothèque publique |
| Statique | `staticPath.includes('..')` | Sert n'importe quel fichier du dossier de l'app (dont `server.js` et `.env`) |
| Web | Un `script.js` de 600 lignes, état dans `localStorage` | Favoris locaux au navigateur, aucune playlist, pas de MediaSession |
| Android | WebView Capacitor pointant sur `http://192.168.1.20:3000` | IP en dur, cleartext, la lecture s'arrête en arrière-plan, pas de notification média, pas d'Android Auto, aucun cache |
| Dépôt | Aucun git ; 1,7 Go de musique, SDK Android et `ffmpeg.exe` dans le dossier | — |

Conclusion : réécriture complète plutôt que reprise. Les POC restent dans `legacy/` comme référence.

## 2. Architecture cible (.NET 10)

```
src/
  Localify.Contracts/   DTO partagés serveur ⇄ clients
  Localify.Server/      ASP.NET Core : API, scan, transcodage, auth, héberge le client web
  Localify.UI/          Razor Class Library : toutes les pages (lecteur + admin), partagée web/Android
  Localify.Web/         Blazor WebAssembly : lecteur navigateur (implémentation audio JS)
  Localify.Mobile/      .NET MAUI Blazor Hybrid : mêmes pages + service natif Media3 (Android Auto)
tests/
  Localify.Server.Tests/
deploy/                 compose.yaml, exemple de tunnel Cloudflare
```

UI unique en Razor partagée entre le navigateur et Android. Le moteur audio est une interface
(`IPlayer`) avec deux implémentations : `<audio>` côté web, ExoPlayer/Media3 côté Android.

### Serveur
- **Stockage** : SQLite + EF Core (migrations), fichier dans `/config`. Bibliothèque musicale montée en lecture seule (`/music`), uploads dans `/music-uploads`.
- **Scan** : service d'arrière-plan + `FileSystemWatcher`, scan incrémental (taille + mtime), tags lus avec TagLib# (repli sur l'arborescence `Artiste/Album/NN titre`), pochette intégrée ou image du dossier, déduplication par empreinte (taille + durée + titre).
- **IDs stables** : GUID par piste, conservés aux rescans tant que le chemin ne change pas.
- **Auth** : comptes utilisateurs (rôles `admin` / `user`), JWT courts + refresh tokens en rotation, admin initial créé via variables d'environnement. Les URL média acceptent un jeton de flux signé en query (`?t=`) pour `<audio>` et Android Auto.
- **Streaming** : `Range` complet sur l'original. Transcodage à la volée par ffmpeg :
  `original`, `lossless` (FLAC), `high` (MP3 320), `normal` (MP3 192), `low` (MP3 128), `data-saver` (MP3 64).
  Le flux est envoyé **pendant** l'encodage (premiers octets en < 1 s) et écrit en parallèle dans un cache disque ; plusieurs clients partagent le même encodage ; les requêtes suivantes sont servies depuis le cache avec `Range`. Cache borné en taille (LRU).
- **Playlists**, favoris, historique d'écoute, recherche (artistes / albums / pistes).
- **Admin** : upload multi-fichiers **par morceaux de 8 Mo** (limite de 100 Mo par requête chez Cloudflare), édition des métadonnées, rescan, gestion des utilisateurs, état du cache.

### Derrière Cloudflare
- `ForwardedHeaders` + `CF-Connecting-IP` pour les vraies IP (journaux, rate limiting du login).
- Uploads découpés (< 100 Mo), premier octet des flux en < 100 s garanti par le transcodage en streaming.
- En-têtes de cache : assets web versionnés `immutable` ; API et flux `private, no-store` (Cloudflare ne met pas les flux en cache, ce qui évite aussi le problème des CGU sur les médias en cache).
- Conçu pour `cloudflared` (tunnel) : aucun port à ouvrir. Optionnel : Cloudflare Access devant `/admin`.
- `/healthz` pour le tunnel et Docker.

### Client web (Blazor WASM)
Accueil, artistes, albums, recherche, playlists (création, ajout, réordonnancement), favoris, file d'attente, shuffle/repeat, choix de qualité, **pré-chargement** des pistes suivantes, MediaSession (touches média, écran de verrouillage). Pages admin réservées au rôle admin.

### Android (MAUI Blazor Hybrid)
- Mêmes pages Razor dans une WebView ; la lecture passe par un `MediaLibraryService` Media3 natif (lecture en arrière-plan, notification, Bluetooth).
- **Android Auto** : arbre de navigation `MediaLibraryService` (Playlists, Albums, Artistes, Favoris, Récents) et recherche vocale.
- **Réseau instable** : cache disque Media3 (`SimpleCache`, 1 Go par défaut), téléchargement anticipé des N pistes suivantes, buffer de lecture étendu, choix de qualité selon le réseau (Wi-Fi / mobile).
- URL du serveur configurable, HTTPS, pas de cleartext.

## 3. CI/CD GitLab (`git.pmjb.org`)

| Stage | Job | Détail |
|---|---|---|
| build | `server` | `dotnet build` + tests (xUnit, SQLite en mémoire, ffmpeg installé dans l'image) |
| build | `android` | workload `maui-android`, APK signé si la variable de keystore est définie, sinon APK debug |
| package | `docker` | image serveur multi-stage poussée sur le registre GitLab (`:sha`, `:latest` sur `main`, `:vX.Y.Z` sur tag) |
| release | `release` | sur tag : release GitLab avec l'APK joint |

## 4. Étapes

1. Squelette de la solution, CI, Dockerfile
2. Serveur : modèle, scan, API bibliothèque, auth
3. Streaming et transcodage
4. Playlists, favoris, admin (upload, métadonnées, utilisateurs)
5. UI partagée + client web
6. App Android + Media3 + Android Auto + pré-chargement
7. Documentation de déploiement (compose + tunnel Cloudflare)

### Import YouTube (ajout)

Inspiré de TonTuyau / LaPompe : file d'import mono-tâche (`YoutubeImporter`) avec pause automatique sur refus de
YouTube, abonnements (`YoutubeSyncService`), outils yt-dlp/Deno mis à jour depuis l'admin (`YoutubeTools`).
Métadonnées : `MetadataResolver` croise YouTube Music, MusicBrainz (1 req/s, User-Agent identifié) et AcoustID ;
pochettes Cover Art Archive. Seules des adresses youtube.com / youtu.be / music.youtube.com sont transmises à
yt-dlp. Tableau de bord : `/api/admin/dashboard` (provenance, historique, disque) rendu en SVG.

## 5. Pistes futures
Extension de navigateur « Ajouter à Localify » sur YouTube (façon LaPompe, avec clés d'API et synchro des cookies), paroles synchronisées, ReplayGain, mode hors-ligne complet (téléchargement manuel d'albums), Chromecast, scrobbling Last.fm/ListenBrainz, API compatible Subsonic (pour réutiliser des clients existants).
