# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Localify.Contracts/*.csproj src/Localify.Contracts/
COPY src/Localify.UI/*.csproj src/Localify.UI/
COPY src/Localify.Web/*.csproj src/Localify.Web/
COPY src/Localify.Server/*.csproj src/Localify.Server/
RUN dotnet restore src/Localify.Server/Localify.Server.csproj
COPY src/ src/
ARG VERSION=0.0.0
RUN dotnet publish src/Localify.Server/Localify.Server.csproj -c Release -o /app --no-restore \
    -p:Version=${VERSION} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /music /uploads /config \
    && chown -R $APP_UID /uploads /config
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
EXPOSE 8080
VOLUME ["/config", "/uploads"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Localify.Server.dll"]
