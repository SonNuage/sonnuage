# Image des jobs .NET de la CI GitLab (tests, publication, paquets NuGet) : SDK .NET, ffmpeg pour les tests et
# crane pour assembler l'image du serveur dans le registre. Reconstruite par le job ci-image quand ce fichier change.
FROM gcr.io/go-containerregistry/crane:latest AS crane

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
COPY --from=crane /ko-app/crane /usr/local/bin/crane
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/nuget
