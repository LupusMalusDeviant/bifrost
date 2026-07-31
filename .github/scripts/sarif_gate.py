#!/usr/bin/env python3
"""Blockierregel fuer SARIF-Ergebnisse (WP3.5).

── Warum das noetig ist ────────────────────────────────────────────────────────────────
`github/codeql-action/analyze` laedt Ergebnisse in die Code-Scanning-Ansicht hoch und
endet dabei mit Exit 0 — auch bei einem Critical-Fund. Das ist fuer eine Uebersichtsseite
richtig und fuer ein Gate falsch. Ohne diesen Schritt waere CodeQL eine Anzeige, kein Tor:
Der Release liefe weiter, und der Fund stuende in einer Ansicht, die im Zweifel niemand
oeffnet, bevor das Tag gesetzt ist.

── Wie die Schwere bestimmt wird ───────────────────────────────────────────────────────
SARIF kennt zwei Angaben nebeneinander:
  * `level` (error/warning/note) — grob, sagt nichts ueber Sicherheitsrelevanz;
  * `properties.security-severity` — ein CVSS-artiger Zahlenwert, den CodeQL und Trivy
    beide setzen.
Massgeblich ist die Zahl (>= 7.0 entspricht High, >= 9.0 Critical, GitHubs eigene
Einteilung). Fehlt sie, wird auf `level` zurueckgefallen — dann zaehlt `error` als High.
Ein Ergebnis ohne beides wird NICHT stillschweigend als harmlos gewertet, sondern
gemeldet und als unbestimmt gezaehlt.

Aufruf:
    sarif_gate.py <datei.sarif> [weitere.sarif …] --name <Anzeigename>
                  [--threshold 7.0] [--summary <datei>]
"""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys

HELFER = pathlib.Path(__file__).resolve().parent / "security_exceptions.py"

# GitHubs Zuordnung von security-severity auf die Stufen der Code-Scanning-Ansicht.
SCHWELLE_HIGH = 7.0
SCHWELLE_CRITICAL = 9.0


def aktive_ausnahmen(werkzeug: str) -> set[str]:
    try:
        ergebnis = subprocess.run(
            [sys.executable, str(HELFER), "active", werkzeug],
            capture_output=True, text=True, check=True,
        )
    except (subprocess.CalledProcessError, OSError) as ausnahme:
        print(f"::error::Ausnahmeregister nicht lesbar: {ausnahme}")
        raise SystemExit(2) from ausnahme
    return {z.strip() for z in ergebnis.stdout.splitlines() if z.strip()}


def stufe(wert: float | None, level: str) -> str:
    if wert is None:
        return "unbestimmt" if level not in ("error", "warning", "note") else (
            "high" if level == "error" else "niedrig"
        )
    if wert >= SCHWELLE_CRITICAL:
        return "critical"
    if wert >= SCHWELLE_HIGH:
        return "high"
    return "niedrig"


def regel_index(lauf: dict) -> dict[str, dict]:
    treiber = lauf.get("tool", {}).get("driver", {})
    regeln = {r.get("id"): r for r in treiber.get("rules", []) or []}
    for erweiterung in treiber.get("extensions", []) or []:
        for regel in erweiterung.get("rules", []) or []:
            regeln.setdefault(regel.get("id"), regel)
    for erweiterung in lauf.get("tool", {}).get("extensions", []) or []:
        for regel in erweiterung.get("rules", []) or []:
            regeln.setdefault(regel.get("id"), regel)
    return regeln


def severity_von(ergebnis: dict, regel: dict | None) -> float | None:
    for quelle in (ergebnis, regel or {}):
        eigenschaften = quelle.get("properties", {}) or {}
        roh = eigenschaften.get("security-severity")
        if roh is None:
            continue
        if isinstance(roh, dict):  # manche Werkzeuge verschachteln
            roh = roh.get("value") or roh.get("text")
        try:
            return float(str(roh))
        except (TypeError, ValueError):
            continue
    return None


def main() -> int:
    argumente = sys.argv[1:]
    if not argumente:
        print(__doc__)
        return 2

    dateien: list[pathlib.Path] = []
    name, schwelle, summary, werkzeug = "SARIF", SCHWELLE_HIGH, None, "codeql"
    i = 0
    while i < len(argumente):
        arg = argumente[i]
        if arg == "--name":
            name = argumente[i + 1]; i += 2
        elif arg == "--threshold":
            schwelle = float(argumente[i + 1]); i += 2
        elif arg == "--summary":
            summary = pathlib.Path(argumente[i + 1]); i += 2
        elif arg == "--tool":
            werkzeug = argumente[i + 1]; i += 2
        else:
            dateien.append(pathlib.Path(arg)); i += 1

    vorhanden = [d for d in dateien if d.exists()]
    if not vorhanden:
        # Fail-closed: Eine fehlende SARIF-Datei heisst nicht "nichts gefunden", sondern
        # "die Analyse ist nicht gelaufen". Das als gruen zu werten waere die
        # gefaehrlichste Variante dieses Skripts.
        print(f"::error::Keine SARIF-Datei gefunden ({', '.join(str(d) for d in dateien)}). "
              f"Die Analyse '{name}' ist offenbar nicht gelaufen — das Gate wertet das als "
              f"Fehlschlag, nicht als Unbedenklichkeit.")
        return 2

    ausnahmen = aktive_ausnahmen(werkzeug)
    blockierend, entschuldigt, unbestimmt, harmlos = [], [], [], 0

    for datei in vorhanden:
        try:
            daten = json.loads(datei.read_text(encoding="utf-8-sig"))
        except (json.JSONDecodeError, OSError) as ausnahme:
            print(f"::error::{datei} nicht lesbar: {ausnahme}")
            return 2

        for lauf in daten.get("runs", []) or []:
            regeln = regel_index(lauf)
            for ergebnis in lauf.get("results", []) or []:
                regel_id = ergebnis.get("ruleId") or "<ohne Regel-ID>"
                regel = regeln.get(regel_id)
                wert = severity_von(ergebnis, regel)
                level = (ergebnis.get("level")
                         or (regel or {}).get("defaultConfiguration", {}).get("level")
                         or "warning")
                einstufung = stufe(wert, level)

                ort = "?"
                orte = ergebnis.get("locations") or []
                if orte:
                    physisch = orte[0].get("physicalLocation", {})
                    ort = physisch.get("artifactLocation", {}).get("uri", "?")
                    zeile = physisch.get("region", {}).get("startLine")
                    if zeile:
                        ort = f"{ort}:{zeile}"

                eintrag = {
                    "regel": regel_id,
                    "ort": ort,
                    "wert": wert,
                    "stufe": einstufung,
                    "text": (ergebnis.get("message", {}) or {}).get("text", "")[:200],
                }

                blockt = (wert is not None and wert >= schwelle) or \
                         (wert is None and einstufung == "high")
                if einstufung == "unbestimmt":
                    unbestimmt.append(eintrag)
                elif not blockt:
                    harmlos += 1
                elif regel_id in ausnahmen:
                    entschuldigt.append(eintrag)
                else:
                    blockierend.append(eintrag)

    blockierend.sort(key=lambda e: -(e["wert"] or 0))

    zeilen = [f"### {name}", ""]
    if not blockierend and not entschuldigt and not unbestimmt:
        zeilen.append(f"Keine Funde ab Schwelle {schwelle} "
                      f"({harmlos} Ergebnis(se) unterhalb der Schwelle).")
    else:
        zeilen += ["| Regel | Ort | security-severity | Status |", "|---|---|---|---|"]
        for eintrag in blockierend:
            zeilen.append(f"| `{eintrag['regel']}` | {eintrag['ort']} | "
                          f"{eintrag['wert']} | **blockiert** |")
        for eintrag in entschuldigt:
            zeilen.append(f"| `{eintrag['regel']}` | {eintrag['ort']} | "
                          f"{eintrag['wert']} | befristete Ausnahme |")
        for eintrag in unbestimmt:
            zeilen.append(f"| `{eintrag['regel']}` | {eintrag['ort']} | "
                          f"— | unbestimmt, siehe Warnung |")
        zeilen.append("")
        zeilen.append(f"Unterhalb der Schwelle: {harmlos}")
    zeilen.append("")

    bericht = "\n".join(zeilen)
    print(bericht)
    if summary:
        with summary.open("a", encoding="utf-8") as datei:
            datei.write(bericht + "\n")

    for eintrag in unbestimmt:
        print(f"::warning::{eintrag['regel']} ({eintrag['ort']}) traegt weder "
              f"security-severity noch ein verwertbares level. Bitte einstufen.")
    for eintrag in entschuldigt:
        print(f"::warning::{eintrag['regel']} laeuft unter einer befristeten Ausnahme.")

    if blockierend:
        for eintrag in blockierend:
            print(f"::error::{eintrag['regel']} ({eintrag['stufe']}, "
                  f"security-severity {eintrag['wert']}) in {eintrag['ort']}: {eintrag['text']}")
        print(f"::error::{name}: {len(blockierend)} Fund(e) ab Schwelle {schwelle}. "
              f"Der Release wird blockiert.")
        return 1

    print(f"{name}: Gate bestanden.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
