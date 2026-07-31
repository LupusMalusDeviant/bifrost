#!/usr/bin/env python3
"""Blockierendes Gate fuer `dotnet list package --vulnerable --include-transitive`.

── Warum dieses Skript ueberhaupt existiert ────────────────────────────────────────────
Weil der naheliegende Workflow-Schritt

    run: dotnet list package --vulnerable --include-transitive

ein Gate ist, das NIE rot werden kann. Nachgemessen am 2026-07-31 gegen ein Projekt mit
Newtonsoft.Json 12.0.1 (GHSA-5crp-9r3c-p9vr, Severity High):

    Für das Projekt "vulnfixture" liegen die folgenden anfälligen Pakete vor.
       > Newtonsoft.Json   12.0.1   12.0.1   High   https://github.com/advisories/GHSA-…
    EXIT=0

Der Befund steht in der Ausgabe, der Exit-Code ist 0. Ein Workflow, der sich auf den
Exit-Code verlaesst, meldet gruen und der Fund faehrt im Release mit. Deshalb wird die
Ausgabe hier ausgewertet statt der Rueckgabewert geglaubt.

── Warum --format json und nicht grep ueber die Tabelle ────────────────────────────────
Die Tabellenausgabe ist LOKALISIERT. Auf dem Entwicklungsrechner (de-DE) lautet die
Ueberschrift "Schweregrad", auf dem Runner "Severity". Ein `grep -i high` haette hier
zufaellig funktioniert und waere bei der naechsten Sprachumgebung still durchgefallen —
also genau die Sorte Gate, die dieses Skript verhindern soll. `--format json` ist
sprachunabhaengig und stabil.

── Was blockiert ───────────────────────────────────────────────────────────────────────
Severity Critical und High. Moderate/Low werden berichtet, blockieren aber nicht
(Blockierregel WP3.5). Ausnahmen kommen ausschliesslich aus dem zentralen Register
(`.github/security-exceptions.yml`), sind befristet und namentlich freigegeben.

Aufruf:
    dotnet_vulnerable_gate.py <json-datei> [--sarif <ausgabe>] [--summary <ausgabe>]
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
HELFER = pathlib.Path(__file__).resolve().parent / "security_exceptions.py"

BLOCKIEREND = {"critical", "high"}
RANG = {"critical": 4, "high": 3, "moderate": 2, "low": 1}


def aktive_ausnahmen() -> set[str]:
    """Holt die aktiven dotnet-Ausnahmen aus dem Register (GHSA-IDs)."""
    try:
        ergebnis = subprocess.run(
            [sys.executable, str(HELFER), "active", "dotnet"],
            capture_output=True, text=True, check=True,
        )
    except (subprocess.CalledProcessError, OSError) as ausnahme:
        # Fail-closed: Wenn das Register nicht lesbar ist, wird NICHT stillschweigend
        # ohne Ausnahmen weitergemacht und auch nicht alles durchgelassen — der Lauf
        # bricht ab. Ein Gate, das bei kaputter Konfiguration raet, ist kein Gate.
        print(f"::error::Ausnahmeregister nicht lesbar: {ausnahme}")
        raise SystemExit(2) from ausnahme
    return {z.strip() for z in ergebnis.stdout.splitlines() if z.strip()}


def advisory_id(url: str) -> str:
    """GHSA-Kennung aus der Advisory-URL (letztes Pfadsegment)."""
    return url.rstrip("/").rsplit("/", 1)[-1] if url else ""


def sammle(daten: dict) -> list[dict]:
    funde = []
    for projekt in daten.get("projects", []):
        pfad = projekt.get("path", "?")
        for framework in projekt.get("frameworks", []) or []:
            ziel = framework.get("framework", "?")
            for art in ("topLevelPackages", "transitivePackages"):
                for paket in framework.get(art, []) or []:
                    for schwachstelle in paket.get("vulnerabilities", []) or []:
                        url = schwachstelle.get("advisoryurl", "")
                        funde.append({
                            "projekt": pathlib.Path(pfad).name,
                            "framework": ziel,
                            "paket": paket.get("id", "?"),
                            "version": paket.get("resolvedVersion", "?"),
                            "transitiv": art == "transitivePackages",
                            "severity": (schwachstelle.get("severity") or "unknown").strip(),
                            "url": url,
                            "id": advisory_id(url),
                        })
    return funde


def sarif(funde: list[dict]) -> dict:
    regeln, ergebnisse, bekannt = [], [], set()
    for fund in funde:
        kennung = fund["id"] or f"{fund['paket']}-{fund['version']}"
        if kennung not in bekannt:
            bekannt.add(kennung)
            regeln.append({
                "id": kennung,
                "name": "VulnerableNuGetPackage",
                "shortDescription": {"text": f"{fund['paket']} {fund['version']}"},
                "fullDescription": {
                    "text": f"{fund['paket']} {fund['version']} weist eine bekannte "
                            f"Schwachstelle auf ({fund['severity']})."
                },
                "helpUri": fund["url"] or "https://github.com/advisories",
                "properties": {"security-severity": {
                    "critical": "9.5", "high": "7.5", "moderate": "5.0", "low": "2.0",
                }.get(fund["severity"].lower(), "0.0")},
                "defaultConfiguration": {
                    "level": "error" if fund["severity"].lower() in BLOCKIEREND else "warning"
                },
            })
        ergebnisse.append({
            "ruleId": kennung,
            "level": "error" if fund["severity"].lower() in BLOCKIEREND else "warning",
            "message": {"text": (
                f"{fund['paket']} {fund['version']} ({fund['severity']}) in "
                f"{fund['projekt']} [{fund['framework']}]"
                f"{' — transitiv' if fund['transitiv'] else ''}. {fund['url']}"
            )},
            # Verankert an Directory.Packages.props: dort stehen die Versionen dieses
            # Repositories (zentrale Paketverwaltung). Ohne Location verwirft die
            # Code-Scanning-API das Ergebnis.
            "locations": [{"physicalLocation": {
                "artifactLocation": {"uri": "Directory.Packages.props"},
                "region": {"startLine": 1},
            }}],
        })
    return {
        "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
        "version": "2.1.0",
        "runs": [{
            "tool": {"driver": {
                "name": "dotnet-list-package-vulnerable",
                "informationUri": "https://learn.microsoft.com/dotnet/core/tools/dotnet-list-package",
                "rules": regeln,
            }},
            "results": ergebnisse,
        }],
    }


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    quelle = pathlib.Path(sys.argv[1])
    sarif_ziel = summary_ziel = None
    for i, arg in enumerate(sys.argv):
        if arg == "--sarif" and i + 1 < len(sys.argv):
            sarif_ziel = pathlib.Path(sys.argv[i + 1])
        if arg == "--summary" and i + 1 < len(sys.argv):
            summary_ziel = pathlib.Path(sys.argv[i + 1])

    if not quelle.exists():
        print(f"::error::Ausgabedatei {quelle} fehlt — lief `dotnet list package` ueberhaupt?")
        return 2

    roh = quelle.read_text(encoding="utf-8-sig").strip()
    # `dotnet list package` stellt der JSON-Ausgabe Restore-Meldungen voran.
    if not roh.startswith("{"):
        klammer = roh.find("{")
        if klammer < 0:
            print("::error::Keine JSON-Ausgabe gefunden.")
            return 2
        roh = roh[klammer:]

    try:
        daten = json.loads(roh)
    except json.JSONDecodeError as ausnahme:
        print(f"::error::JSON-Ausgabe unlesbar: {ausnahme}")
        return 2

    funde = sammle(daten)
    ausnahmen = aktive_ausnahmen()

    blockierend, entschuldigt, nachrichtlich = [], [], []
    for fund in funde:
        if fund["severity"].lower() not in BLOCKIEREND:
            nachrichtlich.append(fund)
        elif fund["id"] and fund["id"] in ausnahmen:
            entschuldigt.append(fund)
        else:
            blockierend.append(fund)

    blockierend.sort(key=lambda f: -RANG.get(f["severity"].lower(), 0))

    if sarif_ziel:
        sarif_ziel.parent.mkdir(parents=True, exist_ok=True)
        sarif_ziel.write_text(json.dumps(sarif(funde), indent=2), encoding="utf-8")
        print(f"SARIF geschrieben: {sarif_ziel} ({len(funde)} Ergebnis(se))")

    zeilen = ["### NuGet-Schwachstellen (`dotnet list package --vulnerable`)", ""]
    if not funde:
        zeilen.append("Keine anfaelligen Pakete — direkt oder transitiv.")
    else:
        zeilen += ["| Paket | Version | Severity | Projekt | Status |", "|---|---|---|---|---|"]
        for fund in blockierend + entschuldigt + nachrichtlich:
            status = ("**blockiert**" if fund in blockierend
                      else "befristete Ausnahme" if fund in entschuldigt
                      else "nachrichtlich (< High)")
            zeilen.append(
                f"| [{fund['paket']}]({fund['url']}) | {fund['version']} | "
                f"{fund['severity']} | {fund['projekt']} | {status} |"
            )
    zeilen.append("")

    bericht = "\n".join(zeilen)
    print(bericht)
    if summary_ziel:
        with summary_ziel.open("a", encoding="utf-8") as datei:
            datei.write(bericht + "\n")

    for fund in entschuldigt:
        print(f"::warning::{fund['id']} ({fund['paket']} {fund['version']}, "
              f"{fund['severity']}) laeuft unter einer befristeten Ausnahme.")

    if blockierend:
        for fund in blockierend:
            print(f"::error::{fund['paket']} {fund['version']} — {fund['severity']} "
                  f"({fund['id'] or 'ohne ID'}) in {fund['projekt']}. {fund['url']}")
        print(f"::error::{len(blockierend)} Critical/High-Schwachstelle(n) in "
              f"NuGet-Abhaengigkeiten. Der Release wird blockiert.")
        return 1

    print("Gate bestanden: keine ungedeckten Critical/High-Funde in NuGet-Abhaengigkeiten.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
