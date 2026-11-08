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
| Déploiement | ✅ Docker Compose sur le homelab (SonNuage + Elasticsearch), CI GitLab avec runner dédié, APK Android signé |

## Livré récemment

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

**Demandes du 8 novembre 2026** (en cours) :

- 🔜 **Android Auto : SonNuage absent de la liste des applis de la voiture.** Le son ne sort que si la lecture est
  lancée depuis le téléphone, d'où l'absence de reprise automatique. Rejoint le point « Démarrage à la connexion »
  ci-dessous : l'appli doit être déclarée et découverte comme appli multimédia d'Android Auto.
- 🔜 **Fondu enchaîné** entre deux morceaux : durée réglable de 0 à 5 s par pas de 0,1 s (0 = désactivé).
- 🔜 **Page artiste** : une dizaine de ses titres, un lien « Voir tout », puis les pochettes de tous ses albums.
- 🔜 **Lecture infinie** : arrivé près de la fin de la file, ajouter des morceaux d'un genre proche. Avance réglable
  (3 par défaut) : avec 5 morceaux et une avance de 3, le 6e est choisi en arrivant au 3e, et ainsi de suite.
- ✅ **Dépôt iOS** `sonnuage-ios` ajouté aux listes de dépôts (README, site).

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
3. 🔜 **Stocker les identifiants MusicBrainz sur les morceaux** (enregistrement, édition). Un rescan suivant
   pourrait alors sauter les morceaux déjà identifiés, et l'admin pourrait lier un album à sa fiche.
4. 🔜 **Réécrire les tags des fichiers d'`uploads/`** quand un rescan les corrige. Les fichiers de `music/`
   restent en lecture seule. Seule la base change pour eux.
5. 🔜 **Rattacher les featurings à l'artiste principal** : un titre crédité « OrelSan feat. Damso » crée
   aujourd'hui un artiste à part (masqué des listes car sans album). Le rattacher à « OrelSan » le ferait
   apparaître dans sa page, et chaque invité pourrait avoir sa liste « Apparaît sur ».
6. 🔜 **Appli de bureau, suites** :
   - essai réel sous macOS et Linux (seul Windows a été lancé) : icône, fenêtre cachée, touches média
   - pré-chargement des pistes suivantes : il passe par `fetch` depuis la page, bloqué faute d'en-têtes CORS sur
     le flux. Autoriser l'origine de l'appli sur `/api/stream` ou télécharger côté .NET.
   - une seule instance (relancer l'appli rouvre la fenêtre), démarrage avec la session, option « démarrer caché »
   - signature et notarisation macOS, signature Windows, installeurs (MSI, DMG, AppImage ou Flatpak)
   - mises à jour automatiques.
7. 🔜 **Autres traitements en tâches** : scan complet de la bibliothèque, réindexation Elasticsearch,
   synchronisation des abonnements, purge du cache de transcodage. Ils gagneraient le journal, l'annulation
   et l'historique.
8. 🔜 **API Subsonic / OpenSubsonic**, en plus de l'API actuelle (`/api/*` reste inchangée, les clients SonNuage
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

## Découverte et recommandations (à cadrer)

Deux besoins distincts, à concevoir ensemble plus tard :

- 💡 **Nouveautés (« discovery »)** : ce qui vient d'arriver ou de sortir, chez les artistes qu'on écoute.
  Pistes :
  - une rubrique « Nouveautés » de l'accueil, alimentée par les abonnements YouTube synchronisés
  - les sorties récentes des artistes de la bibliothèque via MusicBrainz, avec proposition d'import
  - les ajouts récents à la bibliothèque, par utilisateur (ce qu'il n'a pas encore écouté).
- 💡 **Recommandations fondées sur les écoutes** : « parce que vous avez écouté… ».
  Préalable : enregistrer un historique d'écoute par utilisateur (titre, date, écoute complète ou passée).
  Pistes, du plus simple au plus riche :
  - artistes et albums les plus écoutés, « à réécouter »
  - co-écoutes entre utilisateurs du serveur
  - artistes proches via MusicBrainz ou ListenBrainz (envoi des écoutes facultatif)
  - similarité sonore ou sémantique via Elasticsearch.
- Questions ouvertes : confidentialité entre comptes d'un même serveur, place dans l'appli (accueil,
  Android Auto), recommandations limitées à la bibliothèque ou ouvertes à YouTube (import proposé).

## Idées

- 💡 Tâches planifiées (« rescan AcoustID chaque dimanche à 3 h »).
- 💡 Rafraîchissement du volet Tâches en push (Server-Sent Events) au lieu d'une interrogation chaque seconde.
- 💡 Détection des doublons de la bibliothèque grâce aux empreintes AcoustID (même enregistrement, deux fichiers).
- 💡 Revue groupée des propositions d'un rescan (accepter ou refuser en lot au lieu de lire le journal).
- 💡 Sauvegarde automatique de la base SQLite (copie à chaud, rotation).
- 💡 Recherche sémantique (« musique calme pour travailler ») via le modèle d'inférence d'Elasticsearch,
  déjà prévu dans la configuration (`SonNuage__Search__SemanticInferenceId`).
- 💡 Paroles synchronisées (LRCLIB) dans le lecteur.

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
