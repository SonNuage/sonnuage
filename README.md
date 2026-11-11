# SonNuage

**English** · [Français](README.fr.md)

Self-hosted music server in the style of Spotify: library scanned from your folders, web player,
Android app (with Android Auto), desktop app (Windows, macOS, Linux), playlists, favorites, on-the-fly transcoding, administration.

*SonNuage*: SoundCloud, translated into French.

## Project status

SonNuage is a young personal project, still in development. Expect bugs and breaking changes, and keep backups.

| Client | Status |
|---|---|
| Web player | Reference client, used every day |
| Android app (with Android Auto) | Works, used every day |
| Desktop app (Windows, macOS, Linux) | Works, used occasionally, so less tested |
| iOS app | Alpha testing, unsigned build |
| Browser extension | Work in progress, unsigned |

The interface is in French and English. Screenshots of a real instance: https://sonnuage.github.io/en/.

## Repositories

| Project | Role |
|---|---|
| `src/SonNuage.Server` | ASP.NET Core API (.NET 10), scan, ffmpeg transcoding, JWT auth, hosts the web client |
| `src/SonNuage.UI` | Razor pages shared by web / Android / desktop (player + admin) |
| `src/SonNuage.Web` | Blazor WebAssembly client |
| `tests/SonNuage.Server.Tests` | Unit and integration tests (xUnit v3) |
| `legacy/` | Old Node.js / Capacitor proofs of concept, kept for reference |

The other parts of the project have their own repository:

| Repository | Content |
|---|---|
| [sonnuage](https://github.com/SonNuage/sonnuage) (this one) | Server, web client, shared libraries `SonNuage.Contracts` and `SonNuage.UI` |
| [sonnuage-android](https://github.com/SonNuage/sonnuage-android) | Android app (.NET MAUI Blazor Hybrid, Media3, Android Auto) |
| [sonnuage-ios](https://github.com/SonNuage/sonnuage-ios) | iOS app (same MAUI Blazor Hybrid project as the Android app, target `net10.0-ios`) |
| [sonnuage-desktop](https://github.com/SonNuage/sonnuage-desktop) | Desktop app for Windows, macOS, Linux (Photino: system webview, tray icon) |
| [sonnuage-extension](https://github.com/SonNuage/sonnuage-extension) | Browser extension (YouTube import, cookie upload) |

`SonNuage.Contracts` and `SonNuage.UI` are published as NuGet packages in this repository's registry on every build of
`main` (version `0.1.0-ci.N`) and on every `vX.Y.Z` tag. The Android app and the desktop app use them.

The rewrite plan and the architecture choices are in [docs/PLAN.md](docs/PLAN.md) (in French).

## Features

- **Library**: incremental scan (TagLib# tags, fallback on `Artist/Album/NN title`), folder watching,
  covers (folder, embedded, uploaded from the admin), deduplication of "(1)" copies.
- **Playback**: queue, shuffle (the current track stays first, the original order is restored),
  repeat, favorites, history, queue restored on restart.
- **Quality**: original, FLAC, MP3 320/192/128/64. Transcoding is streamed while encoding
  (first byte in ~0.1 s) and cached (LRU purge). A file is never "upscaled".
- **Unstable network**: preloading of the next tracks (web: in memory, Android: 1 GB disk cache
  and 5 min buffer), automatic resume on network error, separate quality on mobile data on Android.
- **Android Auto**: Playlists / Albums / Artists / Recent tabs, search, "Ok Google, play …".
- **Admin** (`/admin`): dashboard (origin of the tracks, history of additions, disk), rescan,
  album upload in 8 MB chunks (resumable), import from YouTube, tag and cover editing,
  account management.
- **Languages**: French and English. Each account chooses its language (Settings), otherwise the browser's or the phone's.

## Translations

The interface texts and the server messages are in `src/SonNuage.Contracts/Localization/{code}.json`
(one file per language, embedded in the assembly and discovered automatically). **To add a language**:

1. copy `en.json` to `de.json` (ISO 639-1 code) and translate the values, without touching the keys.
2. fill in `_meta`: `name` (name shown in the selector, in the language itself), decimal and
   thousands separators, month and day names.
3. if the language's plurals do not follow the English rule (`one` for 1, otherwise `other`), add its rule
   to `PluralRules` ([Translations.cs](src/SonNuage.Contracts/Localization/Translations.cs)).
4. `dotnet test`: a test checks that each language has exactly the keys and the variables (`{count}`, `{name}`…) of English.

A missing key falls back to English. Value format: named variables `{name}`, plurals as an object
`{ "one": "…", "other": "…" }` (CLDR categories) chosen by the `count` variable. In Razor:
`@L["home.recentlyAdded"]`, `@L["common.tracks", ("count", n)]`, `L.Date(date)`. On the server, a message
meant for the user is written `Tx.Key("errors.nameRequired")`: it is translated according to `Accept-Language` in
`ProblemDetails`, or at display time (`L.Server(text)`) when it is stored, like YouTube import errors.

## Import from YouTube

Reserved for administrators (`/admin/youtube`), with [yt-dlp](https://github.com/yt-dlp/yt-dlp):

- **Explore**: YouTube Music search (better metadata) or YouTube search, or the pasted address of a video,
  a playlist, an album or a channel. Select, then import.
- **Queue**: each video becomes a chain of tasks (download → AcoustID fingerprint →
  metadata and filing, see [Tasks](#tasks)). Several downloads in parallel (3 by default),
  live progress. The best audio track is kept as is (Opus or AAC, no re-encoding.
  MP3 as an option). When YouTube refuses (too many requests, "confirm you are not a robot"), the
  downloads pause and resume on their own (30 min, then double).
- **Metadata** rebuilt automatically: YouTube Music fields, MusicBrainz search scored on title,
  artist and duration (the official studio album is preferred to compilations), optional AcoustID audio
  fingerprint (free key + `fpcalc`). Cover from the Cover Art Archive, otherwise the thumbnail cropped to a square.
  Full tags (MusicBrainz identifiers, link to the video) and filing in `uploads/YouTube/<video id>/`:
  the title, free text, lives only in the tags and is never used as a file name.
  Below the adjustable confidence threshold, the import is flagged "to check": the admin picks among the
  suggestions, searches MusicBrainz or types the values by hand. The file is retagged and moved, the track
  keeps its place in the playlists.
- **Subscriptions**: playlists, albums or channels synchronized periodically (6 h by default).
- **Settings**: one-click update of yt-dlp / Deno (official GitHub releases, SHA-256 checked),
  YouTube cookies (cookies.txt format), audio format, threshold, AcoustID key.

The Docker image includes yt-dlp, Deno and fpcalc. Download only what you have the right to keep
(free works, your own content, or according to the law that applies to you): checking this is your responsibility.

### Browser extension

The extension (Chrome, Edge, Firefox) has its own repository: [sonnuage-extension](https://github.com/SonNuage/sonnuage-extension).
It authenticates with a key created in *Administration → API keys*. This key only allows adding
videos to the queue, reading a YouTube address, subscribing, sending YouTube cookies and reading the state of a
video.

Behind Cloudflare Access, allow the header `Authorization: ApiKey …` (*Bypass* rule or service
token) for the paths `/api/admin/youtube/*` and `/api/extension/*`.

## Tasks

**Admin → Tasks** shows the execution queues, the task chains (running, waiting, failed,
done), their steps and the live log of each step. Tasks can be cancelled, restarted and created there.

- Each queue has a limit of simultaneous tasks: downloads (YouTube setting, 1 to 6), **AcoustID: only
  one at a time**, imports and rescans together (and one request per second at most, also enforced by the
  client), MusicBrainz metadata: one at a time.
- **AcoustID rescan** of the whole library or of one artist: fingerprint of each track, comparison with
  MusicBrainz, automatic correction of the sure matches (above the threshold set in YouTube, never
  on a track edited by hand) or simple suggestions in the log.
- The queue lives in the database: tasks interrupted by a shutdown resume on restart, and several
  instances sharing the database can consume the queue without exceeding the limits
  (`SonNuage__Jobs__Worker`, `SonNuage__Jobs__WorkerName`). Finished chains are kept 14 days
  (`SonNuage__Jobs__RetentionDays`).

What comes next: [docs/ROADMAP.md](docs/ROADMAP.md) (in French).

## Audio analysis

For the themed radios (good-mood pop, calm, party…), each track is analyzed by
[Essentia](https://essentia.upf.edu/) and its TensorFlow models: moods (happy, sad, relaxed, aggressive,
party), acoustic / electronic, danceability, voice / instrumental, valence and energy, tempo. Only the three
minutes in the middle are listened to, on two low-priority threads: a few seconds per track.

- "analysis" queue (one task at a time), started at startup and after each scan: tracks never analyzed,
  replaced files, or an outdated version of the computation. An unreadable file keeps its error and is not retried
  until it has changed.
- **Settings → Audio analysis**: progress, activation, immediate start.
- Shipped in the **amd64** Docker image only (Python, `essentia-tensorflow` and its models: about 800 MB
  more on disk, in the runtime base). The analysis process uses close to 1 GB of memory while it
  works and stops after two minutes without a track to analyze. Elsewhere
  (arm64, development on Windows), the analysis reports itself as unavailable. Outside Docker:
  `SonNuage__Analysis__PythonPath` (Python where `essentia-tensorflow` is installed) and `SonNuage__Analysis__ModelsPath`
  (models folder, list and checksums in the [Dockerfile](Dockerfile)).

The Essentia models (discogs-effnet, msd-musicnn, emomusic, TempoCNN) are © Music Technology Group, Universitat
Pompeu Fabra, under the [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/) licence: non-commercial
use only, with attribution. Essentia is under the AGPL v3 licence.

## Search

Without configuration, search runs in the database: substrings, case-insensitive and accent-insensitive.
With **Elasticsearch** (8.x or 9.x), it becomes instant and tolerant:

- results from the first letters of each word (`hig volt` → High Voltage)
- typos (`gilmor ratle` → Rattle That Lock) and "Did you mean …?"
- suggestions while typing, including in the middle of a name (`gilm` → David Gilmour)
- `AC/DC`, `T.N.T.` found by typing `acdc`, `tnt`
- most played tracks favored
- optional semantic search (see below: Elasticsearch enterprise or trial licence required).

### Search by mood

A search in everyday language, in French or in English ("musique calme pour travailler", "énergique
pour courir", "chill du dimanche", "rock énergique années 90", "sad songs") adds a
**Mood** section above the results, with "Start as radio" (endless radio, like the themed radios).
Everything happens on the server, without an outside service or Elasticsearch: the words for mood, energy,
tempo and activity, the decades and the genres of the library become criteria on the audio
analysis (Essentia), the MusicBrainz tags, the genre and the year. A track not analyzed yet can match through its
tags or its genre.

It is a mood search only if every word is understood and a mood or a decade is asked for; and the exact
name of an artist, an album or a track of the library wins ("Calme", if it is a song).
The vocabulary is a table to extend in
[AmbianceVocabulary.cs](src/SonNuage.Server/Search/AmbianceVocabulary.cs).

The index is rebuilt automatically after each scan, each change from the admin, when
Elasticsearch comes back after an outage and every 30 minutes.

**Resilience**: a query that does not complete in 2 s (`SonNuage__Search__TimeoutMilliseconds`) is served
by the database search. The Elasticsearch query continues in the background with retries and
its result serves the next keystroke. After repeated failures, Elasticsearch is set aside (immediate
database answer) and probed in the background until it comes back. The state is visible in the admin.

| Variable | Role |
|---|---|
| `SonNuage__Search__Url` | Elasticsearch URL. Empty = database search |
| `SonNuage__Search__ApiKey` or `__Username` / `__Password` | Authentication |
| `SonNuage__Search__CertificateFingerprint` | SHA-256 fingerprint of Elasticsearch's self-signed certificate |
| `SonNuage__Search__Index` | Index prefix (`sonnuage`) |
| `SonNuage__Search__TimeoutMilliseconds` | Delay before falling back to the database (`2000`) |
| `SonNuage__Search__SemanticInferenceId` | Inference endpoint of the semantic search (see below). Empty = none |

### Semantic search (Elasticsearch option)

With `SonNuage__Search__SemanticInferenceId`, the index gets a `semantic_text` field: each track is described in it
(names, genre, decade, tags, moods heard by the audio analysis) and similarity of meaning reorders the
text results (it never adds a result without a text match).

**Licence.** Since Elasticsearch 8.18/9.0, the whole inference API (ELSER/E5 models deployed in the cluster
as well as endpoints to an external or local service: `openai`, `hugging_face`, `custom`…) requires an
**enterprise** or trial (30 days) licence ([elastic/elasticsearch#122293](https://github.com/elastic/elasticsearch/pull/122293)).
With the **basic** (free) licence, a `semantic_text` field would make indexing fail: SonNuage reads the
licence at each index rebuild and, if it does not allow inference, leaves the option aside (warning
in the log). The mood search above covers this case without installing anything.

**Memory.** The built-in models (`.multilingual-e5-small-elasticsearch`, ELSER) run on a machine
learning node, outside the Java heap: Elastic states 4 GB at least for an ML node dedicated to ELSER
([ELSER doc](https://www.elastic.co/docs/explore-analyze/machine-learning/nlp/ml-nlp-elser)). An Elasticsearch with
a 512 MB heap is not enough. A small embeddings service next to it, on CPU, is lighter.

**Setup with a local model** (enterprise or trial licence; not tested here):

1. Local embeddings service, for example Hugging Face *Text Embeddings Inference* (CPU image) with
   `intfloat/multilingual-e5-small` (French and English), reachable by Elasticsearch, with no Internet access needed
   once the model is downloaded:
   `docker run -d --name tei -p 127.0.0.1:8081:80 -v tei-data:/data ghcr.io/huggingface/text-embeddings-inference:cpu-latest --model-id intfloat/multilingual-e5-small`
2. Inference endpoint to this service (the token is required by the API, TEI ignores it):
   ```
   PUT _inference/text_embedding/sonnuage-e5
   { "service": "hugging_face", "service_settings": { "api_key": "local", "url": "http://tei:80/embed" } }
   ```
   Check: `POST _inference/text_embedding/sonnuage-e5 { "input": "musique calme" }`.
3. `SonNuage__Search__SemanticInferenceId=sonnuage-e5` then restart: a new `…-semantic` index is created and
   filled (button "Reindex search" in the admin to force it), the old one is deleted afterwards.

Search queries then go to this local service, never to a third party.

Locally: `docker run -d -p 127.0.0.1:9200:9200 -e discovery.type=single-node -e xpack.security.enabled=false docker.elastic.co/elasticsearch/elasticsearch:9.2.0`
(the dev configuration already points to `http://localhost:9200`).

## Development

Requirements: .NET 10 SDK, ffmpeg (in the `PATH` or `bin/ffmpeg.exe`).

```bash
dotnet run --project src/SonNuage.Server --launch-profile SonNuage.Server
```

Then http://localhost:5080. The dev configuration ([appsettings.Development.json](src/SonNuage.Server/appsettings.Development.json))
reads the music from `data/`, writes the database and the caches to `var/` and creates the `admin` account on first start.

Tests (`SONNUAGE_TEST_FFMPEG` enables the transcoding tests, `SONNUAGE_TEST_ELASTICSEARCH` those of the advanced search):

```bash
dotnet test --project tests/SonNuage.Server.Tests
```

### Android app

It has its own repository: [sonnuage-android](https://github.com/SonNuage/sonnuage-android). It builds against the
`SonNuage.Contracts` and `SonNuage.UI` packages published by this repository, or directly against its sources if both repositories
are cloned side by side (see its README).

## Deployment behind Cloudflare

The server is meant to be reached only through a **Cloudflare tunnel** (no open port):

1. Cloudflare Zero Trust → Networks → Tunnels: create a tunnel, route the host name
   (e.g. `music.example.org`) to `http://sonnuage:8080`, copy the token.
2. On the host machine:
   ```bash
   cd deploy && cp .env.example .env   # fill in image, music folder, admin password, token
   docker compose up -d
   ```

Cloudflare specifics, already handled:
- the client IP comes from `CF-Connecting-IP` (login attempts limited per IP)
- file uploads are split into 8 MB chunks (limit of 100 MB per request)
- audio streams are `Cache-Control: private, no-store` and protected by a token in the query:
  Cloudflare caches nothing on the CDN side.
- transcoding sends its first bytes immediately (no 524 timeout on long files)
- `/healthz` for probes.

`SonNuage__BehindProxy=true` (default) trusts the proxy headers: do not expose port 8080
directly on the Internet in this mode.

### Configuration (environment variables)

| Variable | Default | Role |
|---|---|---|
| `SonNuage__MusicPaths__0` | `/music` | Music folder(s) (read-only) |
| `SonNuage__UploadPath` | `/uploads` | Destination of the admin's uploads |
| `SonNuage__DataPath` | `/config` | SQLite database, signing key, caches |
| `SonNuage__InitialAdminUser` / `__InitialAdminPassword` | `admin` / none | Account created if the database is empty |
| `SonNuage__TranscodeCacheMaxMegabytes` | `10240` | Size of the transcoding cache |
| `SonNuage__MaxConcurrentTranscodes` | `4` | Simultaneous ffmpeg encodings |
| `SonNuage__ScanIntervalMinutes` | `360` | Periodic full rescan (0 = never) |
| `SonNuage__BehindProxy` | `true` | Trust `CF-Connecting-IP` / `X-Forwarded-*` |

## GitLab CI/CD

[.gitlab-ci.yml](.gitlab-ci.yml), about 1 min 30: server tests and, in parallel, Docker image pushed to the
project's registry (`:sha`), checked at startup. Once the tests pass: `:latest` on the default
branch, `:X.Y.Z` on a tag, and the NuGet packages `SonNuage.Contracts` / `SonNuage.UI`. On `main`, semantic-release then
creates the `vX.Y.Z` tag and its release (see Versions). The image is assembled by `crane`: cached runtime base (`runtime-base`, rebuilt when the
Dockerfile changes) + one layer with the application published by the job.

The runner must accept docker-in-docker (`privileged = true`): full procedure in [docs/CI-RUNNER.md](docs/CI-RUNNER.md) (in French).

### Versions

Versions are published by [semantic-release](https://semantic-release.gitbook.io/) ([.releaserc.json](.releaserc.json)),
from the commit messages in the [Conventional Commits](https://www.conventionalcommits.org/en/) format:
`fix: …` gives a patch (1.0.1), `feat: …` a minor version (1.1.0), `feat!: …` or a
`BREAKING CHANGE:` footer a major version (2.0.0). `docs`, `chore`, `ci`, `refactor`, `test` publish nothing.
On `main`, the `semantic-release` job creates the `vX.Y.Z` tag and the GitLab release with its notes. The tag's
pipeline then publishes the version. It uses `GITLAB_TOKEN`, a token of the `sonnuage` group (masked and protected variable,
protected `v*` tags).

### GitHub Actions

[.github/workflows/ci.yml](.github/workflows/ci.yml) does the same on GitHub: tests (with ffmpeg and
Elasticsearch), Docker image on `ghcr.io/<owner>/sonnuage`, NuGet packages in the owner's GitHub
registry, release on `vX.Y.Z` tags. No secret to configure: the token provided by GitHub is enough.

## Licence

SonNuage is free software under the [GNU AGPL v3.0 or later](LICENSE) licence: anyone can use it,
study it, modify it and redistribute it. A modified version, including one offered as an online service, must
publish its source code under the same licence.

Copyright © 2026 pmj.bailly and the SonNuage contributors.
