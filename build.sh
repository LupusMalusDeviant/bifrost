#!/usr/bin/env bash
# Lokaler Verifier — dieselben Pruefpfade, die auch die CI faehrt.
#
# Duenne Orchestrierung, KEINE eigene Buildlogik. Jeder Modus ruft genau die Befehle auf, die in
# .github/workflows/ci.yml stehen; Exitcodes werden unveraendert durchgereicht.
#
# Warum das existiert: Ein Mensch und ein Agent sollen dieselben Kommandos benutzen wie die
# Pipeline. Solange jeder seine eigene Befehlsfolge zusammenstellt, heisst "bei mir gruen" nichts.
#
# WICHTIG — die CI bleibt die Referenz. Weicht dieses Skript von ci.yml ab, ist das Skript falsch.
#
# Nutzung:
#   ./build.sh verify-fast       Format + Build + schnelle Suiten (ohne Integration)
#   ./build.sh verify-dotnet     vollstaendige .NET-Suite wie im CI-Job 'build'
#   ./build.sh verify-rust       fmt + clippy + cargo test wie im CI-Job 'wasi-spike'
#   ./build.sh verify-container  Image bauen und Smoke-Test wie im CI-Job 'container'
#   ./build.sh verify-all        alles nacheinander

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUST="$REPO/spikes/wasi-component-runtime"
MODE="${1:-verify-fast}"

step() {
  local name="$1"; shift
  printf '\n\033[36m── %s \033[0;90m%s\033[0m\n' "$name" "$(printf '─%.0s' $(seq 1 $((70 - ${#name} > 0 ? 70 - ${#name} : 0))))"
  # Kein Retry, kein Weichzeichnen: Ein Schritt, der scheitert, beendet den Lauf — sonst liest sich
  # ein roter Durchgang am Ende wie ein gruener.
  if ! "$@"; then
    printf '\033[31mFEHLGESCHLAGEN: %s\033[0m\n' "$name"
    exit 1
  fi
}

have() {
  if ! command -v "$1" >/dev/null 2>&1; then
    printf '\033[33mUebersprungen: "%s" ist nicht installiert. %s\033[0m\n' "$1" "$2"
    return 1
  fi
}

verify_rust() {
  have cargo 'Rust-Gates laufen dann nur in der CI.' || return 0
  ( cd "$RUST"
    step 'cargo fmt --check' cargo fmt --check
    step 'cargo clippy -D warnings' cargo clippy --locked --all-targets -- -D warnings
    step 'cargo test' cargo test --locked )
}

verify_fast() {
  step 'dotnet build (Release, warnings=errors)' \
    dotnet build "$REPO/Bifrost.slnx" --configuration Release
  # Die Integrationssuite startet echte Prozesse und braucht Minuten — sie gehoert in
  # verify-dotnet, nicht in den schnellen Durchgang.
  step 'dotnet test (Core, Upstream, CLI)' \
    dotnet test "$REPO/Bifrost.slnx" --configuration Release --no-build \
      --filter 'FullyQualifiedName!~Integration.Tests&FullyQualifiedName!~WasiRealHost'
}

verify_dotnet() {
  step 'dotnet build (Release, warnings=errors)' \
    dotnet build "$REPO/Bifrost.slnx" --configuration Release
  # WasiRealHost ist ausgenommen wie in der CI: Diese Tests brauchen das gebaute Rust-Binary und
  # laufen im eigenen Job. Ohne das Binary ueberspringen sie sich selbst — siehe WasiHostPaths.
  step 'dotnet test (vollstaendig, ohne WasiRealHost)' \
    dotnet test "$REPO/Bifrost.slnx" --configuration Release --no-build \
      --filter 'FullyQualifiedName!~WasiRealHost'
}

verify_container() {
  have docker 'Container-Gate laeuft dann nur in der CI.' || return 0
  step 'docker build' docker build -t bifrost:verify "$REPO"
  step 'compose-Beispiele sind gueltig' bash -c \
    "docker compose -f '$REPO/docker-compose.yml' config --quiet && \
     docker compose -f '$REPO/docker-compose.yml' -f '$REPO/docker-compose.postgres.yml' config --quiet"
  step 'non-root im Image' bash -c \
    '[ "$(docker run --rm --entrypoint sh bifrost:verify -c "id -u")" != "0" ] || { echo "Image laeuft als root."; exit 1; }'
}

case "$MODE" in
  verify-fast)      verify_fast ;;
  verify-dotnet)    verify_dotnet ;;
  verify-rust)      verify_rust ;;
  verify-container) verify_container ;;
  verify-all)       verify_fast; verify_rust; verify_dotnet; verify_container ;;
  *) echo "Unbekannter Modus: $MODE"; sed -n '13,20p' "$0"; exit 2 ;;
esac

printf '\n\033[32mOK — %s\033[0m\n' "$MODE"
