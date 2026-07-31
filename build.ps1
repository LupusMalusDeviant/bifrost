<#
.SYNOPSIS
    Lokaler Verifier — dieselben Prüfpfade, die auch die CI fährt.

.DESCRIPTION
    Dünne Orchestrierung, KEINE eigene Buildlogik. Jeder Modus ruft genau die Befehle auf, die in
    .github/workflows/ci.yml stehen; Exitcodes werden unverändert durchgereicht.

    Warum das existiert: Ein Mensch und ein Agent sollen dieselben Kommandos benutzen wie die
    Pipeline. Solange jeder seine eigene Befehlsfolge zusammenstellt, heisst "bei mir grün" nichts.

    WICHTIG — die CI bleibt die Referenz. Weicht dieses Skript von ci.yml ab, ist das Skript falsch.

.PARAMETER Mode
    verify-fast       Format + Build + schnelle Suiten (ohne Integration)
    verify-dotnet     vollständige .NET-Suite wie im CI-Job 'build'
    verify-rust       fmt + clippy + cargo test wie im CI-Job 'wasi-spike'
    verify-container  Image bauen und Smoke-Test wie im CI-Job 'container'
    verify-all        alles nacheinander

.EXAMPLE
    ./build.ps1 verify-fast
    ./build.ps1 verify-rust
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('verify-fast', 'verify-dotnet', 'verify-rust', 'verify-container', 'verify-all')]
    [string]$Mode = 'verify-fast'
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$rust = Join-Path $repo 'spikes/wasi-component-runtime'

# Ein Schritt, ein Kommando, ein Exitcode. Kein Retry, kein Weichzeichnen: Ein Schritt, der
# scheitert, beendet den Lauf — sonst liest sich ein roter Durchgang am Ende wie ein gruener.
function Invoke-Step {
    param([string]$Name, [scriptblock]$Command)

    Write-Host ''
    Write-Host "── $Name " -NoNewline -ForegroundColor Cyan
    Write-Host ('─' * [Math]::Max(0, 70 - $Name.Length)) -ForegroundColor DarkCyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FEHLGESCHLAGEN: $Name (Exitcode $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

function Test-Tool {
    param([string]$Name, [string]$Hint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-Host "Uebersprungen: '$Name' ist nicht installiert. $Hint" -ForegroundColor Yellow
        return $false
    }
    return $true
}

function Invoke-VerifyRust {
    if (-not (Test-Tool 'cargo' 'Rust-Gates laufen dann nur in der CI.')) { return }
    Push-Location $rust
    try {
        Invoke-Step 'cargo fmt --check' { cargo fmt --check }
        Invoke-Step 'cargo clippy -D warnings' { cargo clippy --locked --all-targets -- -D warnings }
        Invoke-Step 'cargo test' { cargo test --locked }
    }
    finally { Pop-Location }
}

function Invoke-VerifyFast {
    Invoke-Step 'dotnet build (Release, warnings=errors)' {
        dotnet build $repo/Bifrost.slnx --configuration Release
    }
    # Die Integrationssuite startet echte Prozesse und braucht Minuten — sie gehoert in
    # verify-dotnet, nicht in den schnellen Durchgang.
    Invoke-Step 'dotnet test (Core, Upstream, CLI)' {
        dotnet test $repo/Bifrost.slnx --configuration Release --no-build `
            --filter 'FullyQualifiedName!~Integration.Tests&FullyQualifiedName!~WasiRealHost'
    }
}

function Invoke-VerifyDotnet {
    Invoke-Step 'dotnet build (Release, warnings=errors)' {
        dotnet build $repo/Bifrost.slnx --configuration Release
    }
    # WasiRealHost ist ausgenommen wie in der CI: Diese Tests brauchen das gebaute Rust-Binary und
    # laufen im eigenen Job. Ohne das Binary ueberspringen sie sich selbst — siehe WasiHostPaths.
    Invoke-Step 'dotnet test (vollstaendig, ohne WasiRealHost)' {
        dotnet test $repo/Bifrost.slnx --configuration Release --no-build `
            --filter 'FullyQualifiedName!~WasiRealHost'
    }
}

function Invoke-VerifyContainer {
    if (-not (Test-Tool 'docker' 'Container-Gate laeuft dann nur in der CI.')) { return }
    Invoke-Step 'docker build' { docker build -t bifrost:verify $repo }
    Invoke-Step 'compose-Beispiele sind gueltig' {
        docker compose -f $repo/docker-compose.yml config --quiet
        if ($LASTEXITCODE -eq 0) {
            docker compose -f $repo/docker-compose.yml -f $repo/docker-compose.postgres.yml config --quiet
        }
    }
    Invoke-Step 'non-root im Image' {
        $uid = docker run --rm --entrypoint sh bifrost:verify -c 'id -u'
        if ($uid.Trim() -eq '0') { Write-Host 'Image laeuft als root.' -ForegroundColor Red; exit 1 }
        Write-Host "UID im Container: $uid"
    }
}

switch ($Mode) {
    'verify-fast' { Invoke-VerifyFast }
    'verify-dotnet' { Invoke-VerifyDotnet }
    'verify-rust' { Invoke-VerifyRust }
    'verify-container' { Invoke-VerifyContainer }
    'verify-all' { Invoke-VerifyFast; Invoke-VerifyRust; Invoke-VerifyDotnet; Invoke-VerifyContainer }
}

Write-Host ''
Write-Host "OK — $Mode" -ForegroundColor Green
