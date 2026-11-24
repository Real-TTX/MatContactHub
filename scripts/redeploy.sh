#!/usr/bin/env sh
# Baut den MatContactHub-Container neu und startet den lokalen Stack. "--clean" löscht vorher die Volumes.
set -e
cd "$(dirname "$0")/.."
if [ "$1" = "--clean" ]; then
  docker compose -f docker-compose.local.yml down -v --remove-orphans
fi
docker compose -f docker-compose.local.yml up -d --build --remove-orphans
echo "MatContactHub läuft auf http://localhost:7087 (LDAP: localhost:7389, LDAPS: localhost:7636, pgAdmin: http://localhost:7088, Postgres: localhost:7089)"
