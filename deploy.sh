#!/usr/bin/env bash
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DEPLOY_DIR"

echo "=== Deploying VisionX Backend Service ==="
echo "Working directory: $DEPLOY_DIR"

git fetch origin main || true
git reset --hard origin/main || true

docker compose down --remove-orphans || true
docker compose up -d --build --remove-orphans

echo "Waiting for backend service to become healthy..."
docker compose ps
