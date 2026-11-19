# Rejoindre un Jam (Spotify)

Suivre un **Jam** Spotify (session d'écoute partagée) et jouer le même titre dans SonNuage, depuis la bibliothèque ou
un import YouTube. Pensé pour qu'un **compte Spotify gratuit** puisse se synchroniser avec l'hôte d'un Jam.

Le son de Spotify n'est **jamais** récupéré : seuls le **titre en cours** et la **position** sont lus. SonNuage joue sa
propre source. Aucun DRM n'est contourné.

> ⚠️ **Expérimental.** Pour lire l'état d'un Jam, on utilise l'API interne de Spotify (« social-connect » et
> « connect-state »), qui n'accepte que le client id de l'appli de bureau Spotify. C'est **hors des conditions
> d'utilisation de Spotify** et cela peut cesser de marcher sans préavis si Spotify change son API. L'API Web
> officielle, elle, est fermée aux nouvelles applis depuis novembre 2024 et ne donne pas accès aux Jams.

## Pour l'utilisateur

1. **Relier Spotify** : Réglages → « Rejoindre un Jam » → *Connecter Spotify* (ou le bouton « Rejoindre un Jam » de la
   barre latérale). Un onglet s'ouvre pour se connecter à Spotify, puis on revient à SonNuage.
2. **Rejoindre** : coller le lien d'invitation du Jam (ou le scanner), choisir le mode :
   - **Hôte seulement** : on suit le Jam sans jouer ici (le son reste sur l'appareil de l'hôte).
   - **Sur cet appareil aussi** : SonNuage joue le même titre ici, calé sur l'hôte.
3. Un bandeau au-dessus du lecteur montre l'hôte et le titre suivi (point coloré : le son joue ici ; gris : il reste
   chez l'hôte). *Quitter le Jam* arrête le suivi et nous retire du Jam côté Spotify.
4. Si l'hôte termine le Jam ou nous en retire, le bandeau disparaît et « Le Jam est terminé » s'affiche.

L'hôte doit avoir ajouté l'invité au Jam (lien de partage) ; un compte jamais invité est refusé par Spotify, et la
modale l'annonce tout de suite.

## Architecture

- **Jetons** (`User.Spotify*`, chiffrés Data Protection comme ListenBrainz) : obtenus sur l'appareil par OAuth PKCE
  (redirection en boucle locale `http://127.0.0.1:<port>/login`, client id de l'appli de bureau), puis confiés au
  serveur (`PUT /api/me/jam/spotify/token`). Ils ne reviennent jamais vers l'appli.
- **Suivi** (`JamHub` + `JamFollower`, serveur), dans cet ordre (validé en direct) :
  1. hôtes Spotify (`apresolve`) et infos de session (`social-connect/v2/sessions/info/{token}` : nom de l'hôte) ;
  2. flux **dealer** (websocket) pour obtenir un *connection id* ;
  3. appareil caché non lecteur (`PUT connect-state/v1/devices/hobs_{id}`) ;
  4. **puis** `POST social-connect/v2/sessions/join/{token}?playback_control=control&local_device_id=hobs_{id}`
     (`control` est permis aux comptes gratuits, `listen_and_control` exige Premium). Sans ce join après
     l'enregistrement, le dealer n'envoie plus rien.

  Le dealer pousse alors l'état de l'hôte (JSON `{"cluster":{"player_state":…}}`, gzip + base64) à chaque changement
  de titre, pause ou saut, avec `server_timestamp_ms` : l'écart d'horloge avec Spotify (temps de vol compris) est
  estimé comme NTP, au minimum observé. Le keepalive tourne à part (annuler un `ReceiveAsync` .NET coupe la socket).
- **Fin** : un message `social-connect/v2/session_update` de raison `SESSION_DELETED`, `YOU_LEFT`, `YOU_WERE_KICKED`
  ou `YOU_WERE_REMOVED_UNSUPPORTED_CONTENT` termine le suivi. Une connexion perdue est reprise (1, 5, 15, 30 s) ; après
  quatre échecs d'affilée, ou un join refusé, le suivi s'arrête aussi. Dans tous les cas l'état inactif est poussé aux
  appareils. Quitter appelle `POST social-connect/v3/sessions/{id}/leave`.
- **Préparation** : le titre en cours et les deux suivants de la file de l'hôte sont préparés d'avance — métadonnées
  par la page d'intégration (`open.spotify.com/embed/track/{id}`, l'API Web étant limitée pour ce jeton), bibliothèque
  (titre + artiste normalisés, la durée départage), sinon import YouTube en mode invité. Un échec passager est retenté
  (1 min, ou 10 min si la recherche ou l'import a échoué).
- **Diffusion** (`GET /api/me/jam/stream`, Server-Sent Events) : l'état (`JamStateDto`, avec les suivants déjà
  résolus dans `Upcoming`) est poussé à tous les appareils du compte, et renvoyé frais toutes les 10 s.
- **Lecture en phase** (`JamSession`, appli) : départ directement à la position de l'hôte ; titres suivants mis en
  file pour un enchaînement sans blanc ; écart > 2,5 s → un saut, sinon la vitesse est ajustée de quelques % (hauteur
  conservée) jusqu'à moins de 40 ms. Titre absent du serveur : lecture YouTube directe sur l'appareil
  (`YoutubeDirect`, recherche YouTube Music + flux audio en ~0,5 s), sans changer de source en cours de titre.
- **Lien court** (`spotify.link/…`) : suivi à la main, en https et vers des hôtes Spotify seulement (chaque
  redirection est revérifiée), pour que le serveur ne puisse pas être envoyé vers une autre adresse.

En mémoire : un redémarrage du serveur arrête les suivis.

## Endpoints (`/api/me/jam`, compte connecté)

| Méthode | Route | Rôle |
|---|---|---|
| GET | `/spotify` | État de la liaison Spotify (connecté ? nom affiché). |
| PUT | `/spotify/token` | Enregistre les jetons obtenus sur l'appareil. |
| DELETE | `/spotify/token` | Délie Spotify (et quitte le Jam suivi). |
| POST | `/join` | Rejoint un Jam depuis un lien, mode `RemoteOnly`/`Guest`. 409 si Spotify refuse. |
| POST | `/leave` | Arrête de suivre et quitte le Jam côté Spotify. |
| GET | `/state` | État courant (one-shot). |
| GET | `/stream` | Flux d'état (SSE). |

## Deezer

Pas de suivi possible : Deezer n'a pas d'équivalent au Jam (vérifié sur l'appli Android 9.0.26.3 et confirmé par
Deezer en 2025). *Music Together* (ex-Shaker) n'est qu'une playlist commune, sans titre en cours ni position, et
Deezer Connect ne relie que les appareils d'un même compte. Un lien Deezer collé est refusé avec un message clair.

## À faire

- **Reprise après redémarrage du serveur** (re-suivre automatiquement les Jams actifs).
- Décider si un **import** déclenché par un Jam doit compter dans le quota quotidien du compte.
- À terme, déplacer le suivi dans l'appli (le serveur est partagé) : le code de protocole (`ClusterState`,
  `SessionUpdate`, dealer) se reporte tel quel.
