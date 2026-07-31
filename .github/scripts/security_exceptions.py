#!/usr/bin/env python3
"""Validator und Abfragewerkzeug fuer das zentrale Ausnahmeregister (WP3.5).

Zwei Unterbefehle:

    validate            Prueft `.github/security-exceptions.yml` auf Vollstaendigkeit,
                        Fristen und Drift gegen `.trivyignore.yaml`. Exit 1 bei jedem
                        Verstoss.
    active <tool>       Gibt die IDs der AKTIVEN (nicht abgelaufenen) Ausnahmen fuer ein
                        Werkzeug aus, eine je Zeile. Damit fuettern die Gates ihre
                        `--ignore`-Schalter.

Warum ein eigener Validator statt "wir achten im Review darauf": Ein Review, das eine
Frist pruefen soll, prueft sie beim ersten Mal und danach nicht mehr. Eine Frist, die
niemand maschinell nachhaelt, ist eine Notiz. Dieser Validator laeuft in JEDEM CI-Lauf —
damit verfaellt eine Ausnahme wirklich, statt nur laut Dokument zu verfallen.

Kein PyYAML: Die GitHub-Runner bringen es nicht zwingend mit, und eine Abhaengigkeit
allein fuer diese Datei waere ein schlechter Tausch. Das Register hat eine bewusst enge,
flache Struktur; der Miniparser unten deckt genau sie ab und lehnt alles andere ab,
statt es zu raten.
"""

from __future__ import annotations

import datetime as _dt
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
REGISTER = REPO / ".github" / "security-exceptions.yml"
TRIVYIGNORE = REPO / ".trivyignore.yaml"

ERLAUBTE_WERKZEUGE = {"dotnet", "cargo-audit", "trivy", "gitleaks", "codeql"}
MAX_LAUFZEIT_TAGE = 90
MIN_BEGRUENDUNG = 40

PFLICHTFELDER = ("id", "tool", "reason", "approved_by", "approved_at", "expires")


# ---------------------------------------------------------------------------------------
# Miniparser
# ---------------------------------------------------------------------------------------
def _entferne_kommentare(zeile: str) -> str:
    """Schneidet einen Kommentar ab, laesst '#' innerhalb von Anfuehrungszeichen stehen."""
    heraus, quote = [], None
    for zeichen in zeile:
        if quote:
            if zeichen == quote:
                quote = None
        elif zeichen in "\"'":
            quote = zeichen
        elif zeichen == "#":
            break
        heraus.append(zeichen)
    return "".join(heraus).rstrip()


def lies_register(pfad: pathlib.Path) -> list[dict[str, str]]:
    """Liest die Liste unter `exceptions:`.

    Unterstuetzt Skalare, gequotete Werte und Blockskalare (`>-`, `|`, `>`), weil die
    Begruendung fast immer mehrzeilig ist.
    """
    if not pfad.exists():
        raise SystemExit(f"FEHLER: {pfad} fehlt.")

    zeilen = pfad.read_text(encoding="utf-8").splitlines()

    # Startpunkt: die Zeile `exceptions:` auf Spalte 0.
    start = None
    for i, roh in enumerate(zeilen):
        if _entferne_kommentare(roh).rstrip() in ("exceptions:", "exceptions: []"):
            start = i
            if _entferne_kommentare(roh).rstrip() == "exceptions: []":
                return []
            break
    if start is None:
        raise SystemExit("FEHLER: Schluessel 'exceptions:' nicht gefunden.")

    eintraege: list[dict[str, str]] = []
    aktuell: dict[str, str] | None = None
    block_feld: str | None = None
    block_zeilen: list[str] = []
    block_einzug = 0

    def block_schliessen() -> None:
        nonlocal block_feld, block_zeilen
        if block_feld and aktuell is not None:
            aktuell[block_feld] = " ".join(t.strip() for t in block_zeilen if t.strip())
        block_feld, block_zeilen = None, []

    for roh in zeilen[start + 1 :]:
        # Ein Blockskalar sammelt roh weiter, solange der Einzug tiefer bleibt.
        if block_feld:
            if roh.strip() == "":
                block_zeilen.append("")
                continue
            if len(roh) - len(roh.lstrip()) >= block_einzug:
                block_zeilen.append(roh)
                continue
            block_schliessen()

        ohne = _entferne_kommentare(roh)
        if not ohne.strip():
            continue

        einzug = len(ohne) - len(ohne.lstrip())
        text = ohne.strip()

        # Ein neuer Top-Level-Schluessel beendet die Liste.
        if einzug == 0 and not text.startswith("-"):
            break

        if text.startswith("- "):
            if aktuell:
                eintraege.append(aktuell)
            aktuell = {}
            text = text[2:].strip()
            if not text:
                continue

        if aktuell is None:
            continue

        treffer = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*)$", text)
        if not treffer:
            continue
        feld, wert = treffer.group(1), treffer.group(2).strip()

        if wert in (">-", "|", ">", "|-", ">+", "|+"):
            block_feld = feld
            block_zeilen = []
            block_einzug = einzug + 1
            continue

        if len(wert) >= 2 and wert[0] == wert[-1] and wert[0] in "\"'":
            wert = wert[1:-1]
        aktuell[feld] = wert

    block_schliessen()
    if aktuell:
        eintraege.append(aktuell)
    return eintraege


def _datum(wert: str) -> _dt.date:
    return _dt.datetime.strptime(wert.strip(), "%Y-%m-%d").date()


def lies_trivyignore_ids(pfad: pathlib.Path) -> list[str]:
    """Zieht die `- id: …`-Werte aus `.trivyignore.yaml`. Fehlt die Datei: leere Liste."""
    if not pfad.exists():
        return []
    ids = []
    for roh in pfad.read_text(encoding="utf-8").splitlines():
        treffer = re.match(r"^\s*-?\s*id\s*:\s*(\S+)\s*$", _entferne_kommentare(roh))
        if treffer:
            ids.append(treffer.group(1).strip("\"'"))
    return ids


# ---------------------------------------------------------------------------------------
# validate
# ---------------------------------------------------------------------------------------
def validate() -> int:
    heute = _dt.date.today()
    eintraege = lies_register(REGISTER)
    fehler: list[str] = []
    aktive: list[dict[str, str]] = []

    gesehen: set[tuple[str, str]] = set()

    for nr, eintrag in enumerate(eintraege, start=1):
        kennung = eintrag.get("id", f"<Eintrag {nr} ohne id>")

        fehlend = [f for f in PFLICHTFELDER if not eintrag.get(f)]
        if fehlend:
            fehler.append(f"{kennung}: Pflichtfeld(er) fehlen: {', '.join(fehlend)}")
            continue

        werkzeug = eintrag["tool"]
        if werkzeug not in ERLAUBTE_WERKZEUGE:
            fehler.append(
                f"{kennung}: unbekanntes Werkzeug '{werkzeug}' "
                f"(erlaubt: {', '.join(sorted(ERLAUBTE_WERKZEUGE))})"
            )
            continue

        schluessel = (kennung, werkzeug)
        if schluessel in gesehen:
            fehler.append(f"{kennung}: doppelter Eintrag fuer Werkzeug '{werkzeug}'")
        gesehen.add(schluessel)

        if len(eintrag["reason"].strip()) < MIN_BEGRUENDUNG:
            fehler.append(
                f"{kennung}: 'reason' ist zu duenn ({len(eintrag['reason'].strip())} Zeichen, "
                f"mindestens {MIN_BEGRUENDUNG}). Erwartet wird der Grund der "
                f"Nichterreichbarkeit, nicht der Wunsch, das Gate zu passieren."
            )

        if eintrag["approved_by"].strip().lower() in {"team", "ci", "bot", "dependabot", "-"}:
            fehler.append(
                f"{kennung}: 'approved_by' muss eine Person nennen, nicht "
                f"'{eintrag['approved_by']}'."
            )

        try:
            freigabe = _datum(eintrag["approved_at"])
            ablauf = _datum(eintrag["expires"])
        except ValueError as ausnahme:
            fehler.append(f"{kennung}: Datumsfeld unlesbar ({ausnahme}). Erwartet: YYYY-MM-DD.")
            continue

        if ablauf <= freigabe:
            fehler.append(f"{kennung}: 'expires' liegt nicht nach 'approved_at'.")
        elif (ablauf - freigabe).days > MAX_LAUFZEIT_TAGE:
            fehler.append(
                f"{kennung}: Laufzeit {(ablauf - freigabe).days} Tage ueberschreitet die "
                f"Hoechstdauer von {MAX_LAUFZEIT_TAGE} Tagen."
            )

        if ablauf < heute:
            fehler.append(
                f"{kennung}: ABGELAUFEN am {ablauf} ({(heute - ablauf).days} Tage). "
                f"Entweder den Fund beheben oder mit NEUER Begruendung neu freigeben — "
                f"das Datum allein hochzuschieben ist nicht vorgesehen."
            )
        else:
            aktive.append(eintrag)
            rest = (ablauf - heute).days
            if rest <= 14:
                print(f"WARNUNG: {kennung} laeuft in {rest} Tagen ab ({ablauf}).")

    # Drift: Trivy liest `.trivyignore.yaml` selbst. Was dort steht, aber hier fehlt,
    # waere eine Ausnahme ohne Freigabe und ohne Frist — genau das, was das Register
    # verhindern soll.
    register_trivy = {e["id"] for e in eintraege if e.get("tool") == "trivy" and e.get("id")}
    for kennung in lies_trivyignore_ids(TRIVYIGNORE):
        if kennung not in register_trivy:
            fehler.append(
                f"{kennung}: steht in .trivyignore.yaml, fehlt aber im Register. "
                f"Jede Trivy-Ausnahme braucht hier einen Eintrag mit Freigebendem und Frist."
            )

    print(f"Ausnahmen im Register: {len(eintraege)} (aktiv: {len(aktive)})")
    for eintrag in aktive:
        print(f"  - {eintrag['id']} [{eintrag['tool']}] bis {eintrag['expires']}, "
              f"freigegeben von {eintrag['approved_by']}")

    if fehler:
        print()
        print(f"Das Ausnahmeregister ist nicht gueltig — {len(fehler)} Verstoss/Verstoesse:")
        for eintrag in fehler:
            print(f"  ::error::{eintrag}")
        return 1

    print("Ausnahmeregister gueltig.")
    return 0


# ---------------------------------------------------------------------------------------
# active
# ---------------------------------------------------------------------------------------
def active(werkzeug: str) -> int:
    if werkzeug not in ERLAUBTE_WERKZEUGE:
        print(f"Unbekanntes Werkzeug '{werkzeug}'.", file=sys.stderr)
        return 2
    heute = _dt.date.today()
    for eintrag in lies_register(REGISTER):
        if eintrag.get("tool") != werkzeug or not eintrag.get("expires"):
            continue
        try:
            if _datum(eintrag["expires"]) >= heute:
                print(eintrag["id"])
        except ValueError:
            # Unlesbares Datum heisst: keine gueltige Ausnahme. `validate` meldet es
            # ohnehin — hier still zu schlucken waere die falsche Richtung.
            continue
    return 0


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    if sys.argv[1] == "validate":
        return validate()
    if sys.argv[1] == "active":
        if len(sys.argv) < 3:
            print("Aufruf: security_exceptions.py active <tool>", file=sys.stderr)
            return 2
        return active(sys.argv[2])
    print(f"Unbekannter Unterbefehl '{sys.argv[1]}'.", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
