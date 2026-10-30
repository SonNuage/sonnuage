# SonNuage : feuille de route

Ce document dit où en est le projet et ce qui vient ensuite. Il se lit sans connaître le code.
Les priorités sont indicatives : on les revoit à chaque livraison.

Légende : ✅ fait · 🔜 prochain · 💡 idée à creuser · ⚠️ limite connue

---

## Où on en est

| Domaine | État |
|---|---|
| Lecture (web, Android, Android Auto) | ✅ bibliothèque, recherche, playlists, favoris, transcodage à la volée, lecture sur un autre appareil |
| Comptes | ✅ admin et utilisateurs, plusieurs serveurs et comptes dans l'appli Android, langue de l'interface par compte (fr, en) |
| Recherche | ✅ Elasticsearch (fautes de frappe, suggestions), avec repli automatique sur la base s'il tombe |
| Import YouTube | ✅ recherche, playlists, albums, chaînes, abonnements synchronisés, extension de navigateur avec clés API |
| Tâches | ✅ volet « Tâches » dans l'admin : files, suites de tâches, journal en direct, annulation et relance, rescan AcoustID |
| Déploiement | ✅ Docker Compose sur le homelab (SonNuage + Elasticsearch), CI GitLab avec runner dédié, APK Android signé |

## Livré récemment

### Recherche, extension, chaînes YouTube (fin octobre 2026)

- **Recherche dans l'en-tête** de l'appli web : un champ toujours visible (raccourci `/`) ouvre les résultats
  par-dessus la page ; une croix en haut à droite ou Échap referme. La page Recherche n'affiche plus la liste
  de suggestions du navigateur qui doublait les résultats.
- **Extension de navigateur** : le bouton flottant posé sur YouTube laisse la place à un menu qui s'ouvre
  depuis l'icône de l'extension. Selon la page, il propose d'importer la vidéo, de synchroniser la playlist ou
  l'album, ou toute la chaîne ; il montre aussi l'état du serveur et l'envoi des cookies.
- **Synchronisation d'une chaîne complète.** yt-dlp ne lit, à la racine d'une chaîne, que les onglets Vidéos
  et Shorts. Les albums n'arrivaient donc que par leurs clips : 4 titres de « Perdu d'avance » sur 14 pour
  OrelSan. Désormais, une chaîne qui a un onglet **Sorties** est lue album par album (albums, EP, singles),
  avec le nom de l'album ; une chaîne sans discographie reste lue par ses vidéos.
- **Administration réorganisée** : on y entre par la pastille du compte (en bas du menu). Le menu de gauche
  devient celui de l'administration, en deux groupes, Musique (YouTube, abonnements, envoi de fichiers, suivi des
  imports, métadonnées) et Serveur (tâches, utilisateurs, clés d'API, réglages), avec « Retour à la musique » en
  tête. Fini les deux niveaux d'onglets. La vue d'ensemble commence par ce qui demande une action ; l'analyse
  de la bibliothèque et le cache rejoignent les Réglages ; création de tâche et d'utilisateur en fenêtre.
- **Menu du compte** : thème clair, sombre ou celui du système (par appareil) et langue de l'interface.
- **Mises à jour du client web** : l'onglet ouvert propose de recharger après un déploiement, sans couper la
  musique ; les fichiers de l'interface portent leur version dans l'adresse (le cache de Cloudflare ne sert plus
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
  | AcoustID | **1, toujours** | AcoustID tolère 3 requêtes/s ; on s'en tient à 1/s, imports et rescans confondus |
  | Métadonnées | 1 | MusicBrainz impose une requête par seconde |
  | Bibliothèque | 1 | préparation des gros traitements |

  En plus de la file, le client AcoustID espace lui-même ses requêtes d'au moins une seconde : même un appel
  hors file ne peut pas dépasser la limite.
- **Priorités.** Les imports YouTube passent devant un rescan de la bibliothèque dans les files partagées.
- **Journal en direct** de chaque tâche, paramètres et résultat consultables, annulation, relance.
- **Robustesse.** La file vit dans la base. Une tâche en cours porte un « bail » renouvelé toutes les
  2 secondes ; si le serveur s'arrête, ses tâches reprennent au redémarrage. Plusieurs instances du serveur
  peuvent consommer la même file : la réservation d'une tâche et le respect des plafonds se font en une seule
  requête SQL atomique.
- **Rescan AcoustID** de toute la bibliothèque ou d'un artiste : empreinte de chaque morceau, comparaison
  avec MusicBrainz, puis correction automatique si la correspondance est sûre, ou simple proposition dans le
  journal. Prudent par construction :
  - titre et artiste corrigés seulement au-dessus du seuil de confiance réglé dans YouTube ;
  - album, numéros de piste et année seulement si l'édition trouvée est le même album ;
  - genre seulement s'il manque ;
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

1. 🔜 **Valider le nouveau pipeline en conditions réelles** sur le homelab : un gros import (une discographie),
   puis un rescan AcoustID d'un artiste. Regarder les durées par étape dans le volet Tâches.
2. 🔜 **Application Android sur un vrai téléphone** : Android Auto, bascule entre comptes, migration depuis
   l'ancienne version. Ces points sont testés en émulateur seulement.
3. 🔜 **Stocker les identifiants MusicBrainz sur les morceaux** (enregistrement, édition). Un rescan suivant
   pourrait alors sauter les morceaux déjà identifiés, et l'admin pourrait lier un album à sa fiche.
4. 🔜 **Réécrire les tags des fichiers d'`uploads/`** quand un rescan les corrige. Les fichiers de `music/`
   restent en lecture seule ; seule la base change pour eux.
5. 🔜 **Rattacher les featurings à l'artiste principal** : un titre crédité « OrelSan feat. Damso » crée
   aujourd'hui un artiste à part (masqué des listes car sans album). Le rattacher à « OrelSan » le ferait
   apparaître dans sa page, et chaque invité pourrait avoir sa liste « Apparaît sur ».
6. 🔜 **Autres traitements en tâches** : scan complet de la bibliothèque, réindexation Elasticsearch,
   synchronisation des abonnements, purge du cache de transcodage. Ils gagneraient le journal, l'annulation
   et l'historique.

## Découverte et recommandations (à cadrer)

Deux besoins distincts, à concevoir ensemble plus tard :

- 💡 **Nouveautés (« discovery »)** : ce qui vient d'arriver ou de sortir, chez les artistes qu'on écoute.
  Pistes :
  - une rubrique « Nouveautés » de l'accueil, alimentée par les abonnements YouTube synchronisés ;
  - les sorties récentes des artistes de la bibliothèque via MusicBrainz, avec proposition d'import ;
  - les ajouts récents à la bibliothèque, par utilisateur (ce qu'il n'a pas encore écouté).
- 💡 **Recommandations fondées sur les écoutes** : « parce que vous avez écouté… ».
  Préalable : enregistrer un historique d'écoute par utilisateur (titre, date, écoute complète ou passée).
  Pistes, du plus simple au plus riche :
  - artistes et albums les plus écoutés, « à réécouter » ;
  - co-écoutes entre utilisateurs du serveur ;
  - artistes proches via MusicBrainz ou ListenBrainz (envoi des écoutes facultatif) ;
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
