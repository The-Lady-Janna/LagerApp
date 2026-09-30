# Lager - ein Container mit Oberfläche (SPA) und API.
#   docker compose up --build     (siehe docker-compose.yml)
#   docker build -t lager .       (aus dem Repo-Root, ohne Zusatzargumente; so baut es auch .github/workflows/docker.yml)
#
# Drei Stufen: Frontend bauen (Node) -> API veröffentlichen (.NET SDK) -> schlankes Laufzeit-Image (ASP.NET, Debian).

# ---- 1) Frontend bauen (Vite) ------------------------------------------------------------------------------------
# Node 22: die "engines" der package.json erlauben 20.19+ und 22.13+; Node 20 ist seit April 2026 ohne Wartung.
FROM node:22-slim AS frontend
WORKDIR /build/frontend/lager-ui
# Erst nur die Paketdateien: die npm-Schicht bleibt im Cache, solange sich package*.json nicht ändern.
COPY frontend/lager-ui/package.json frontend/lager-ui/package-lock.json ./
RUN npm ci
COPY frontend/lager-ui/ ./
RUN npm run build

# ---- 2) API veröffentlichen --------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS publish
WORKDIR /build
# Directory.Build.props setzt Framework und Sprachversion für alle Projekte; global.json bleibt draußen, damit
# jedes 8.0-SDK des Basis-Images passt. Erst nur die Projektdateien (Restore-Schicht im Cache), dann der Quellcode.
COPY Directory.Build.props ./
COPY src/Lager.Domain/Lager.Domain.csproj src/Lager.Domain/
COPY src/Lager.Contracts/Lager.Contracts.csproj src/Lager.Contracts/
COPY src/Lager.Application/Lager.Application.csproj src/Lager.Application/
COPY src/Lager.Infrastructure/Lager.Infrastructure.csproj src/Lager.Infrastructure/
COPY src/Lager.Api/Lager.Api.csproj src/Lager.Api/
RUN dotnet restore src/Lager.Api/Lager.Api.csproj
COPY src/ src/
RUN dotnet publish src/Lager.Api/Lager.Api.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

# ---- 3) Laufzeit-Image -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
# QuestPDF/SkiaSharp (Versandetikett als PDF) braucht unter Linux fontconfig samt einer Schrift; curl für den Healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends fontconfig libfontconfig1 fonts-dejavu-core curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=publish /app/publish ./
# Das gebaute Frontend liegt in wwwroot: das Backend liefert es mit aus (index.html, /assets, Service-Worker).
COPY --from=frontend /build/frontend/lager-ui/dist ./wwwroot

# Alles Veränderliche liegt auf dem Volume /data (SQLite-Datenbank, JWT-Key, Logs, Backups), nichts davon im Image.
# Ein neues benanntes Volume übernimmt Besitzer und Rechte dieses Verzeichnisses; bei einem Bind-Mount muss das
# Host-Verzeichnis für die Benutzer-ID $APP_UID (1654) beschreibbar sein.
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data

# Produktions-Vorgaben; jede Einstellung ist per Umgebungsvariable (docker run -e / docker-compose.yml / .env) überschreibbar.
#   ASPNETCORE_URLS              Adresse und Port im Container
#   Database__ConnectionString   SQLite-Datei auf dem Volume (MySQL: siehe docker-compose.mysql.yml)
#   Jwt__KeyFile                 Signing-Key wird beim ersten Start erzeugt und hier abgelegt (alternativ Jwt__SigningKey)
#   Logging__Directory           Logdateien
#   Backup__Directory            Ziel des Backup-Endpunkts (Backups enthalten Passwort-Hashes und sind unverschlüsselt)
# Ohne Auth__BootstrapAdminPassword erzeugt der erste Start ein Einmalpasswort und gibt es genau einmal im Container-Log
# aus (docker compose logs lager).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    Database__ConnectionString="Data Source=/data/lager.db" \
    Jwt__KeyFile=/data/jwt.key \
    Logging__Directory=/data/logs \
    Backup__Directory=/data/backups

VOLUME /data
EXPOSE 8080
USER $APP_UID

# Bereit = Datenbank erreichbar (/health/ready antwortet sonst 503). Der Host "localhost" muss in AllowedHosts erlaubt bleiben.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Lager.Api.dll"]
