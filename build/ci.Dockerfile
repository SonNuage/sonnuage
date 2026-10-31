# Image des jobs .NET de la CI GitLab (tests, publication, paquets NuGet) : SDK .NET, ffmpeg pour les tests et
# client Docker pour construire l'image du serveur. Reconstruite par le job ci-image quand ce fichier change.
FROM docker:28-cli AS docker

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
COPY --from=docker /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker /usr/local/libexec/docker/cli-plugins/docker-buildx /usr/local/libexec/docker/cli-plugins/docker-buildx
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/nuget
