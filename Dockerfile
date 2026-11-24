# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable (no native host), so the arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatContactHub/MatContactHub.csproj", "src/MatContactHub/"]
RUN dotnet restore "src/MatContactHub/MatContactHub.csproj"

COPY src src
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatContactHub/MatContactHub.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL org.opencontainers.image.title="MatContactHub" \
      org.opencontainers.image.description="Kontaktverzeichnis: Kontakte aus REST, SQL Server und CSV, Adressbuecher, Dubletten, eingebauter LDAP-Server fuer Telefone und Mail" \
      org.opencontainers.image.source="https://github.com/Real-TTX/MatContactHub" \
      org.opencontainers.image.vendor="Matthias Schmoldt"

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:7087 \
    HTTP_PORTS="" \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    TZ=Europe/Berlin

# 7087 web, 10389 LDAP (plain + StartTLS), 10636 LDAPS. Unprivileged ports inside: the compose files map them to 389/636 (release) or 7389/7636.
EXPOSE 7087 10389 10636

COPY --from=build /app/publish .

# One volume for what the app keeps in files: the keys that protect the sessions and the secrets of the data sources (keys), the LDAP certificate (ldap)
# and the uploaded CSV files of the data sources (sources). The database is the other container.
RUN mkdir -p /data/keys /data/ldap /data/sources && chown -R $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

# The app checks itself on /api/health (200 for ok and degraded, 503 for down), so the image needs no curl.
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s --retries=3 \
    CMD ["dotnet", "MatContactHub.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "MatContactHub.dll"]
