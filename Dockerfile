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
WORKDIR /app
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    SonNuage__YtDlpPath=/opt/tools/yt-dlp \
    SonNuage__DenoPath=/opt/tools/deno
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
