# syntax=docker/dockerfile:1

# ── Build ────────────────────────────────────────────────────────────────────
# Distro explizit gepinnt statt nur ":10.0": Der Default-Alias zeigt heute auf Ubuntu 24.04
# (noble), wandert aber bei einem künftigen Distro-Wechsel weiter — das soll nicht unbemerkt
# unter einem laufenden Build passieren.
#
# Bewusst KEINE -chiseled-Variante: Das Laufzeit-Image richtet /data per chown/chmod ein und
# braucht dafür eine Shell (siehe unten). Chiseled ist distroless und hat keine.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restore zuerst (Layer-Cache): nur die Projekt-/Props-Dateien kopieren.
COPY Directory.Build.props Directory.Packages.props nuget.config MCPMCP.slnx ./
COPY src/McpMcp.Abstractions/McpMcp.Abstractions.csproj src/McpMcp.Abstractions/
COPY src/McpMcp.Core/McpMcp.Core.csproj src/McpMcp.Core/
COPY src/McpMcp.Upstream/McpMcp.Upstream.csproj src/McpMcp.Upstream/
COPY src/McpMcp.Persistence/McpMcp.Persistence.csproj src/McpMcp.Persistence/
COPY src/McpMcp.Persistence.Migrations.Sqlite/McpMcp.Persistence.Migrations.Sqlite.csproj src/McpMcp.Persistence.Migrations.Sqlite/
COPY src/McpMcp.Persistence.Migrations.Postgres/McpMcp.Persistence.Migrations.Postgres.csproj src/McpMcp.Persistence.Migrations.Postgres/
COPY src/McpMcp.Web/McpMcp.Web.csproj src/McpMcp.Web/
COPY src/McpMcp.Server/McpMcp.Server.csproj src/McpMcp.Server/
RUN dotnet restore src/McpMcp.Server/McpMcp.Server.csproj

COPY src/ src/
RUN dotnet publish src/McpMcp.Server/McpMcp.Server.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# ── WASI-Host (Rust, ADR-0020, Plan 0003/WP7.1) ─────────────────────────────
# Der Host wird MIT ausgeliefert statt als getrenntes Artefakt: Gateway und Host sprechen einen
# versionierten IPC-Vertrag, und zwei getrennt verteilte Artefakte laufen früher oder später
# auseinander. Ein Image = ein Vertragsstand.
#
# `--platform=$BUILDPLATFORM` hält die Stage auf der Architektur des Bauhosts und kreuzkompiliert
# von dort. Ohne das liefe der Rust-Build für arm64 unter QEMU-Emulation — bei wasmtime sind das
# Größenordnungen mehr Bauzeit.
FROM --platform=$BUILDPLATFORM rust:1.94-slim-bookworm AS wasi-host
ARG TARGETARCH
WORKDIR /src

# Cross-Toolchain nur für den fremden Zielbogen; amd64 baut nativ.
# `crossbuild-essential-arm64` statt nur `gcc-aarch64-linux-gnu`: Der Compiler allein greift sonst
# auf die glibc-Header des Bauhosts zu und bricht in wasmtimes helpers.c ab
# ("bits/libc-header-start.h: No such file"). Erst das Paket mit der Ziel-libc bringt den Sysroot.
RUN if [ "$TARGETARCH" = "arm64" ]; then \
        apt-get update && apt-get install -y --no-install-recommends crossbuild-essential-arm64 \
        && rm -rf /var/lib/apt/lists/*; \
    fi

# Verzeichnislayout wie im Repo: Die Crate bindet eine WIT-Datei aus docs/ per include_str! ein.
COPY docs/spikes/fixtures/ docs/spikes/fixtures/
COPY spikes/wasi-component-runtime/ spikes/wasi-component-runtime/
WORKDIR /src/spikes/wasi-component-runtime
RUN case "$TARGETARCH" in \
      amd64) TARGET=x86_64-unknown-linux-gnu ;; \
      arm64) TARGET=aarch64-unknown-linux-gnu ;; \
      *) echo "Nicht unterstuetzte Zielarchitektur: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && rustup target add "$TARGET" \
    # Wasmtime kompiliert im Build-Skript auch C. Ohne CC_* nimmt die cc-Crate den Host-Compiler
    # und findet die Ziel-Header nicht ("bits/libc-header-start.h") — der Linker allein reicht nicht.
    && CC_aarch64_unknown_linux_gnu=aarch64-linux-gnu-gcc \
       CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc \
       cargo build --locked --release --target "$TARGET" \
    && cp "target/$TARGET/release/mcpmcp-wasi-component-spike" /mcpmcp-wasi-host

# ── Runtime (Ubuntu-noble-Basis mit Shell, non-root; ~230 MB < 300 MB) ───────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app
COPY --from=build /app ./
# Fester Pfad im Image: Er gehört in die Wasi.HostExecutable eines WASI-Upstreams.
COPY --from=wasi-host /mcpmcp-wasi-host /usr/local/bin/mcpmcp-wasi-host

# Datenverzeichnis (SQLite-DB + DataProtection-Keys) beschreibbar anlegen und dem non-root
# app-User geben. Chiseled-Images haben keine Shell für RUN chmod — die Ubuntu-Basis schon,
# was diese Verzeichnisrechte zuverlässig macht.
RUN mkdir -p /data && chown app:app /data && chmod 0770 /data

ENV MCPMCP_DATA_DIR=/data \
    ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
VOLUME /data

ENTRYPOINT ["dotnet", "McpMcp.Server.dll"]
