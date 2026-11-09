# SonNuage : feuille de route

Ce document dit où en est le projet et ce qui vient ensuite. Il se lit sans connaître le code.
Les priorités sont indicatives : on les revoit à chaque livraison.

Légende : ✅ fait · 🔜 prochain · 💡 idée à creuser · ⚠️ limite connue

---

## Où on en est

| Domaine | État |
|---|---|
| Lecture (web, Android, Android Auto, bureau) | ✅ bibliothèque, recherche, playlists, favoris, transcodage à la volée, lecture sur un autre appareil |
| Comptes | ✅ admin et utilisateurs, plusieurs serveurs et comptes dans l'appli Android, langue de l'interface par compte (fr, en) |
| Recherche | ✅ Elasticsearch (fautes de frappe, suggestions), avec repli automatique sur la base s'il tombe |
| Import YouTube | ✅ recherche, playlists, albums, chaînes, abonnements synchronisés, extension de navigateur avec clés API |
| Tâches | ✅ volet « Tâches » dans l'admin : files, suites de tâches, journal en direct, annulation et relance, rescan AcoustID |
| Applis tierces | ✅ API Subsonic / OpenSubsonic (`/rest/*`) : Symfonium, Feishin, DSub, Substreamer, Tempo… |
| Paroles | ✅ synchronisées dans le lecteur : fichier `.lrc` à côté du morceau, sinon LRCLIB (aussi par Subsonic) |
| Déploiement | ✅ Docker Compose sur le homelab (SonNuage + Elasticsearch), CI GitLab avec runner dédié, APK Android signé |

## Livré récemment

### Radios thématiques, favoris et notes (novembre 2026)

- **Radios** sur l'accueil et dans Découvrir : saisons et fêtes (Halloween, Noël, Réveillon, Saint-Valentin,
  printemps, été, automne, hiver, chacune sur sa période), humeurs (Pop bonne humeur, Détente, Plein d'énergie,
  Soirée, Mélancolie, Concentration) et radio « Météo du moment » selon le temps qu'il fait dans la ville choisie (Open-Meteo, sans clé :
  seules les coordonnées de la ville partent). Chaque morceau est noté selon son titre, son genre, ses tags
  MusicBrainz (récupérés une fois par album, rafraîchis tous les 90 jours) et son analyse audio. Une radio ne
  s'affiche qu'avec au moins 15 morceaux possibles ; les notes de 1 ou 2 l'écartent, les favoris passent plus
  souvent, jamais deux fois de suite le même artiste. La file se complète tant qu'on écoute.
- Ville de la radio météo : admin → Réglages YouTube → Radios.
- **Analyse audio** (Essentia, sur le serveur, rien ne sort) : humeur (joyeux, triste, détendu, agressif, festif),
  dansant, acoustique ou électronique, voix ou instrumental, énergie, valence et tempo. Environ 7 s par morceau, par
  lots de 50, un seul à la fois ; relancée après chaque scan. Volet dans Réglages (activer, avancement, lancer).
  Image amd64 seulement (environ 800 Mo de plus) ; ailleurs l'analyse se désactive et les radios d'humeur s'appuient
  sur le titre, le genre et les tags. Modèles Essentia sous licence CC BY-NC-SA 4.0.
- Applis Subsonic : chaque radio est une playlist en lecture seule « Radio … », retirée à chaque ouverture.
- Android : radios dans l'appli et dossier « Radios » dans Android Auto.
- **Favoris d'albums et d'artistes** (cœur sur leur page, sections dans Favoris) et **notes de 1 à 5 étoiles** sur
  les morceaux, albums et artistes (aussi sous le titre dans le lecteur plein écran). Partagés avec les applis Subsonic.

### Subsonic, deuxième vague (novembre 2026)

- Mix : `getSimilarSongs2` / `getSimilarSongs` à partir d'un titre, d'un album ou d'un artiste, avec le moteur de
  la radio de découverte.
- File de lecture partagée entre appareils : `savePlayQueue` / `getPlayQueue`.
- `lastModified` réel sur `getIndexes` (fin du dernier scan) : les clients ne relisent plus tout pour rien.
- Favoris d'albums et d'artistes, notes de 1 à 5 (`setRating`), listes « mieux notés » et « favoris » complètes.
- `getArtistInfo2` : identifiant MusicBrainz et artistes proches de la bibliothèque selon ListenBrainz (pas de
  biographie ni de photo).
- `getNowPlaying` : ce qu'écoutent les comptes dans leurs applis Subsonic, effacé à la fin du titre.

### Paroles (novembre 2026)

- Bouton « Paroles » dans le lecteur plein écran (et dans la barre du lecteur sur ordinateur) : la ligne chantée
  est mise en avant et reste au milieu, un appui sur une ligne y saute. Texte brut quand rien n'est daté.
- Un fichier `.lrc` posé à côté du fichier audio passe en premier. Sinon LRCLIB (base libre, sans compte),
  recherche exacte puis au plus près de la durée. Réponses gardées dans `<DataPath>/lyrics`, une absence
  redemandée après une semaine.
- Applis Subsonic : `getLyricsBySongId` (extension OpenSubsonic `songLyrics`) et `getLyrics`.

### Imports YouTube rangés par identifiant (novembre 2026)

- Un import va dans `uploads/YouTube/<id>/<id>.opus`, avec sa pochette. Le titre d'une vidéo faisait le nom du
  dossier : des caractères invisibles de 3 octets dépassaient la limite de 255 octets de Linux et l'import échouait.
- Les imports déjà faits gardent leur place. Ils migrent à la prochaine correction de leurs métadonnées.
- Les noms tirés des tags (envoi de fichiers) sont désormais bornés en octets.

### Appli de bureau (novembre 2026)

- **SonNuage pour Windows, macOS et Linux** (dépôt `sonnuage-desktop`), en .NET avec Photino : les pages de
  l'appli web dans la webview du système, sans Chromium embarqué (environ 40 Mo, runtime compris). Plusieurs serveurs et comptes, comme sur Android.
- **Icône de la barre système** : fermer la fenêtre la cache sans couper la musique. Menu lecture/pause, suivant,
  précédent, Quitter. Touches média par MediaSession.
- **Paquets** produits par la CI du dépôt à chaque build de `main` : `.exe` autonome, `SonNuage.app`, archive Linux.
- Le lecteur web (`WebPlayer`) rejoint `SonNuage.UI`, partagé par le navigateur et l'appli de bureau. Sa file est
  rangée par compte.

### Recherche, extension, chaînes YouTube (fin octobre 2026)

- **Recherche dans l'en-tête** de l'appli web : un champ toujours visible (raccourci `/`) ouvre les résultats
  par-dessus la page. Une croix en haut à droite ou Échap referme. La page Recherche n'affiche plus la liste
  de suggestions du navigateur qui doublait les résultats.
- **Extension de navigateur** : le bouton flottant posé sur YouTube laisse la place à un menu qui s'ouvre
  depuis l'icône de l'extension. Selon la page, il propose d'importer la vidéo, de synchroniser la playlist ou
  l'album, ou toute la chaîne. Il montre aussi l'état du serveur et l'envoi des cookies.
- **Synchronisation d'une chaîne complète.** yt-dlp ne lit, à la racine d'une chaîne, que les onglets Vidéos
  et Shorts. Les albums n'arrivaient donc que par leurs clips : 4 titres de « Perdu d'avance » sur 14 pour
  OrelSan. Désormais, une chaîne qui a un onglet **Sorties** est lue album par album (albums, EP, singles),
  avec le nom de l'album. Une chaîne sans discographie reste lue par ses vidéos.
- **Administration réorganisée** : on y entre par la pastille du compte (en bas du menu). Le menu de gauche
  devient celui de l'administration, en deux groupes, Musique (YouTube, abonnements, envoi de fichiers, suivi des
  imports, métadonnées) et Serveur (tâches, utilisateurs, clés d'API, réglages), avec « Retour à la musique » en
  tête. Fini les deux niveaux d'onglets. La vue d'ensemble commence par ce qui demande une action. L'analyse
  de la bibliothèque et le cache rejoignent les Réglages. Création de tâche et d'utilisateur en fenêtre.
- **Menu du compte** : thème clair, sombre ou celui du système (par appareil) et langue de l'interface.
- **Mises à jour du client web** : l'onglet ouvert propose de recharger après un déploiement, sans couper la
  musique. Les fichiers de l'interface portent leur version dans l'adresse (le cache de Cloudflare ne sert plus
  l'ancienne interface).
- **Rescan AcoustID** : seuil de correction automatique réglable pour chaque tâche.
- **Bibliothèque** : purge horaire des albums vides, artistes sans album masqués, réparation automatique des
  fichiers Ogg/Opus dont les tags écrits par TagLib étaient illisibles.

### Gestionnaire de tâches (octobre 2026)

Tout traitement long passe désormais par une file de tâches, visible dans **Admin → Tâches** :

- **Suites de tâches.** Un import YouTube est une suite de trois étapes :
  téléchargement → empreinte AcoustID → métadonnées, tags et rangement. Chaque étape démarre dès que la
  précédente a réussi. Si une étape échoue, la suite s'arrête et l'import passe en échec (relançable).
- **Files avec plafond.** Chaque type de tâche appartient à une file dont la capacité est bornée :

  | File | Tâches simultanées | Pourquoi |
  |---|---|---|
  | Téléchargements | 1 à 6 (réglage YouTube) | une vidéo bridée par YouTube ne bloque plus les autres |
  | AcoustID | **1, toujours** | AcoustID tolère 3 requêtes/s. On s'en tient à 1/s, imports et rescans confondus |
  | Métadonnées | 1 | MusicBrainz impose une requête par seconde |
  | Bibliothèque | 1 | préparation des gros traitements |

  En plus de la file, le client AcoustID espace lui-même ses requêtes d'au moins une seconde : même un appel
  hors file ne peut pas dépasser la limite.
- **Priorités.** Les imports YouTube passent devant un rescan de la bibliothèque dans les files partagées.
- **Journal en direct** de chaque tâche, paramètres et résultat consultables, annulation, relance.
- **Robustesse.** La file vit dans la base. Une tâche en cours porte un « bail » renouvelé toutes les
  2 secondes. Si le serveur s'arrête, ses tâches reprennent au redémarrage. Plusieurs instances du serveur
  peuvent consommer la même file : la réservation d'une tâche et le respect des plafonds se font en une seule
  requête SQL atomique.
- **Rescan AcoustID** de toute la bibliothèque ou d'un artiste : empreinte de chaque morceau, comparaison
  avec MusicBrainz, puis correction automatique si la correspondance est sûre, ou simple proposition dans le
  journal. Prudent par construction :
  - titre et artiste corrigés seulement au-dessus du seuil de confiance réglé dans YouTube
  - album, numéros de piste et année seulement si l'édition trouvée est le même album
  - genre seulement s'il manque
  - jamais de modification d'un morceau édité à la main.
- Les suites terminées sont effacées après 14 jours (`SonNuage__Jobs__RetentionDays`).

### Lenteur des imports YouTube : diagnostic et correction

Constat sur le homelab : certains imports prenaient plus de 3 minutes, là où TonTuyau télécharge environ
6 Mo/s.

- **Cause.** YouTube bride parfois une vidéo à la vitesse de lecture : « Plus rien » a pris 224 s pour 210 s
  d'audio. La file faisait un téléchargement à la fois, si bien qu'une seule vidéo bridée bloquait tout
  derrière elle. Hors bridage, un import complet prenait 8 à 40 s, dont environ 3 s de téléchargement.
- **Correction.**
  - Plusieurs téléchargements en parallèle (3 par défaut, jusqu'à 6).
  - yt-dlp relance une connexion qui tombe sous 100 Kio/s (`--throttled-rate`).
  - Les métadonnées se traitent à part, à mesure que les fichiers arrivent.
  - L'ensemble tourne désormais sur le gestionnaire de tâches.

## Prochaines étapes

**Demandes du 8 novembre 2026** :

- 🟡 **Android Auto : SonNuage absent de la liste des applis de la voiture.** Le manifeste est correct (service
  MediaLibraryService exporté, `automotive_app_desc`) : Android Auto masque les applis multimédias qui ne viennent pas
  du Play Store. Côté téléphone : Android Auto → toucher 10 fois « Version » → menu ⋮ → Paramètres pour les
  développeurs → cocher « Sources inconnues », puis cocher SonNuage dans « Personnaliser le lanceur ». La reprise
  automatique à la connexion en dépend. Solution durable : publication sur le Play Store (piste de test interne).
- ✅ **Fondu enchaîné** (web, bureau, Android) : 0 à 5 s par pas de 0,1 s dans Réglages, courbe à puissance constante
  sur deux éléments audio. Android (1.3.0) : un second ExoPlayer joue la fin de la piste qui s'éteint pendant que le
  lecteur de la session (notification, Android Auto) part sur la suivante.
- ✅ **Page artiste** : ses 10 titres les plus écoutés, « Voir tout (n) » / « Voir moins », puis la discographie.
- ✅ **Lecture infinie** (web, bureau, Android) : option dans Réglages, avance réglable de 1 à 10 (3 par défaut).
  Le serveur (`POST /api/radio`) choisit au hasard, par cercles : même genre que les derniers morceaux joués, puis
  même artiste, puis le reste de la bibliothèque, sans jamais reprendre un morceau déjà dans la file. Désactivée
  quand la répétition est active.
- ✅ **Dépôt iOS** `sonnuage-ios` ajouté aux listes de dépôts (README, site).
- ✅ **Réglages de lecture liés au compte** (1.4.0) : fondu, lecture infinie et avance sont enregistrés sur le
  serveur (`/api/me/playback`) et suivent le compte sur tous ses appareils, avec une copie locale pour le hors-ligne.
  Qualité et pré-chargement restent propres à chaque appareil.

1. 🔜 **Valider le nouveau pipeline en conditions réelles** sur le homelab : un gros import (une discographie),
   puis un rescan AcoustID d'un artiste. Regarder les durées par étape dans le volet Tâches.
2. 🔜 **Android Auto, retours du premier essai en voiture** (novembre 2026). La lecture et le lecteur partagé
   avec le GPS fonctionnent. À reprendre :
   - **Démarrage à la connexion** : Android Auto a relancé Apple Music, inutilisé depuis des mois, au lieu de
     SonNuage. Il a fallu lancer la lecture depuis le téléphone. La reprise à froid (0.1.0-ci.14 :
     `MediaButtonReceiver`, racine « récents ») n'a pas pu être validée. Regarder comment Android Auto choisit
     l'appli à relancer, au-delà du dernier récepteur du bouton média.
   - **Logo absent** dans Android Auto (liste des applis et lecteur) : icône de la session et du service à vérifier.
   - **Délai du changement de titre** entre deux morceaux : correctif livré en 0.1.0-ci.14, à confirmer.
   - **Recherche et navigation** dans Android Auto : pas encore testées.
   - Bascule entre comptes et migration depuis l'ancienne version : testées en émulateur seulement.
3. ✅ **Identifiants MusicBrainz sur les morceaux et les albums** : lus dans les tags au scan, posés par une
   correspondance AcoustID acceptée. Le rescan AcoustID saute les morceaux identifiés (case « Inclure les morceaux
   identifiés » pour forcer). L'éditeur de bibliothèque lie un album à sa fiche (`PUT /api/admin/albums/{id}/musicbrainz`).
4. ✅ **Tags réécrits dans `uploads/`** après une correction (édition, rescan, lien MusicBrainz), avec TagLib, par un
   fichier temporaire renommé ensuite : le flux audio n'est pas touché. `music/` reste en lecture seule, seule la
   base change pour ses fichiers.
5. ✅ **Featurings rattachés à l'artiste principal** : « OrelSan feat. Damso » (feat., ft., featuring ; « & », « x »,
   « avec », « , » seulement quand la première partie est l'artiste de l'album) appartient à OrelSan, Damso est
   un invité. La page artiste a une section « Apparaît sur » (`GET /api/artists/{id}/appearances`). Chaque scan
   complet recalcule les crédits : les données existantes se rangent au premier scan.
6. 🔜 **Appli de bureau, suites** :
   - essai réel sous macOS et Linux (seul Windows a été lancé) : icône, fenêtre cachée, touches média
   - pré-chargement des pistes suivantes : il passe par `fetch` depuis la page, bloqué faute d'en-têtes CORS sur
     le flux. Autoriser l'origine de l'appli sur `/api/stream` ou télécharger côté .NET.
   - une seule instance (relancer l'appli rouvre la fenêtre), démarrage avec la session, option « démarrer caché »
   - signature et notarisation macOS, signature Windows, installeurs (MSI, DMG, AppImage ou Flatpak)
   - mises à jour automatiques.
7. ✅ **Autres traitements en tâches** : scan complet, réindexation Elasticsearch (bouton « Réindexer la recherche »),
   synchronisation de chaque abonnement (voie `sync`) et purge du cache de transcodage passent par le moteur de
   tâches, avec journal, annulation, nouvelles tentatives et historique dans le volet Tâches.
8. ✅ (1.16.0) **API Subsonic / OpenSubsonic**, en plus de l'API actuelle (`/api/*` reste inchangée, les clients SonNuage
   continuent de l'utiliser). Ouvre l'appli à tout l'écosystème : Symfonium, Feishin, Substreamer, DSub, Tempo…
   y compris leur Android Auto. Estimation : 2 à 3 sessions, dont une bonne part d'essais avec de vrais clients.
   - **Couche de traduction** sous `/rest/*` (avec et sans suffixe `.view`), réponses XML et JSON (`f=json`),
     branchée sur les services existants : bibliothèque, transcodage, playlists, favoris, historique, pochettes.
   - **Authentification.** Subsonic envoie `u` + `t = md5(mot de passe + s)` (ou `p` en clair) : le serveur doit
     connaître un secret réversible, or le mot de passe du compte n'est stocké que haché. D'où un **mot de passe
     d'application Subsonic** par compte, généré et révocable dans les Réglages, stocké chiffré (Data Protection).
     Accepter aussi la clé `apiKey` de l'extension OpenSubsonic. Mêmes limitations de tentatives que la connexion.
   - **Endpoints visés** (environ 25) : `ping`, `getLicense`, `getOpenSubsonicExtensions`, `getMusicFolders`,
     `getIndexes`, `getArtists`, `getArtist`, `getAlbum`, `getSong`, `getAlbumList2`, `getRandomSongs`,
     `getGenres`, `getSongsByGenre`, `search3`, `getPlaylists`, `getPlaylist`, `createPlaylist`, `updatePlaylist`,
     `deletePlaylist`, `star`, `unstar`, `getStarred2`, `stream` (`maxBitRate`, `format`), `download`,
     `getCoverArt` (`size`), `scrobble`, `getUser`. Le reste (podcasts, radios, partages, `getSimilarSongs`…)
     répond vide mais valide.
   - **Identifiants** stables préfixés par type (`ar-`, `al-`, `tr-`, `pl-`) ; flux protégés comme aujourd'hui,
     jamais mis en cache par Cloudflare.
   - **Tests** : contrat XML et JSON vérifié par des tests d'intégration, puis essais avec Symfonium et Feishin.
   - Derrière Cloudflare Access : autoriser `/rest/*` comme `/api/extension/*` (les clients tiers ne savent pas
     passer Access).
   - Une fois livré : passer la ligne « Compatible API Subsonic » du comparatif de `sonnuage-site` à ✓.
   - **Livré** : mot de passe Subsonic par compte dans Réglages → Applis Subsonic (chiffré, relisible, révocable,
     sert aussi de clé `apiKey`), environ 45 méthodes sous `/rest/*`, XML, JSON et JSONP, GET et formulaire POST.
     Depuis : mix, file partagée, favoris d'albums et d'artistes, notes, infos d'artiste, en cours d'écoute.
     Reste : essais avec Symfonium et Feishin, et autoriser `/rest/*` dans
     Cloudflare Access si le domaine y passe.

## Découverte (« discovery »)

Demandé le 8 novembre 2026. Faire découvrir de la musique, dans la bibliothèque puis au-delà. Trois couches, de la
plus simple (données locales) à la plus ouverte (bases publiques), sur un même socle.

**Socle (préalable à tout le reste)**

- 💡 **Historique d'écoute par utilisateur** : titre, début, durée écoutée, écoute complète ou passée, appareil.
  Envoyé par chaque appli (web, Android, ordinateur), mis en file hors ligne comme les réglages de lecture.
- ✅ Identifiants MusicBrainz sur les morceaux et les albums (étape 3 ci-dessus) : la clé commune avec les bases
  publiques. Reste à poser l'identifiant MusicBrainz de l'**artiste**.
- 💡 **Cache des données externes** par identifiant MusicBrainz, avec date d'expiration, et appels passés par le
  gestionnaire de tâches (file « Métadonnées », une requête par seconde pour MusicBrainz).

**Couche 1 : dans la bibliothèque (aucune donnée externe)**

- ✅ « À réécouter » sur l'accueil : titres écoutés au moins deux fois ou likés il y a plus de 30 jours, pas joués
  depuis 30 jours.
- ✅ « Pas encore écouté » sur l'accueil : albums dont l'utilisateur n'a lancé aucun titre, les plus récents d'abord.
- 💡 Co-écoutes entre comptes du serveur (« ceux qui écoutent X écoutent aussi Y »), sur accord de chaque compte.
- La lecture infinie (même genre, puis même artiste) en profitera : elle pourra puiser dans ces listes.

**Couche 2 : nouveautés des artistes suivis**

- 💡 Sorties récentes et à venir des artistes de la bibliothèque, via MusicBrainz (groupes de parutions par
  artiste et date) ou l'API « fresh releases » de ListenBrainz, filtrée sur nos artistes.
- 💡 Les nouvelles vidéos des chaînes YouTube synchronisées, déjà disponibles côté serveur.
- Action proposée : « Importer » (administrateur, via l'import YouTube existant) ou « Demander à l'admin »
  pour les autres comptes.

**Couche 3 : artistes et titres à découvrir hors de la bibliothèque**

- 💡 Artistes proches de ceux qu'on écoute le plus, depuis les bases publiques ci-dessous, avec leurs titres phares.
- 💡 Fiche découverte : biographie, genres, albums (MusicBrainz, Wikidata), écoute d'un extrait, puis import.
- 💡 ListenBrainz sur deux niveaux (validés le 8 novembre 2026) :
  1. sans rien envoyer : artistes proches et nouveautés demandés à partir de la bibliothèque ; aucune écoute ne
     quitte le serveur ;
  2. facultatif, par compte : l'utilisateur saisit son jeton ListenBrainz dans Réglages et ses écoutes y sont
     envoyées (désactivé par défaut), en échange de recommandations personnelles.

**Découverte par artiste** (demandé le 9 novembre 2026)

- ✅ (1.10.0) **Fiches d'artistes à découvrir, stockées en base** (page « Découvrir ») : 24 artistes proches des
  plus écoutés de la bibliothèque (ListenBrainz, identifiants MusicBrainz), avec « Proche de … » et 3 titres phares
  chacun (ListenBrainz si un jeton `SonNuage:ListenBrainzToken` est fourni, sinon Deezer, sans compte), retrouvés
  sur YouTube par la recherche par correspondance. Tâche de fond « discovery.refresh » toutes les 6 heures, incrémentale
  (1.12.0) : 6 nouveaux artistes à chaque passage (au moins 24 affichés), les plus récents en haut avec un badge
  « Nouveau », retirés au bout de 7 jours sans être écartés pour la suite ; ou bouton « Actualiser » (admin). Tout est
  réglable dans Réglages → Découverte (1.13.0) : fréquence, nouveaux artistes, minimum affiché, durée, titres par artiste. Reste : albums des artistes proposés.
- ✅ (1.10.0) **Extraits pré-téléchargés** pour ces titres phares, dans un dossier à part (`var/discovery/previews`,
  distinct du cache des extraits à la demande) : la page se lit tout de suite, sans attendre yt-dlp. Préparés
  par la même tâche, quelques-uns à la fois, et gardés tant que l'artiste reste proposé.
- ✅ (1.9.0) **Place allouée aux extraits**, réglable dans Réglages → Stockage (2 Go par défaut) : au-delà, les
  extraits les plus anciens sont supprimés. Les extraits pré-téléchargés de la découverte comptent dedans
  (1.10.0) ; les extraits à la demande partent en premier.
- ✅ (1.9.0) **Espace libre minimum sur le disque**, réglable dans Réglages → Stockage (5 Go par défaut) : en
  dessous, **tout téléchargement attend** (imports YouTube reportés de 10 minutes sans compter d'essai, extraits
  refusés), avec une alerte sur le tableau de bord. Les téléchargements reprennent seuls quand la place revient.
- ✅ (1.10.0) Une demande depuis une fiche passe par la recherche par correspondance ci-dessous, puis par le circuit
  habituel des demandes (quota, approbation).

**Bases publiques retenues**

| Base | Apporte | Accès | Verdict |
|------|---------|-------|---------|
| MusicBrainz | discographies, dates de sortie, relations entre artistes, genres | libre (CC0), 1 req/s | socle, déjà utilisé |
| ListenBrainz | artistes et titres similaires, nouveautés, recommandations perso | libre, identifiants MusicBrainz ; titres phares sous jeton depuis fin 2026 | source principale |
| Deezer | titres phares d'un artiste (classement de popularité) | API publique sans compte, 50 req / 5 s | relais des titres phares |
| Last.fm | similarité très fournie (artistes, titres), tags | clé d'API gratuite, usage non commercial | complément facultatif (clé fournie par l'admin) |
| Wikidata | influences, genres, liens | libre (CC0) | enrichissement des fiches |
| Discogs | styles fins, labels | clé d'API, quotas | plus tard, si besoin |
| Spotify | (similaires, recommandations) | fermé aux nouvelles applis depuis novembre 2024 | écarté |
| AcousticBrainz | (similarité sonore) | arrêté en 2022, archives seules | écarté |

**Ordre proposé** : historique d'écoute → « À réécouter » et « Pas encore écouté » → nouveautés des artistes
(MusicBrainz) → artistes proches (ListenBrainz, Last.fm en complément) → envoi des écoutes à ListenBrainz.

**Qui voit quoi** (décidé le 9 novembre 2026) : un utilisateur simple ne voit que ce qui est déjà sur le serveur
(couche 1, et les couches 2 et 3 limitées aux titres présents dans la bibliothèque). Les propositions hors de la
bibliothèque, avec leur bouton de demande, sont réservées aux contributeurs et aux administrateurs.

**Place dans l'appli** : rubriques de l'accueil (« Nouveautés de vos artistes », « À découvrir », « À réécouter »),
une page Découvrir, et un onglet dans Android Auto.

**Questions ouvertes** : confidentialité entre comptes d'un même serveur (co-écoutes). Import et extrait avant
import : voir « Demandes de téléchargement » ci-dessous.

## Demandes de téléchargement (à développer après le socle de la découverte)

Demandé le 8 novembre 2026. Permettre à d'autres comptes que l'administrateur de faire entrer de la musique, sous
contrôle, et écouter un extrait avant de télécharger le morceau entier.

**Rôle et approbation**

- ✅ Nouveau rôle **Contributeur**, entre Utilisateur et Administrateur : il peut créer des demandes de
  téléchargement. Un utilisateur simple ne peut rien demander (décidé le 9 novembre 2026).
- ✅ Par compte, réglage réservé à l'administrateur : **« Demandes approuvées automatiquement »**. Désactivé :
  chaque demande attend l'accord d'un admin. Activé : elle part directement au téléchargement.
- ✅ **Quota par contributeur**, réglable par l'administrateur pour chaque compte : **100 par jour par défaut**
  (24 heures glissantes). Au-delà, la demande est refusée avec un message clair.
- ✅ Statuts d'une demande : **demandée → acceptée ou refusée → en cours → terminée** (refus avec motif ; échec
  du téléchargement signalé). L'auteur suit l'état de ses demandes.
- ✅ Tableau de bord d'administration : encart **« Dernières demandes »** avec les 50 plus récentes et un lien
  **« Voir plus »** vers la liste complète.
- ✅ Page **Demandes** (liste complète) : recherche (titre, chaîne, auteur), filtre par statut ; approuver, refuser
  avec motif. Côté contributeur : recherche YouTube, bouton « Demander », suivi de ses demandes et de son quota.
- 💡 Filtres par date, corriger la correspondance choisie.
- ✅ Garde-fous : refus des doublons (déjà dans la bibliothèque ou déjà demandé), qui a demandé et qui a tranché
  reste affiché sur chaque demande (les demandes approuvées automatiquement sont marquées comme telles).

**Recherche YouTube par correspondance**

- 💡 Point de départ : une fiche de la découverte (album ou titre MusicBrainz), une recherche sans résultat dans
  la bibliothèque, ou un lien envoyé par l'extension.
- ✅ (1.9.0) Mode **« Artiste et titre »** de la page Demandes : le serveur cherche sur YouTube Music et YouTube
  en parallèle et note chaque candidat sur 100 : mots du titre retrouvés, artiste dans la chaîne ou le titre,
  chaîne « - Topic » favorisée, durée proche de celle de MusicBrainz (cherchée si on ne la donne pas), versions
  live, remix, karaoké ou reprise écartées si non demandées.
- ✅ Correspondance sûre (80 et plus) : mise en avant comme « Meilleure correspondance ». Douteuse : les
  candidats sont proposés au choix, avec leur score, l'extrait et le lien YouTube.
- Un album se traite morceau par morceau (liste des titres MusicBrainz), avec un récapitulatif des titres non
  trouvés.

**Extrait avant téléchargement**

- ✅ Bouton **« Extrait »** (1.8.0) sur les résultats de la page Demandes et sur les demandes pas encore
  téléchargées (l'admin écoute avant d'accepter). yt-dlp ne télécharge que 30 secondes, vers le tiers du morceau
  (`--download-sections`), converties en MP3 ; rien n'est ajouté à la bibliothèque. Un seul extrait à la fois, dans
  une balise audio à part : il met le lecteur en pause et s'arrête si la lecture reprend.
- ✅ Extraits en cache disque 7 jours (500 au plus), produits une fois par vidéo, deux à la fois sur le serveur.
- 💡 Limite d'extraits par compte, si le besoin apparaît (pour l'instant réservés aux contributeurs et admins).
- 💡 Message d'échec détaillé (vidéo bloquée, réservée aux majeurs…) : la balise audio ne transmet pas le texte
  de l'erreur, l'interface affiche un message générique et le détail reste dans le journal du serveur.

**Ce qui ne change pas (sécurité)**

- Seuls les hôtes YouTube vont à yt-dlp. Les cookies et identifiants YouTube restent sur le serveur : les
  applis ne reçoivent que l'audio de l'extrait, jamais l'adresse de la vidéo source ni de jeton.
- Le téléchargement lui-même reste une tâche du serveur (gestionnaire de tâches existant), lancée par
  l'approbation, automatique ou non.

**Ordre proposé** : rôle Contributeur, quota et approbation automatique par compte → statuts, encart « Dernières
demandes » et page Demandes → recherche YouTube par correspondance → extrait avant téléchargement → demandes
depuis la découverte (couches 2 et 3).

## Idées

- 💡 Tâches planifiées (« rescan AcoustID chaque dimanche à 3 h »).
- 💡 Rafraîchissement du volet Tâches en push (Server-Sent Events) au lieu d'une interrogation chaque seconde.
- 💡 Détection des doublons de la bibliothèque grâce aux empreintes AcoustID (même enregistrement, deux fichiers).
- 💡 Revue groupée des propositions d'un rescan (accepter ou refuser en lot au lieu de lire le journal).
- 💡 Sauvegarde automatique de la base SQLite (copie à chaud, rotation).
- 💡 Recherche sémantique (« musique calme pour travailler ») via le modèle d'inférence d'Elasticsearch,
  déjà prévu dans la configuration (`SonNuage__Search__SemanticInferenceId`).

## Limites connues

- ⚠️ **Débit AcoustID** : une requête par seconde et une empreinte à la fois, soit environ 2 s par morceau.
  Une bibliothèque de 10 000 titres demande environ 6 h de rescan. C'est voulu, pour respecter AcoustID.
- ⚠️ **Plusieurs consommateurs** : ils partagent la base SQLite, donc ils doivent tourner sur la même machine
  (même disque). Répartir sur plusieurs machines demanderait de passer à PostgreSQL.
- ⚠️ **Corrections d'un rescan dans `music/`** : elles ne vivent que dans la base. Si le fichier change sur
  disque (retag par un autre outil), le scan suivant relit ses tags et la correction est perdue.
  Une édition à la main dans l'admin, elle, verrouille le morceau.
- ⚠️ **Chaînes déjà synchronisées** : les clips et vidéos importés avant la lecture de l'onglet Sorties restent
  dans la bibliothèque, à côté des titres d'album. On peut les supprimer depuis l'import YouTube.
- ⚠️ **Bridage YouTube** : il reste possible, mais une vidéo bridée n'occupe plus qu'une place de
  téléchargement sur trois.
