//! Platten-Cache für kompilierte Components (Plan 0003, WP5).
//!
//! Der prozesslokale Cache deckt jeden Aufruf nach dem ersten ab; was er nicht deckt, ist der
//! **Host-Start**. Gemessen kostet die Kompilierung rund 2,3 ms je KiB, ein Plugin von 1–3 MB also
//! 3–7 Sekunden — bei jedem Gateway-Neustart, jedem Hot-Swap, jedem Supervisor-Restart.
//!
//! # Warum das ein Sicherheitsthema ist
//!
//! Ein Kompilat (`Component::serialize`) ist **ausführbarer Maschinencode**, und das Einlesen
//! (`Component::deserialize`) führt ihn dem Prozess zu. Die Ed25519-Signaturkette deckt die
//! `.wasm`-Bytes ab — ein Kompilat auf Platte steht daneben. Wer in das Cache-Verzeichnis schreiben
//! kann, hätte damit Codeausführung im Host, ohne dass Publisher-Pinning oder Grants greifen.
//!
//! Deshalb trägt jeder Eintrag einen **HMAC-SHA256 unter einem host-lokalen Schlüssel**, der beim
//! ersten Start im Cache-Verzeichnis erzeugt wird (unter Unix mit `0600`). Ein Eintrag ohne
//! gültigen MAC wird nicht gelesen, sondern gelöscht; danach wird neu kompiliert.
//!
//! **Was das leistet und was nicht:** Es schützt gegen einen Angreifer mit *Schreibzugriff* auf das
//! Verzeichnis (geteiltes Volume, zu weit gesetzte Rechte) und gegen Bitfehler. Es schützt **nicht**
//! gegen jemanden, der als derselbe Benutzer läuft wie der Host — der liest den Schlüssel und
//! könnte ohnehin gleich das Host-Binary austauschen. Der Schlüssel hebt die Hürde auf
//! „gleicher Benutzer", nicht darüber hinaus.

use std::io::Write as _;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use hmac::{Hmac, Mac};
use sha2::Sha256;
use wasmtime::Engine;
use wasmtime::component::Component;

use crate::sha256_hex;

type HmacSha256 = Hmac<Sha256>;

/// Dateikennung, damit ein fremder Dateiinhalt nicht als Eintrag missverstanden wird.
const MAGIC: &[u8; 8] = b"BIFROSTC";
/// Format-Version des Eintrags; ein Wechsel macht alte Einträge ungültig statt sie zu misslesen.
const FORMAT_VERSION: u8 = 1;
const KEY_FILE: &str = "mac.key";
const KEY_BYTES: usize = 32;
/// Dateiendung der Einträge. Das Aufräumen fasst nur diese an — `mac.key` bleibt liegen.
const ENTRY_EXTENSION: &str = "cwasm";

/// Vorgabe für die Belegung des Cache-Verzeichnisses. Ein Kompilat ist deutlich größer als das
/// Component; 256 MiB fassen auch mehrere große Plugins und bleiben für ein Datenvolumen zumutbar.
pub const DEFAULT_MAX_DISK_BYTES: u64 = 256 * 1024 * 1024;

/// Ergebnis eines Aufräumdurchgangs: Belegung danach und wie viele Einträge dafür weichen mussten.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PruneOutcome {
    pub bytes: u64,
    pub evicted: u64,
}

/// Ein Verzeichnis mit MAC-gesicherten Kompilaten plus dem host-lokalen Schlüssel dazu.
pub struct DiskCache {
    directory: PathBuf,
    mac_key: [u8; KEY_BYTES],
    /// Obergrenze der Belegung. Überschreitung räumt die am längsten nicht genutzten Einträge ab.
    max_bytes: u64,
}

impl std::fmt::Debug for DiskCache {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Der Schlüssel gehört in keine Log- oder Debug-Ausgabe.
        formatter
            .debug_struct("DiskCache")
            .field("directory", &self.directory)
            .finish_non_exhaustive()
    }
}

impl DiskCache {
    /// Öffnet das Cache-Verzeichnis und lädt den host-lokalen Schlüssel — oder legt beides an.
    pub fn open(directory: impl AsRef<Path>) -> Result<Self> {
        let directory = directory.as_ref().to_path_buf();
        std::fs::create_dir_all(&directory).with_context(|| {
            format!("Cache-Verzeichnis '{}' nicht anlegbar", directory.display())
        })?;
        let mac_key = load_or_create_key(&directory.join(KEY_FILE))?;
        Ok(Self {
            directory,
            mac_key,
            max_bytes: DEFAULT_MAX_DISK_BYTES,
        })
    }

    /// Wie [`Self::open`], aber mit eigener Obergrenze. `0` bedeutet „unbegrenzt" — bewusst
    /// ausdrücklich, damit niemand versehentlich einen Cache ohne Grenze bekommt.
    pub fn open_with_budget(directory: impl AsRef<Path>, max_bytes: u64) -> Result<Self> {
        Ok(Self {
            max_bytes,
            ..Self::open(directory)?
        })
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    pub fn max_bytes(&self) -> u64 {
        self.max_bytes
    }

    /// Liest ein Kompilat, wenn eines für genau diesen Cache-Schlüssel vorliegt und sein MAC passt.
    ///
    /// Ein unbrauchbarer Eintrag ist kein Fehler nach außen, sondern ein Miss: Der Aufrufer
    /// kompiliert dann neu. Wer hier hart scheitern ließe, machte einen kaputten Cache zu einem
    /// Ausfall des Upstreams.
    pub fn load(&self, cache_key: &str, engine: &Engine) -> Option<Component> {
        let path = self.entry_path(cache_key);
        let raw = std::fs::read(&path).ok()?;
        match self.verify(cache_key, &raw) {
            Ok(precompiled) => {
                // SAFETY: `deserialize` führt vorkompilierten Maschinencode zu. Zulässig ist das
                // hier, weil (1) der Inhalt einen gültigen HMAC unter dem host-lokalen Schlüssel
                // trägt, also von diesem Host stammt, (2) der eingebettete Cache-Schlüssel
                // — Modulhash, Runtime-Version, Engine-Profil, Grants — mit dem angefragten
                // übereinstimmt, und (3) wasmtime zusätzlich seine eigene Kompatibilitätsprüfung
                // ausführt und bei fremdem Erzeuger einen Fehler liefert statt zu laufen.
                match unsafe { Component::deserialize(engine, precompiled) } {
                    Ok(component) => {
                        Self::touch(&path);
                        Some(component)
                    }
                    Err(failure) => {
                        eprintln!(
                            "wasi-host: Kompilat '{}' nicht ladbar ({failure}) — wird neu erzeugt",
                            path.display()
                        );
                        let _ = std::fs::remove_file(&path);
                        None
                    }
                }
            }
            Err(failure) => {
                // Bewusst laut: Ein MAC-Fehler ist entweder ein Schlüsselwechsel oder ein
                // Manipulationsversuch. Beides will man im Log sehen.
                eprintln!(
                    "wasi-host: Cache-Eintrag '{}' verworfen: {failure}",
                    path.display()
                );
                let _ = std::fs::remove_file(&path);
                None
            }
        }
    }

    /// Legt ein Kompilat ab und räumt danach auf. Atomar über temporäre Datei plus Rename, damit
    /// ein zweiter Host nie einen halb geschriebenen Eintrag sieht.
    pub fn store(&self, cache_key: &str, component: &Component) -> Result<PruneOutcome> {
        let precompiled = component.serialize()?;
        let key_bytes = cache_key.as_bytes();
        let key_len = u32::try_from(key_bytes.len())?;

        let mut signed = Vec::with_capacity(1 + 4 + key_bytes.len() + precompiled.len());
        signed.push(FORMAT_VERSION);
        signed.extend_from_slice(&key_len.to_be_bytes());
        signed.extend_from_slice(key_bytes);
        signed.extend_from_slice(&precompiled);

        let mut body = Vec::with_capacity(MAGIC.len() + 32 + signed.len());
        body.extend_from_slice(MAGIC);
        body.extend_from_slice(&self.mac(&signed));
        body.extend_from_slice(&signed);

        let target = self.entry_path(cache_key);
        let temporary = target.with_extension(format!("tmp-{}", std::process::id()));
        {
            let mut file = std::fs::File::create(&temporary).with_context(|| {
                format!("Cache-Eintrag '{}' nicht schreibbar", temporary.display())
            })?;
            file.write_all(&body)?;
            file.sync_all()?;
        }
        std::fs::rename(&temporary, &target)?;
        Ok(self.prune())
    }

    /// Räumt auf, bis die Belegung unter der Obergrenze liegt: älteste Änderungszeit zuerst.
    ///
    /// Ein Treffer stempelt seinen Eintrag frisch (siehe [`Self::load`]), damit „alt" hier
    /// „lange nicht gebraucht" heißt und nicht „lange her geschrieben". Fehler beim Aufräumen sind
    /// keine Aufruf-Fehler: Ein voller oder gesperrter Cache kostet Kompilierzeit, nichts weiter.
    /// Räumen zwei Hosts gleichzeitig, kann einer einen Eintrag löschen, den der andere gerade
    /// lesen wollte — das ergibt einen Miss und eine Neukompilierung.
    pub fn prune(&self) -> PruneOutcome {
        let mut entries: Vec<(std::time::SystemTime, u64, PathBuf)> = Vec::new();
        let Ok(listing) = std::fs::read_dir(&self.directory) else {
            return PruneOutcome::default();
        };

        for entry in listing.flatten() {
            let path = entry.path();
            if path.extension().and_then(|extension| extension.to_str()) != Some(ENTRY_EXTENSION) {
                continue;
            }
            if let Ok(metadata) = entry.metadata() {
                let modified = metadata.modified().unwrap_or(std::time::UNIX_EPOCH);
                entries.push((modified, metadata.len(), path));
            }
        }

        let mut total: u64 = entries.iter().map(|(_, size, _)| size).sum();
        if self.max_bytes == 0 {
            return PruneOutcome {
                bytes: total,
                evicted: 0,
            };
        }

        // Ältester zuerst — der wird als erster geopfert.
        entries.sort_by_key(|(modified, _, _)| *modified);
        let mut evicted = 0;
        for (_, size, path) in &entries {
            if total <= self.max_bytes {
                break;
            }
            if std::fs::remove_file(path).is_ok() {
                total = total.saturating_sub(*size);
                evicted += 1;
            }
        }

        PruneOutcome {
            bytes: total,
            evicted,
        }
    }

    /// Stempelt einen Eintrag als gerade benutzt. Ohne das verdrängte das Aufräumen nach
    /// Schreibalter statt nach Nutzung — und würde bevorzugt das Kompilat wegwerfen, das seit
    /// Wochen bei jedem Start gebraucht wird.
    fn touch(path: &Path) {
        if let Ok(file) = std::fs::OpenOptions::new().write(true).open(path) {
            let _ = file.set_modified(std::time::SystemTime::now());
        }
    }

    /// Prüft Kennung, Version, MAC und den eingebetteten Cache-Schlüssel; gibt das Kompilat zurück.
    fn verify<'a>(&self, cache_key: &str, raw: &'a [u8]) -> Result<&'a [u8]> {
        let after_magic = raw
            .strip_prefix(MAGIC.as_slice())
            .context("fremder Dateiinhalt (Kennung fehlt)")?;
        let (mac, rest) = after_magic
            .split_at_checked(32)
            .context("Eintrag zu kurz für einen MAC")?;

        // MAC zuerst: Erst danach werden die Felder überhaupt interpretiert.
        let expected = self.mac(rest);
        HmacSha256::new_from_slice(&self.mac_key)
            .expect("HMAC nimmt jede Schlüssellänge")
            .chain_update(rest)
            .verify_slice(mac)
            .map_err(|_| anyhow::anyhow!("MAC passt nicht (fremder oder veränderter Eintrag)"))?;
        debug_assert_eq!(expected.as_slice(), mac);

        let (version, rest) = rest.split_first().context("Eintrag ohne Version")?;
        if *version != FORMAT_VERSION {
            bail!("Formatversion {version} statt {FORMAT_VERSION}");
        }
        let (length, rest) = rest
            .split_at_checked(4)
            .context("Eintrag ohne Schlüssellänge")?;
        let length = u32::from_be_bytes(length.try_into().expect("vier Bytes")) as usize;
        let (embedded, precompiled) = rest
            .split_at_checked(length)
            .context("Eintrag kürzer als angekündigt")?;

        // Der eingebettete Schlüssel MUSS verglichen werden: Ohne das ließe sich eine gültige Datei
        // auf den Dateinamen eines anderen Eintrags umbenennen, und der MAC stimmte weiterhin.
        if embedded != cache_key.as_bytes() {
            bail!("Eintrag gehört zu einem anderen Cache-Schlüssel");
        }
        Ok(precompiled)
    }

    fn mac(&self, message: &[u8]) -> [u8; 32] {
        let mut mac = HmacSha256::new_from_slice(&self.mac_key).expect("HMAC nimmt jede Länge");
        mac.update(message);
        mac.finalize().into_bytes().into()
    }

    /// Dateiname = Hash des Cache-Schlüssels: Der Schlüssel selbst enthält `:` und `=` und wäre
    /// unter Windows kein gültiger Pfad.
    fn entry_path(&self, cache_key: &str) -> PathBuf {
        self.directory
            .join(format!("{}.cwasm", sha256_hex(cache_key.as_bytes())))
    }
}

/// Lädt den host-lokalen MAC-Schlüssel oder erzeugt ihn. `create_new` entscheidet das Rennen
/// zwischen zwei gleichzeitig startenden Hosts: Der Verlierer liest den Schlüssel des Gewinners.
fn load_or_create_key(path: &Path) -> Result<[u8; KEY_BYTES]> {
    match std::fs::read(path) {
        Ok(existing) if existing.len() == KEY_BYTES => {
            return Ok(existing.try_into().expect("Länge geprüft"));
        }
        Ok(_) => bail!(
            "Schlüsseldatei '{}' hat nicht {KEY_BYTES} Byte — bitte löschen, damit ein neuer \
             Schlüssel erzeugt wird (alle Einträge werden dann ungültig)",
            path.display()
        ),
        Err(error) if error.kind() != std::io::ErrorKind::NotFound => {
            return Err(error)
                .with_context(|| format!("Schlüsseldatei '{}' nicht lesbar", path.display()));
        }
        Err(_) => {}
    }

    let mut key = [0u8; KEY_BYTES];
    getrandom::fill(&mut key).context("kein Zufall für den Cache-Schlüssel verfügbar")?;

    let mut options = std::fs::OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        // Nur der Host-Benutzer darf den Schlüssel lesen — er ist der ganze Schutz des Caches.
        options.mode(0o600);
    }

    match options.open(path) {
        Ok(mut file) => {
            file.write_all(&key)?;
            file.sync_all()?;
            Ok(key)
        }
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
            let existing = std::fs::read(path)?;
            existing.try_into().map_err(|_| {
                anyhow::anyhow!("gleichzeitig erzeugte Schlüsseldatei ist unbrauchbar")
            })
        }
        Err(error) => Err(error)
            .with_context(|| format!("Schlüsseldatei '{}' nicht anlegbar", path.display())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{CapabilityGrants, ExecutionLimits, ModuleCache};

    const GUEST: &[u8] = include_bytes!("../fixtures/wasi-p2-guest.component.wasm");

    fn scratch(name: &str) -> PathBuf {
        let path =
            std::env::temp_dir().join(format!("bifrost-diskcache-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&path);
        path
    }

    fn engine() -> Engine {
        crate::hardened_engine(true, true).unwrap()
    }

    #[test]
    fn a_stored_compilation_comes_back() -> Result<()> {
        let directory = scratch("roundtrip");
        let cache = DiskCache::open(&directory)?;
        let engine = engine();
        let component = Component::from_binary(&engine, GUEST)?;

        cache.store("schluessel-a", &component)?;
        let loaded = cache.load("schluessel-a", &engine);

        assert!(loaded.is_some(), "der Eintrag muss zurückkommen");
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    #[test]
    fn an_entry_for_another_key_is_not_served() -> Result<()> {
        let directory = scratch("otherkey");
        let cache = DiskCache::open(&directory)?;
        let engine = engine();
        cache.store("schluessel-a", &Component::from_binary(&engine, GUEST)?)?;

        assert!(cache.load("schluessel-b", &engine).is_none());
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    /// Der Kern der Absicherung: Ein manipuliertes Kompilat wird nicht geladen, sondern entfernt.
    #[test]
    fn a_tampered_entry_is_rejected_and_removed() -> Result<()> {
        let directory = scratch("tampered");
        let cache = DiskCache::open(&directory)?;
        let engine = engine();
        cache.store("schluessel", &Component::from_binary(&engine, GUEST)?)?;
        let path = cache.entry_path("schluessel");

        // Ein Byte im Kompilat kippen — genau der Fall, den der MAC abdecken soll.
        let mut raw = std::fs::read(&path)?;
        let last = raw.len() - 1;
        raw[last] ^= 0xFF;
        std::fs::write(&path, &raw)?;

        assert!(cache.load("schluessel", &engine).is_none());
        assert!(
            !path.exists(),
            "ein verworfener Eintrag darf nicht liegen bleiben"
        );
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    /// Eine Datei, die eine andere Signatur trägt (fremder Schlüssel), ist wertlos — das ist der
    /// Unterschied zwischen „irgendein Kompilat" und „ein Kompilat dieses Hosts".
    #[test]
    fn an_entry_from_another_host_key_is_rejected() -> Result<()> {
        let directory = scratch("foreignkey");
        let engine = engine();
        {
            let cache = DiskCache::open(&directory)?;
            cache.store("schluessel", &Component::from_binary(&engine, GUEST)?)?;
        }

        // Schlüsselwechsel simulieren: Datei ersetzen, Eintrag liegen lassen.
        std::fs::write(directory.join(KEY_FILE), [7u8; KEY_BYTES])?;
        let rotated = DiskCache::open(&directory)?;

        assert!(rotated.load("schluessel", &engine).is_none());
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    #[test]
    fn a_foreign_file_is_ignored() -> Result<()> {
        let directory = scratch("foreignfile");
        let cache = DiskCache::open(&directory)?;
        std::fs::write(cache.entry_path("schluessel"), b"ich bin kein Kompilat")?;

        assert!(cache.load("schluessel", &engine()).is_none());
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    /// Der eigentliche Zweck: Ein **neuer Prozess** (neuer `ModuleCache`) findet das Kompilat auf
    /// Platte und kompiliert nicht erneut.
    #[test]
    fn a_second_process_starts_warm() -> Result<()> {
        let directory = scratch("warmstart");
        let grants = CapabilityGrants::default();
        let limits = ExecutionLimits::default();

        let mut first = ModuleCache::with_disk(DiskCache::open(&directory)?);
        first.compile(GUEST, &grants, &limits)?;
        assert_eq!(first.stats().misses, 1, "der erste Lauf kompiliert");
        assert_eq!(first.stats().disk_hits, 0);

        // Zweiter Host-Start: leerer Speicher-Cache, gleiches Verzeichnis.
        let mut second = ModuleCache::with_disk(DiskCache::open(&directory)?);
        second.compile(GUEST, &grants, &limits)?;

        assert_eq!(second.stats().disk_hits, 1, "das Kompilat kam von Platte");
        assert_eq!(second.stats().misses, 0, "und wurde nicht neu erzeugt");
        assert_eq!(second.stats().last_compile_ms, 0.0);
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    /// Obergrenze auf Platte: Über dem Budget wird abgeräumt, statt das Verzeichnis unbegrenzt
    /// wachsen zu lassen. Der jüngste Eintrag überlebt.
    #[test]
    fn the_disk_budget_evicts_the_oldest_entry() -> Result<()> {
        let directory = scratch("budget");
        let engine = engine();
        let component = Component::from_binary(&engine, GUEST)?;
        // Budget knapp unter zwei Einträgen: der zweite Store muss den ersten verdrängen.
        let one = {
            let unbounded = DiskCache::open_with_budget(&directory, 0)?;
            unbounded.store("erster", &component)?;
            let size = unbounded.prune().bytes;
            std::fs::remove_file(unbounded.entry_path("erster"))?;
            size
        };
        let cache = DiskCache::open_with_budget(&directory, one + one / 2)?;

        cache.store("alt", &component)?;
        // Änderungszeiten müssen unterscheidbar sein, sonst ist „ältester" nicht bestimmt.
        std::fs::OpenOptions::new()
            .write(true)
            .open(cache.entry_path("alt"))?
            .set_modified(std::time::SystemTime::now() - std::time::Duration::from_secs(60))?;
        let outcome = cache.store("neu", &component)?;

        assert_eq!(outcome.evicted, 1, "ein Eintrag musste weichen");
        assert!(
            outcome.bytes <= cache.max_bytes(),
            "Budget muss danach halten"
        );
        assert!(cache.load("neu", &engine).is_some(), "der jüngste bleibt");
        assert!(cache.load("alt", &engine).is_none(), "der älteste ist weg");
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    /// Ein Treffer stempelt seinen Eintrag frisch — sonst würde das Aufräumen bevorzugt das
    /// Kompilat wegwerfen, das bei jedem Start gebraucht wird, nur weil es lange geschrieben ist.
    #[test]
    fn a_hit_refreshes_the_entry_against_eviction() -> Result<()> {
        let directory = scratch("touch");
        let engine = engine();
        let cache = DiskCache::open(&directory)?;
        cache.store("heiss", &Component::from_binary(&engine, GUEST)?)?;
        let path = cache.entry_path("heiss");
        let long_ago = std::time::SystemTime::now() - std::time::Duration::from_secs(3600);
        std::fs::OpenOptions::new()
            .write(true)
            .open(&path)?
            .set_modified(long_ago)?;

        assert!(cache.load("heiss", &engine).is_some());

        let refreshed = std::fs::metadata(&path)?.modified()?;
        assert!(
            refreshed > long_ago,
            "der Treffer muss die Nutzung vermerken"
        );
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    /// Aufräumen fasst nur Einträge an — der Schlüssel überlebt, sonst wären mit einem Mal alle
    /// Kompilate ungültig.
    #[test]
    fn pruning_never_touches_the_key_file() -> Result<()> {
        let directory = scratch("keysafe");
        let cache = DiskCache::open_with_budget(&directory, 1)?;

        // Ein Budget von einem Byte räumt jeden Eintrag sofort wieder ab — store() prunt selbst.
        let outcome = cache.store("egal", &Component::from_binary(&engine(), GUEST)?)?;

        assert_eq!(outcome.evicted, 1);
        assert_eq!(outcome.bytes, 0, "kein Eintrag bleibt übrig");
        assert!(
            directory.join(KEY_FILE).exists(),
            "mac.key darf nie mit abgeräumt werden"
        );
        // Und der Schlüssel zählt auch nicht in die Belegung hinein.
        assert_eq!(cache.prune(), PruneOutcome::default());
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }

    /// Ein Cache-Schlüssel unterscheidet sich auch über die Grants — ein Platten-Treffer darf die
    /// Invalidierung nicht aufweichen.
    #[test]
    fn the_disk_cache_respects_the_grant_part_of_the_key() -> Result<()> {
        let directory = scratch("grantkey");
        let limits = ExecutionLimits::default();
        let mut cache = ModuleCache::with_disk(DiskCache::open(&directory)?);
        cache.compile(GUEST, &CapabilityGrants::default(), &limits)?;

        let mut other = CapabilityGrants::default();
        other.environment.insert("MCPMCP_SPIKE".to_owned());
        let mut fresh = ModuleCache::with_disk(DiskCache::open(&directory)?);
        fresh.compile(GUEST, &other, &limits)?;

        assert_eq!(fresh.stats().disk_hits, 0, "andere Grants, anderer Eintrag");
        assert_eq!(fresh.stats().misses, 1);
        std::fs::remove_dir_all(&directory)?;
        Ok(())
    }
}
