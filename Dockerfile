# syntax=docker/dockerfile:1

# Base d'exécution : ASP.NET, ffmpeg, yt-dlp, Deno et fpcalc. Cette couche change rarement et reste en cache.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# Import YouTube : yt-dlp et Deno (moteur JavaScript exigé par YouTube) épinglés et vérifiés. L'admin peut les
# mettre à jour sans reconstruire l'image (copie dans /config/tools, prioritaire). fpcalc pour AcoustID.
ARG YTDLP_VERSION=2026.10.26
ARG DENO_VERSION=2.9.8
ARG TARGETARCH
# GitHub renvoie parfois des 504 en rafale : chaque téléchargement réessaie (v1.2.0 et v1.3.0 ont échoué là).
ARG CURL="curl -fsSL --retry 8 --retry-all-errors --retry-delay 15"
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl unzip libchromaprint-tools \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /music /uploads /config /opt/tools \
    && case "${TARGETARCH:-amd64}" in \
         arm64) YTDLP_ASSET=yt-dlp_linux_aarch64; DENO_TARGET=aarch64-unknown-linux-gnu ;; \
         *) YTDLP_ASSET=yt-dlp_linux; DENO_TARGET=x86_64-unknown-linux-gnu ;; \
       esac \
    && cd /tmp \
    && ${CURL} -O "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${YTDLP_ASSET}" \
    && ${CURL} "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/SHA2-256SUMS" | grep " ${YTDLP_ASSET}\$" | sha256sum -c - \
    && install -m 755 "${YTDLP_ASSET}" /opt/tools/yt-dlp \
    && ${CURL} -O "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-${DENO_TARGET}.zip" \
    && echo "$(${CURL} "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-${DENO_TARGET}.zip.sha256sum" | grep -oE '[0-9a-fA-F]{64}' | head -1)  deno-${DENO_TARGET}.zip" | sha256sum -c - \
    && unzip -q "deno-${DENO_TARGET}.zip" -d /opt/tools \
    && chmod 755 /opt/tools/deno \
    && rm -f /tmp/* \
    && chown -R $APP_UID /uploads /config
# Analyse audio (humeur, énergie, tempo) : Essentia et ses modèles TensorFlow, en amd64 seulement (pas de paquet arm64).
# Paquets Python épinglés et vérifiés (--require-hashes), modèles téléchargés à des adresses fixes et vérifiés par
# sha256. Les modèles Essentia sont sous licence CC BY-NC-SA 4.0 (voir le README). Le script d'analyse est livré avec le
# serveur (Analysis/essentia_analyzer.py). Sans tout cela (arm64), l'analyse se désactive d'elle-même.
COPY <<'EOF' /opt/analysis/requirements.txt
essentia-tensorflow==2.1b6.dev1389 --hash=sha256:6812425191f47ac7a5c566484f86a67890d581977e4969d1af83dc2dbc1f56ee
numpy==2.2.6 --hash=sha256:fd83c01228a688733f1ded5201c678f0c53ecc1006ffbc404db9f7a899ac6249
pyyaml==6.0.2 --hash=sha256:80bab7bfc629882493af4aa31a4cfa43a4c57c83813253626916b8c7ada83476
six==1.17.0 --hash=sha256:4721f391ed90541fddacab5acf947aa0d3dc7d27b2e1e8eda2be8970586c3274
EOF
COPY <<'EOF' /opt/analysis/models.txt
3ed9af50d5367c0b9c795b294b00e7599e4943244f4cbd376869f3bfc87721b1 feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb
cdea0722bcee7f731286843f2233e3aa69887bb5c3e2dce011eff55f38d04f3e feature-extractors/musicnn/msd-musicnn-1.pb
de322aee7f4da29ecbd149c86e7964bd6fabe41ca85698a0be40a39d49e88633 classification-heads/mood_happy/mood_happy-discogs-effnet-1.pb
4865cba49968b6ec295db3e8af6b4a7bb506b1a646628b9f07ee9910d52df82c classification-heads/mood_sad/mood_sad-discogs-effnet-1.pb
2c5aa6666b58fe80429a2dc677a135e9995922889b5a399af87d1c15f0ebb71d classification-heads/mood_relaxed/mood_relaxed-discogs-effnet-1.pb
7705284e3a67f23f04d3f2fd75e18a82c0e70db8875b7b6f7061f2432de80858 classification-heads/mood_aggressive/mood_aggressive-discogs-effnet-1.pb
bbd773a27002978179dd6fc5ffb75b7c41e6a7e412b5c5254d51a025e5b9373e classification-heads/mood_party/mood_party-discogs-effnet-1.pb
96a48d403f52fe9047214437fe396dd2f46699da765c911cd6dfbd456a20f733 classification-heads/mood_acoustic/mood_acoustic-discogs-effnet-1.pb
74cb244069c3803d86e6ee7c2817ecac46e37ac2759c457fa2f20d2132a8cb8b classification-heads/mood_electronic/mood_electronic-discogs-effnet-1.pb
e1251f02cdc846445e2bc1fb3fe9963e32728c5c000e009188ae47c8963cb4c4 classification-heads/danceability/danceability-discogs-effnet-1.pb
c8033548e17c292874265db62e82a051247768d10375a35a769e7bf695f16acf classification-heads/voice_instrumental/voice_instrumental-discogs-effnet-1.pb
fcfb486510213b35e0a691975325f58170f648ad4a02d749bce790da13ded43b classification-heads/emomusic/emomusic-msd-musicnn-2.pb
21c328332a221695dd6e8572728c617373064df882e8f81da6d88dc3a821e3b3 tempo/tempocnn/deeptemp-k16-3.pb
EOF
RUN if [ "${TARGETARCH:-amd64}" = "amd64" ]; then \
      apt-get update \
      && apt-get install -y --no-install-recommends python3 python3-venv \
      && rm -rf /var/lib/apt/lists/* \
      && python3 -m venv /opt/analysis/venv \
      && /opt/analysis/venv/bin/pip install --no-cache-dir --disable-pip-version-check --only-binary=:all: --require-hashes \
           -r /opt/analysis/requirements.txt \
      && mkdir -p /opt/analysis/models \
      && cd /opt/analysis/models \
      && while read -r sha path; do \
           ${CURL} -o "${path##*/}" "https://essentia.upf.edu/models/${path}" \
           && echo "${sha}  ${path##*/}" | sha256sum -c - || exit 1; \
         done < /opt/analysis/models.txt; \
    fi
WORKDIR /app
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    SonNuage__YtDlpPath=/opt/tools/yt-dlp \
    SonNuage__DenoPath=/opt/tools/deno \
    SonNuage__Analysis__PythonPath=/opt/analysis/venv/bin/python \
    SonNuage__Analysis__ModelsPath=/opt/analysis/models
EXPOSE 8080
VOLUME ["/config", "/uploads"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "SonNuage.Server.dll"]

# Compilation. Le restore reste en cache tant que les fichiers projet ne changent pas.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SonNuage.Contracts/*.csproj src/SonNuage.Contracts/
COPY src/SonNuage.UI/*.csproj src/SonNuage.UI/
COPY src/SonNuage.Web/*.csproj src/SonNuage.Web/
COPY src/SonNuage.Server/*.csproj src/SonNuage.Server/
RUN dotnet restore src/SonNuage.Server/SonNuage.Server.csproj
COPY src/ src/
# Version publiée : argument VERSION, sinon fichier VERSION à la racine (écrit au déploiement depuis le tag),
# sinon celle de Directory.Build.props. Le motif VERSIO[N] n'échoue pas quand le fichier est absent.
COPY global.json VERSIO[N] ./
ARG VERSION=
RUN V="${VERSION:-$(cat VERSION 2>/dev/null | tr -d '[:space:]')}"; \
    dotnet publish src/SonNuage.Server/SonNuage.Server.csproj -c Release -o /app --no-restore \
    ${V:+-p:Version=$V} -p:UseAppHost=false

# Par défaut (docker compose, GitHub) : tout est compilé dans l'image. La CI GitLab, elle, publie l'image de la
# cible base (runtime-base) et y ajoute l'application publiée par le job (voir .gitlab-ci.yml).
FROM base AS runtime
COPY --from=build /app .
