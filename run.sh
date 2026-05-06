#!/usr/bin/env bash
# Monorepo kökünden çalıştırın: ./run.sh
# Ctrl+C = çıkış kodu 130 (SIGINT); bu normaldir — hem API hem Astro durur.
# $ROOT yalnızca bu script içinde tanımlıdır; ayrı terminale kopyalayıp cd "$ROOT/frontend" yazmayın.

set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"

(cd "$ROOT/backend/src/ThumbnailGenerator.Api" && dotnet run) &
API_PID=$!
cleanup() { kill "$API_PID" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

cd "$ROOT/frontend"
npm run dev
