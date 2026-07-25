use std::collections::BTreeSet;
use std::path::{Component as PathComponent, Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail};
use ed25519_dalek::{Signature, Verifier, VerifyingKey};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use wasmtime::component::types::ComponentItem;
use wasmtime::component::{Component, Linker};
use wasmtime::{Config, Engine, Store, StoreLimits, StoreLimitsBuilder};
use wit_component::{ComponentEncoder, StringEncoding, dummy_module, embed_component_metadata};
use wit_parser::{Function, ManglingAndAbi, Resolve, Type, TypeDefKind, WorldItem, WorldKey};

pub mod disk_cache;
pub mod host;

const NO_IMPORT_COMPONENT: &str = include_str!("../fixtures/no-import.component.wat");
const DENIED_IMPORT_COMPONENT: &str = include_str!("../fixtures/denied-import.component.wat");
const INFINITE_COMPONENT: &str = include_str!("../fixtures/infinite.component.wat");
const MEMORY_GROWTH_COMPONENT: &str = include_str!("../fixtures/memory-growth.component.wat");
#[cfg(test)]
const NEEDS_RANDOM_COMPONENT: &str = include_str!("../fixtures/needs-random.component.wat");
#[cfg(test)]
const NEEDS_CLOCK_COMPONENT: &str = include_str!("../fixtures/needs-clock.component.wat");
const CONTROL_PLANE_WIT: &str = include_str!("../../../docs/spikes/fixtures/control-plane.wit");
pub const RUNTIME_VERSION: &str = "wasmtime-47.0.2";
const WASI_GUEST_COMPONENT: &[u8] = include_bytes!("../fixtures/wasi-p2-guest.component.wasm");

#[derive(Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilityInventory {
    pub world: String,
    pub capabilities: Vec<CapabilityDescriptorV1>,
}

#[derive(Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilityDescriptorV1 {
    pub native_name: String,
    pub kind: String,
    pub execution: String,
    pub input_type: String,
    pub result_type: String,
    pub imports: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeProbeReport {
    pub runtime: &'static str,
    pub component_sha256: String,
    pub wit_component_sha256: String,
    pub imports: Vec<String>,
    pub exports: Vec<String>,
    pub wit_component_exports: Vec<String>,
    pub smoke_result: i32,
    pub fuel_limit_enforced: bool,
    pub epoch_timeout_enforced: bool,
    pub memory_limit_enforced: bool,
    pub output_limit_enforced: bool,
    pub compile_milliseconds: f64,
    pub instantiate_and_call_milliseconds: f64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct IsolationComparisonReport {
    pub samples: usize,
    pub wasi_runtime: &'static str,
    pub wasi_policy: &'static str,
    pub wasi_cold_start_milliseconds: TimingSummary,
    pub container_runtime: String,
    pub container_image: String,
    pub container_image_id: String,
    pub container_policy: Vec<&'static str>,
    pub container_job_milliseconds: TimingSummary,
    pub qualification: &'static str,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TimingSummary {
    pub minimum: f64,
    pub median: f64,
    pub p95: f64,
    pub maximum: f64,
}

#[derive(Debug, PartialEq)]
pub struct BoundedCapture {
    pub bytes: Vec<u8>,
    pub total_bytes: usize,
    pub truncated: bool,
}

/// Explizite Host-Capability-Grants, standardmäßig alle leer/aus (default-deny). Ein nicht-leeres
/// Feld bzw. `true` erlaubt, dass die zugehörige WASI-P2-Kategorie überhaupt importiert werden darf;
/// die feingranulare Begrenzung (welche Pfade/Ziele) liegt zusätzlich in den jeweiligen Feldern.
#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilityGrants {
    /// Kanonische Preopen-Wurzeln; leer = kein Dateisystem.
    pub filesystem_preopens: BTreeSet<String>,
    /// Netzwerk-Ziel-Allowlist (`host:port`); leer = kein Netzwerk.
    pub network_allow: BTreeSet<String>,
    /// Freigegebene Environment-Variablennamen; leer = kein Environment.
    pub environment: BTreeSet<String>,
    /// Freigegebene Secret-Capability-Namen. **Noch ohne Wirkung**: Der Host kennt keine
    /// Secret-Quelle und injiziert nichts — das kommt mit dem Trust-Store (Plan 0003, WP4). Das
    /// Feld bleibt im Vertrag, damit die Wire-Form stabil ist; das Gateway weist einen solchen
    /// Grant vorher ab, statt ihn wirkungslos zu senden.
    pub secrets: BTreeSet<String>,
    /// Uhr-Capability.
    pub clock: bool,
    /// Zufallsquelle.
    pub random: bool,
}

/// Grant-Kategorie, auf die ein WASI-P2-Import abgebildet wird.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum GrantCategory {
    Filesystem,
    Network,
    Environment,
    Secret,
    Clock,
    Random,
    /// Unbekannter Import — fail-closed, immer verweigert.
    Unknown,
}

/// Bildet einen WASI-P2-Interface-Importnamen auf seine Grant-Kategorie ab. Unbekannte Namen sind
/// bewusst `Unknown` und werden dadurch immer verweigert (fail-closed).
pub fn classify_import(name: &str) -> GrantCategory {
    if name.contains("wasi:filesystem") {
        GrantCategory::Filesystem
    } else if name.contains("wasi:sockets") {
        GrantCategory::Network
    } else if name.contains("wasi:cli/environment") {
        GrantCategory::Environment
    } else if name.contains("wasi:clocks") {
        GrantCategory::Clock
    } else if name.contains("wasi:random") {
        GrantCategory::Random
    } else if name.contains("secret") {
        GrantCategory::Secret
    } else {
        GrantCategory::Unknown
    }
}

fn import_is_granted(import: &str, grants: &CapabilityGrants) -> bool {
    match classify_import(import) {
        GrantCategory::Filesystem => !grants.filesystem_preopens.is_empty(),
        GrantCategory::Network => !grants.network_allow.is_empty(),
        GrantCategory::Environment => !grants.environment.is_empty(),
        GrantCategory::Secret => !grants.secrets.is_empty(),
        GrantCategory::Clock => grants.clock,
        GrantCategory::Random => grants.random,
        GrantCategory::Unknown => false,
    }
}

/// Ein administrativ gepinnter Publisher. Nur diese Public Keys dürfen Component-Bytes signieren;
/// `key_id` ist der stabile SHA-256-Fingerprint des Public Keys (für Audit und Anzeige).
pub struct PinnedPublisher {
    pub key_id: String,
    pub verifying_key: VerifyingKey,
}

/// Erzeugt einen gepinnten Publisher aus einem Public Key (SHA-256-Fingerprint als stabile Id).
pub fn pinned_publisher(verifying_key: VerifyingKey) -> PinnedPublisher {
    PinnedPublisher {
        key_id: sha256_hex(verifying_key.as_bytes()),
        verifying_key,
    }
}

/// Verifiziert eine **detached** Ed25519-Signatur über die Component-Bytes gegen die administrativ
/// gepinnten Publisher. Gibt bei Erfolg die `key_id` des akzeptierenden Publishers zurück.
/// Manipulierte Bytes, eine ungültige Signatur oder ein nicht gepinnter Schlüssel werden
/// fail-closed abgewiesen.
pub fn verify_component_signature(
    component_bytes: &[u8],
    signature_bytes: &[u8; 64],
    pinned: &[PinnedPublisher],
) -> Result<String> {
    let signature = Signature::from_bytes(signature_bytes);
    for publisher in pinned {
        if publisher
            .verifying_key
            .verify(component_bytes, &signature)
            .is_ok()
        {
            return Ok(publisher.key_id.clone());
        }
    }
    bail!("component signature matches no pinned publisher")
}

/// Auditdatensatz beim Laden/Instanziieren eines Components: identifiziert das Modul über seinen
/// SHA-256, den akzeptierenden Publisher, die Runtime-Version und die tatsächlich erteilten
/// Host-Grants. Deterministisch serialisierbar für das Governance-Audit.
#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GrantAuditRecord {
    pub module_sha256: String,
    pub publisher_key_id: String,
    pub runtime: String,
    pub granted_filesystem_preopens: Vec<String>,
    pub granted_network_allow: Vec<String>,
    pub granted_environment: Vec<String>,
    pub granted_secrets: Vec<String>,
    pub granted_clock: bool,
    pub granted_random: bool,
}

/// Baut den [`GrantAuditRecord`] aus den Component-Bytes, dem verifizierten Publisher und den
/// erteilten Grants. Die Set-Felder werden als sortierte Vecs übernommen (BTreeSet-Ordnung).
pub fn grant_audit_record(
    component_bytes: &[u8],
    publisher_key_id: &str,
    grants: &CapabilityGrants,
) -> GrantAuditRecord {
    GrantAuditRecord {
        module_sha256: sha256_hex(component_bytes),
        publisher_key_id: publisher_key_id.to_owned(),
        runtime: RUNTIME_VERSION.to_owned(),
        granted_filesystem_preopens: grants.filesystem_preopens.iter().cloned().collect(),
        granted_network_allow: grants.network_allow.iter().cloned().collect(),
        granted_environment: grants.environment.iter().cloned().collect(),
        granted_secrets: grants.secrets.iter().cloned().collect(),
        granted_clock: grants.clock,
        granted_random: grants.random,
    }
}

/// Lexikalische Preopen-Eingrenzung ohne Dateisystemzugriff (portabel, deterministisch): entfernt
/// `.`, verarbeitet `..` und weist alles ab, das die Wurzel verlässt (führendes `..`, absolute
/// Pfade, Root-/Prefix-Komponenten). Erste Verteidigungslinie gegen Path-Traversal.
pub fn resolve_within_root(root: &Path, requested: &str) -> Result<PathBuf> {
    let mut stack: Vec<std::ffi::OsString> = Vec::new();
    for component in Path::new(requested).components() {
        match component {
            PathComponent::CurDir => {}
            PathComponent::ParentDir => {
                if stack.pop().is_none() {
                    bail!("path '{requested}' traverses above the preopen root");
                }
            }
            PathComponent::Normal(part) => stack.push(part.to_owned()),
            PathComponent::RootDir | PathComponent::Prefix(_) => {
                bail!("path '{requested}' is absolute and escapes the preopen root");
            }
        }
    }
    let mut resolved = root.to_path_buf();
    resolved.extend(stack);
    Ok(resolved)
}

/// Dateisystem-basierte Eingrenzung: kanonisiert Wurzel und Ziel (löst Symlinks auf) und verlangt,
/// dass das kanonische Ziel unter der kanonischen Wurzel bleibt. Fängt Symlink-Ausbrüche, die eine
/// rein lexikalische Prüfung nicht sieht. Wurzel und Ziel müssen existieren.
pub fn canonical_within_root(root: &Path, target: &Path) -> Result<PathBuf> {
    let canonical_root = root
        .canonicalize()
        .with_context(|| format!("preopen root {} is not accessible", root.display()))?;
    let canonical_target = target
        .canonicalize()
        .with_context(|| format!("path {} is not accessible", target.display()))?;
    if canonical_target.starts_with(&canonical_root) {
        Ok(canonical_target)
    } else {
        bail!(
            "canonical path {} escapes preopen root {}",
            canonical_target.display(),
            canonical_root.display()
        )
    }
}

struct WasiGuestHost {
    ctx: wasmtime_wasi::WasiCtx,
    table: wasmtime::component::ResourceTable,
    limits: StoreLimits,
}

/// `HasData`-Marker für die WASI-I/O-Interfaces, die nur die Resource-Table brauchen.
/// wasmtime-wasi hält sein eigenes Äquivalent privat, deshalb hier eins.
struct HasResourceTable;

impl wasmtime::component::HasData for HasResourceTable {
    type Data<'a> = &'a mut wasmtime::component::ResourceTable;
}

/// Verdrahtet **nur die gewährten** WASI-Interfaces in den Linker (WP3.1). Was nicht gewährt ist,
/// wird gar nicht erst gelinkt: Ein Component, das ein solches Interface importiert, scheitert
/// dadurch schon beim Instanziieren — vor jeder Ausführung, ohne dass ein Aufruf abgefangen
/// werden müsste (deny-before-instantiation).
///
/// Immer verdrahtet ist nur das I/O-Gerüst samt stdio: Diese Streams gehören dem Host — stdout
/// hängt an einem begrenzten Puffer, stdin ist leer, stderr verwirft. Sie sind der Rückkanal des
/// Aufrufs, kein Zugang zur Außenwelt. Alles, was nach draußen führt — Dateisystem, Netzwerk,
/// Environment, Uhr, Zufall — hängt an seinem Grant.
fn add_granted_wasi_to_linker(
    linker: &mut Linker<WasiGuestHost>,
    grants: &CapabilityGrants,
) -> Result<()> {
    use wasmtime_wasi::cli::{WasiCli, WasiCliView as _};
    use wasmtime_wasi::clocks::{WasiClocks, WasiClocksView as _};
    use wasmtime_wasi::filesystem::{WasiFilesystem, WasiFilesystemView as _};
    use wasmtime_wasi::p2::bindings::{cli, clocks, filesystem, random, sockets, sync};
    use wasmtime_wasi::random::{WasiRandom, WasiRandomView as _};
    use wasmtime_wasi::sockets::{WasiSockets, WasiSocketsView as _};

    // Als `fn` statt Closure: Die Lebensdauer der Ausleihe lässt sich sonst nicht ausdrücken.
    fn table(host: &mut WasiGuestHost) -> &mut wasmtime::component::ResourceTable {
        &mut host.table
    }

    sync::io::error::add_to_linker::<_, HasResourceTable>(linker, table)?;
    sync::io::poll::add_to_linker::<_, HasResourceTable>(linker, table)?;
    sync::io::streams::add_to_linker::<_, HasResourceTable>(linker, table)?;
    cli::stdin::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::stdout::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::stderr::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::exit::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::terminal_input::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::terminal_output::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::terminal_stdin::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::terminal_stdout::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    cli::terminal_stderr::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;

    // Auch Secrets werden als Environment-Einträge injiziert (festgelegte Entscheidung 3), also
    // muss die Schnittstelle dafür ebenfalls existieren. Der Preis dieser Entscheidung steht im
    // Doc-Kommentar von `apply_grants_to_context`.
    if !grants.environment.is_empty() || !grants.secrets.is_empty() {
        cli::environment::add_to_linker::<_, WasiCli>(linker, WasiGuestHost::cli)?;
    }

    if grants.clock {
        clocks::wall_clock::add_to_linker::<_, WasiClocks>(linker, WasiGuestHost::clocks)?;
        clocks::monotonic_clock::add_to_linker::<_, WasiClocks>(linker, WasiGuestHost::clocks)?;
    }

    if grants.random {
        random::random::add_to_linker::<_, WasiRandom>(linker, WasiGuestHost::random)?;
        random::insecure::add_to_linker::<_, WasiRandom>(linker, WasiGuestHost::random)?;
        random::insecure_seed::add_to_linker::<_, WasiRandom>(linker, WasiGuestHost::random)?;
    }

    if !grants.filesystem_preopens.is_empty() {
        filesystem::preopens::add_to_linker::<_, WasiFilesystem>(
            linker,
            WasiGuestHost::filesystem,
        )?;
        sync::filesystem::types::add_to_linker::<_, WasiFilesystem>(
            linker,
            WasiGuestHost::filesystem,
        )?;
    }

    if !grants.network_allow.is_empty() {
        sockets::instance_network::add_to_linker::<_, WasiSockets>(linker, WasiGuestHost::sockets)?;
        sockets::network::add_to_linker::<_, WasiSockets>(
            linker,
            &sockets::network::LinkOptions::default(),
            WasiGuestHost::sockets,
        )?;
        sockets::tcp_create_socket::add_to_linker::<_, WasiSockets>(
            linker,
            WasiGuestHost::sockets,
        )?;
        sockets::udp_create_socket::add_to_linker::<_, WasiSockets>(
            linker,
            WasiGuestHost::sockets,
        )?;
        sockets::ip_name_lookup::add_to_linker::<_, WasiSockets>(linker, WasiGuestHost::sockets)?;
        sync::sockets::tcp::add_to_linker::<_, WasiSockets>(linker, WasiGuestHost::sockets)?;
        sync::sockets::udp::add_to_linker::<_, WasiSockets>(linker, WasiGuestHost::sockets)?;
    }

    Ok(())
}

/// Füllt den WASI-Kontext mit genau den gewährten Ressourcen (WP3.1). Das Linken oben entscheidet,
/// **ob** eine Kategorie überhaupt existiert; hier wird festgelegt, **worauf** sie zeigt.
///
/// Secrets kommen als Environment-Einträge herein (WP4, festgelegte Entscheidung 3). Das hat einen
/// Preis, der benannt gehört: Wer Secrets gewährt, gewährt damit `wasi:cli/environment` — das
/// Component kann also **alle** gesetzten Variablen auflisten, nicht nur die, die es erwartet.
/// Eine eigene Secret-Schnittstelle wäre enger, war aber ausdrücklich nicht gewollt.
/// Fail-closed: Ein gewährter Name ohne Wert ist ein Fehler, kein leerer String — sonst liefe ein
/// Component mit einem Secret, das es für gesetzt hält.
fn apply_grants_to_context(
    builder: &mut wasmtime_wasi::WasiCtxBuilder,
    grants: &CapabilityGrants,
    secret_values: &std::collections::BTreeMap<String, String>,
) -> Result<()> {
    for key in &grants.environment {
        builder.env(key, "granted");
    }

    for name in &grants.secrets {
        let value = secret_values.get(name).with_context(|| {
            format!("Secret '{name}' ist gewährt, aber es wurde kein Wert übergeben")
        })?;
        builder.env(name, value);
    }

    for root in &grants.filesystem_preopens {
        // Kanonisch auflösen: Ein Preopen auf einen Symlink würde sonst außerhalb der gewährten
        // Wurzel landen, ohne dass der Grant das hergibt.
        let canonical = std::fs::canonicalize(root)
            .with_context(|| format!("Preopen-Wurzel '{root}' ist nicht auflösbar"))?;
        // Nur lesend: Schreibrechte sind im Grant-Modell noch nicht ausdrückbar und werden
        // deshalb nicht stillschweigend vergeben.
        builder.preopened_dir(
            &canonical,
            root,
            wasmtime_wasi::DirPerms::READ,
            wasmtime_wasi::FilePerms::READ,
        )?;
    }

    if !grants.network_allow.is_empty() {
        let allowed = resolve_network_allowlist(&grants.network_allow)?;
        // Die Prüfung läuft pro Socket-Adresse: Ein nicht gelistetes Ziel wird abgewiesen, auch
        // wenn das Netzwerk-Interface selbst gewährt ist.
        builder.socket_addr_check(move |address, _use| {
            let allowed = allowed.clone();
            Box::pin(async move { allowed.contains(&address) })
        });
        // Namensauflösung ist eine eigene Fähigkeit und in der Allowlist nicht ausdrückbar.
        builder.allow_ip_name_lookup(false);
    }

    Ok(())
}

/// Aufgelöste Netzwerkziele, geteilt mit der Adressprüfung im WASI-Kontext.
type NetworkAllowlist = std::sync::Arc<std::collections::HashSet<std::net::SocketAddr>>;

/// Löst die `host:port`-Allowlist zu konkreten Socket-Adressen auf. Auflösung passiert **einmal**
/// beim Aufbau des Kontexts, nicht pro Verbindung: Sonst entschiede ein DNS-Server zur Laufzeit
/// darüber, wohin ein Grant zeigt.
fn resolve_network_allowlist(allow: &BTreeSet<String>) -> Result<NetworkAllowlist> {
    use std::net::ToSocketAddrs;

    let mut resolved = std::collections::HashSet::new();
    for target in allow {
        let addresses = target
            .to_socket_addrs()
            .with_context(|| format!("Netzwerkziel '{target}' ist nicht auflösbar (host:port)"))?;
        resolved.extend(addresses);
    }

    Ok(std::sync::Arc::new(resolved))
}

impl wasmtime_wasi::WasiView for WasiGuestHost {
    fn ctx(&mut self) -> wasmtime_wasi::WasiCtxView<'_> {
        wasmtime_wasi::WasiCtxView {
            ctx: &mut self.ctx,
            table: &mut self.table,
        }
    }
}

/// Präfix des WASI-CLI-Kommando-Exports. Bewusst ohne Versionssuffix: die wasip2-Toolchain
/// erzeugt je nach WASI-Release `wasi:cli/run@0.2.x`, und der Host soll nicht an einer
/// Patch-Version zerbrechen. Components mit diesem Export sind ausführbare Kommandos; alles
/// andere wird als typisierter Funktions-Export aufgerufen.
pub const WASI_CLI_RUN_PREFIX: &str = "wasi:cli/run";

/// True, wenn der Export-Name den WASI-CLI-Kommando-Einstiegspunkt bezeichnet.
pub fn is_wasi_command_export(export: &str) -> bool {
    export.starts_with(WASI_CLI_RUN_PREFIX)
}

/// Ausführungslimits für einen einzelnen Aufruf (WP1.4). Alle Werte sind pro Invocation gültig;
/// die Defaults sind bewusst eng — ein Aufrufer, der mehr braucht, muss es explizit anfordern.
#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExecutionLimits {
    /// Wasmtime-Fuel (Instruktionsbudget). `None` = kein Fuel-Limit.
    pub fuel: Option<u64>,
    /// Wanduhr-Deadline; wird über Epoch-Interruption durchgesetzt.
    pub timeout_ms: Option<u64>,
    /// Obergrenze des Linear-Memory der Instanz.
    pub max_memory_bytes: Option<usize>,
    /// Obergrenze der eingesammelten stdout-Bytes.
    pub max_output_bytes: usize,
}

impl Default for ExecutionLimits {
    fn default() -> Self {
        Self {
            fuel: Some(50_000_000),
            timeout_ms: Some(5_000),
            max_memory_bytes: Some(64 * 1024 * 1024),
            max_output_bytes: 64 * 1024,
        }
    }
}

/// Ein kompiliertes Component samt der Engine, zu der es gehört (WP5). Beide sind intern
/// referenzgezählt, das Klonen kostet also nichts — der teure Teil ist die Kompilierung.
#[derive(Clone)]
pub struct CachedModule {
    engine: Engine,
    component: Component,
    /// Wie lange die Kompilierung gedauert hat, die diesen Eintrag erzeugt hat.
    pub compile: Duration,
}

/// Kennzahlen des Modul-Caches. Sie gehen über `health` an das Gateway: Ohne sie wäre die Zusage
/// „warme Starts" eine Behauptung, die im Betrieb niemand nachprüfen kann.
#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModuleCacheStats {
    pub entries: usize,
    pub hits: u64,
    pub misses: u64,
    /// Dauer der letzten Kompilierung.
    pub last_compile_ms: f64,
    /// Summe aller Kompilierungen — die Kosten, die der Cache seither einspart.
    pub total_compile_ms: f64,
    /// Treffer aus dem Platten-Cache: Kompilate, die dieser Prozess NICHT selbst erzeugt hat.
    pub disk_hits: u64,
    /// Verworfene oder nicht schreibbare Platten-Einträge. Sichtbar, damit ein stummer
    /// Cache-Ausfall (falsche Rechte, MAC-Fehler) nicht als „kompiliert eben immer neu" endet.
    pub disk_errors: u64,
    /// Aus dem Speicher-Cache verdrängte Kompilate (Obergrenze erreicht).
    pub evictions: u64,
    /// Vom Platten-Cache gelöschte Einträge, weil das Budget erreicht war.
    pub disk_evictions: u64,
    /// Belegung des Platten-Caches nach dem letzten Aufräumen, in Byte.
    pub disk_bytes: u64,
}

/// Content-adressierter Cache kompilierter Components (WP5.1).
///
/// Der Schlüssel ist `Modul-SHA-256 + Runtime-Version + Engine-Profil + Grant-Fingerabdruck`:
///
/// - **Modulhash** — anderer Inhalt, anderes Kompilat. Der Name des Components taugt nicht als
///   Schlüssel, ein Update unter gleichem Namen bekäme sonst das alte Kompilat.
/// - **Runtime-Version** — ein Wasmtime-Wechsel macht Kompilate ungültig.
/// - **Engine-Profil** — Fuel- und Epoch-Instrumentierung sind in den Code einkompiliert; ein
///   Aufruf mit anderen Limit-Kategorien braucht ein anderes Kompilat.
/// - **Grant-Fingerabdruck** — heute beeinflussen Grants nur das Linken, nicht die Kompilierung.
///   Er steht trotzdem im Schlüssel: Ein Eintrag soll nie über eine Grant-Änderung hinweg
///   weiterverwendet werden, falls das Linken einmal ins Kompilat wandert.
#[derive(Debug)]
pub struct ModuleCache {
    entries: std::collections::HashMap<String, CacheEntry>,
    stats: ModuleCacheStats,
    /// Optionaler Platten-Cache. Ohne konfiguriertes Verzeichnis bleibt der Cache prozesslokal —
    /// ein Verzeichnis lässt sich nicht sicher erraten, also wird keines gewählt.
    disk: Option<crate::disk_cache::DiskCache>,
    /// Obergrenze der Kompilate im Speicher. Gezählt wird in Einträgen, nicht in Byte: Wasmtime
    /// gibt den Speicherbedarf eines fertigen `Component` nicht her, und ihn über `serialize()`
    /// zu schätzen würde genau die Arbeit kosten, die der Cache einsparen soll.
    max_modules: usize,
    /// Monoton steigender Zähler für die Verdrängungsreihenfolge (LRU).
    clock: u64,
}

/// Ein Kompilat samt letztem Zugriff. Der Zeitstempel gehört in den Cache, nicht in das
/// öffentliche [`CachedModule`], das der Aufrufer geklont bekommt.
#[derive(Debug)]
struct CacheEntry {
    module: CachedModule,
    last_used: u64,
}

/// Vorgabe für die Zahl der Kompilate im Speicher. Ein Host bedient einen Upstream; mehrere
/// Einträge entstehen nur über verschiedene Grant-Sätze oder Limit-Profile desselben Components.
/// Acht ist reichlich und begrenzt trotzdem, was ein wechselndes Limit-Profil anhäufen kann.
pub const DEFAULT_MAX_MEMORY_MODULES: usize = 8;

impl Default for ModuleCache {
    fn default() -> Self {
        Self {
            entries: std::collections::HashMap::new(),
            stats: ModuleCacheStats::default(),
            disk: None,
            max_modules: DEFAULT_MAX_MEMORY_MODULES,
            clock: 0,
        }
    }
}

impl std::fmt::Debug for CachedModule {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("CachedModule")
            .field("compile", &self.compile)
            .finish_non_exhaustive()
    }
}

impl ModuleCache {
    /// Cache mit Platten-Rückhalt: Kompilate überleben den Prozess (WP5).
    pub fn with_disk(disk: crate::disk_cache::DiskCache) -> Self {
        Self {
            disk: Some(disk),
            ..Self::default()
        }
    }

    /// Setzt die Obergrenze der Kompilate im Speicher (mindestens eines).
    pub fn with_max_modules(mut self, max_modules: usize) -> Self {
        self.max_modules = max_modules.max(1);
        self
    }

    /// Liefert das Kompilat aus dem Cache oder erzeugt es. Ein Fehlschlag hinterlässt keinen
    /// Eintrag — ein kaputtes Component soll nicht als vermeintlich gültiges Kompilat hängen
    /// bleiben.
    pub fn compile(
        &mut self,
        component_bytes: &[u8],
        grants: &CapabilityGrants,
        limits: &ExecutionLimits,
    ) -> Result<CachedModule> {
        let key = Self::key(component_bytes, grants, limits);
        self.clock += 1;
        if let Some(entry) = self.entries.get_mut(&key) {
            entry.last_used = self.clock;
            self.stats.hits += 1;
            return Ok(entry.module.clone());
        }

        let mut config = Config::new();
        config.wasm_component_model(true);
        config.consume_fuel(limits.fuel.is_some());
        config.epoch_interruption(limits.timeout_ms.is_some());
        let engine = Engine::new(&config)?;

        // Platte vor Kompilierung: Genau dafür existiert sie — der Prozessstart soll nicht zahlen,
        // was ein früherer Start schon bezahlt hat.
        if let Some(disk) = self.disk.as_ref()
            && let Some(component) = disk.load(&key, &engine)
        {
            let module = CachedModule {
                engine,
                component,
                compile: Duration::ZERO,
            };
            self.remember(key, module.clone());
            self.stats.disk_hits += 1;
            self.stats.last_compile_ms = 0.0;
            return Ok(module);
        }

        let started = Instant::now();
        let component = Component::from_binary(&engine, component_bytes)?;
        let compile = started.elapsed();

        if let Some(disk) = self.disk.as_ref() {
            match disk.store(&key, &component) {
                Ok(pruned) => {
                    self.stats.disk_evictions += pruned.evicted;
                    self.stats.disk_bytes = pruned.bytes;
                }
                Err(failure) => {
                    // Ein nicht schreibbarer Cache darf den Aufruf nicht scheitern lassen — er
                    // kostet dann nur wieder Kompilierzeit. Sichtbar bleibt er über disk_errors.
                    self.stats.disk_errors += 1;
                    eprintln!("wasi-host: Kompilat nicht ablegbar: {failure:#}");
                }
            }
        }

        let module = CachedModule {
            engine,
            component,
            compile,
        };
        self.remember(key, module.clone());
        self.stats.misses += 1;
        self.stats.last_compile_ms = compile.as_secs_f64() * 1_000.0;
        self.stats.total_compile_ms += self.stats.last_compile_ms;
        Ok(module)
    }

    pub fn stats(&self) -> &ModuleCacheStats {
        &self.stats
    }

    /// Nimmt ein Kompilat auf und verdrängt bei Überschreitung der Obergrenze das am längsten
    /// nicht genutzte. Ein verdrängtes Kompilat ist kein Verlust, nur eine spätere Kompilierung —
    /// und auf Platte liegt es meist noch.
    fn remember(&mut self, key: String, module: CachedModule) {
        self.entries.insert(
            key,
            CacheEntry {
                module,
                last_used: self.clock,
            },
        );

        while self.entries.len() > self.max_modules {
            let Some(oldest) = self
                .entries
                .iter()
                .min_by_key(|(_, entry)| entry.last_used)
                .map(|(key, _)| key.clone())
            else {
                break;
            };
            self.entries.remove(&oldest);
            self.stats.evictions += 1;
        }

        self.stats.entries = self.entries.len();
    }

    fn key(component_bytes: &[u8], grants: &CapabilityGrants, limits: &ExecutionLimits) -> String {
        let grants_fingerprint = sha256_hex(
            serde_json::to_vec(grants)
                .expect("Grants sind immer serialisierbar")
                .as_slice(),
        );
        format!(
            "{}:{RUNTIME_VERSION}:fuel={}:epoch={}:{grants_fingerprint}",
            sha256_hex(component_bytes),
            limits.fuel.is_some(),
            limits.timeout_ms.is_some(),
        )
    }
}

/// Ergebnis eines Aufrufs samt maschinenlesbaren Truncation-Metadaten.
#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InvocationOutcome {
    /// Eingesammelte stdout-Ausgabe (bereits auf `max_output_bytes` begrenzt).
    pub stdout: String,
    /// True, wenn die Ausgabe das Limit erreicht hat und abgeschnitten sein kann.
    pub truncated: bool,
    /// Rückgabewert eines typisierten Exports, falls vorhanden.
    pub result: Option<i32>,
}

/// Listet die aufrufbaren Exports (Tools) eines Components. Reine Reflexion — das Component wird
/// dafür weder instanziiert noch ausgeführt.
pub fn discover_component_tools(component_bytes: &[u8]) -> Result<Vec<String>> {
    let mut config = Config::new();
    config.wasm_component_model(true);
    let engine = Engine::new(&config)?;
    let component = Component::from_binary(&engine, component_bytes)?;
    Ok(component_exports(&engine, &component))
}

/// Art eines Exports, wie ihn [`invoke_component_tool`] behandelt.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum ToolKind {
    /// WASI-Kommando-Einstiegspunkt (`wasi:cli/run`) — wird als Kommando ausgeführt.
    Command,
    /// Typisierter Funktions-Export.
    Function,
}

/// Ein Parameter eines Funktions-Exports. Der Name kommt aus dem Component-Typ, nicht geraten.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolParameter {
    pub name: String,
    /// Component-Model-Typname, z. B. `s32`.
    #[serde(rename = "type")]
    pub type_name: String,
}

/// Beschreibung eines Exports für die Discovery (WP6.1). Trägt genug Typinformation, dass der
/// Aufrufer ein echtes Schema erzeugen kann, statt für jedes Tool dasselbe Platzhalter-Schema zu
/// verwenden.
///
/// `supported` sagt, ob **dieser** Host den Export heute tatsächlich aufrufen kann. Nicht
/// unterstützte Exports werden mitgeliefert statt verschwiegen: Ein Betreiber, dessen Tool nicht
/// im Katalog auftaucht, soll den Grund sehen können und nicht raten müssen.
#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolDescriptor {
    /// Roher Export-Name — genau diesen erwartet [`invoke_component_tool`].
    pub name: String,
    pub kind: ToolKind,
    pub params: Vec<ToolParameter>,
    /// Ergebnistypen in Component-Model-Schreibweise.
    pub results: Vec<String>,
    pub supported: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unsupported_reason: Option<String>,
}

/// Beschreibt die Exports eines Components typisiert (WP6.1). Reine Reflexion, keine Ausführung.
///
/// Gelistet wird genau das, was adressierbar ist: der WASI-Kommando-Einstiegspunkt als **ein**
/// Tool (seine innere `run`-Funktion ist derselbe Einstiegspunkt und erschiene sonst doppelt),
/// Top-Level-Funktionen als typisierte Tools, und Funktionen in anderen Instanzen als nicht
/// unterstützt — ihr punktierter Name lässt sich beim Aufruf heute nicht auflösen.
pub fn describe_component_tools(component_bytes: &[u8]) -> Result<Vec<ToolDescriptor>> {
    let mut cache = ModuleCache::default();
    let module = cache.compile(
        component_bytes,
        &CapabilityGrants::default(),
        &ExecutionLimits::default(),
    )?;
    Ok(describe_cached_module(&module))
}

/// Wie [`describe_component_tools`], aber auf einem bereits kompilierten Modul (WP5): Discovery
/// soll nicht erneut kompilieren, nur weil sie nach dem Laden kommt.
pub fn describe_cached_module(module: &CachedModule) -> Vec<ToolDescriptor> {
    let engine = &module.engine;
    let component = &module.component;

    let mut tools = Vec::new();
    for (name, item) in component.component_type().exports(engine) {
        match item.ty {
            ComponentItem::ComponentFunc(func) => {
                let params: Vec<ToolParameter> = func
                    .params()
                    .map(|(param, ty)| ToolParameter {
                        name: param.to_owned(),
                        type_name: type_name(&ty),
                    })
                    .collect();
                let results: Vec<String> = func.results().map(|ty| type_name(&ty)).collect();
                let unsupported_reason = unsupported_function_reason(&params, &results);
                tools.push(ToolDescriptor {
                    name: name.to_owned(),
                    kind: ToolKind::Function,
                    params,
                    results,
                    supported: unsupported_reason.is_none(),
                    unsupported_reason,
                });
            }
            ComponentItem::ComponentInstance(instance) if is_wasi_command_export(name) => {
                // Der Kommando-Einstiegspunkt ist EIN Tool. Ohne diesen Zweig erschiene zusätzlich
                // seine innere `run`-Funktion — zwei Katalogeinträge für denselben Aufruf.
                let _ = instance;
                tools.push(ToolDescriptor {
                    name: name.to_owned(),
                    kind: ToolKind::Command,
                    params: Vec::new(),
                    results: Vec::new(),
                    supported: true,
                    unsupported_reason: None,
                });
            }
            ComponentItem::ComponentInstance(instance) => {
                for (child, _) in instance.exports(engine) {
                    tools.push(ToolDescriptor {
                        name: format!("{name}.{child}"),
                        kind: ToolKind::Function,
                        params: Vec::new(),
                        results: Vec::new(),
                        supported: false,
                        unsupported_reason: Some(
                            "Exports in Instanzen sind über ihren punktierten Namen noch nicht aufrufbar"
                                .to_owned(),
                        ),
                    });
                }
            }
            _ => {}
        }
    }

    tools.sort_by(|left, right| left.name.cmp(&right.name));
    tools
}

/// Warum ein Funktions-Export heute nicht aufrufbar ist — `None`, wenn er es ist. Die Bedingung
/// spiegelt exakt den typisierten Pfad in [`invoke_component_tool`]: eine `s32`-Eingabe, eine
/// `s32`-Ausgabe. Sie muss mitwandern, wenn dieser Pfad erweitert wird.
fn unsupported_function_reason(params: &[ToolParameter], results: &[String]) -> Option<String> {
    let signature = format!(
        "({}) -> ({})",
        params
            .iter()
            .map(|param| param.type_name.as_str())
            .collect::<Vec<_>>()
            .join(", "),
        results.join(", ")
    );
    if params.len() == 1 && params[0].type_name == "s32" && results == ["s32"] {
        None
    } else {
        Some(format!(
            "nur (s32) -> s32 wird aufgerufen, dieser Export ist {signature}"
        ))
    }
}

/// Component-Model-Typname. Zusammengesetzte Typen bekommen ihren Sortennamen — für die
/// Schema-Erzeugung reicht das, denn aufrufbar sind ohnehin nur die skalaren Fälle.
fn type_name(ty: &wasmtime::component::Type) -> String {
    use wasmtime::component::Type;
    match ty {
        Type::Bool => "bool",
        Type::S8 => "s8",
        Type::U8 => "u8",
        Type::S16 => "s16",
        Type::U16 => "u16",
        Type::S32 => "s32",
        Type::U32 => "u32",
        Type::S64 => "s64",
        Type::U64 => "u64",
        Type::Float32 => "f32",
        Type::Float64 => "f64",
        Type::Char => "char",
        Type::String => "string",
        Type::List(_) => "list",
        Type::Record(_) => "record",
        Type::Tuple(_) => "tuple",
        Type::Variant(_) => "variant",
        Type::Enum(_) => "enum",
        Type::Option(_) => "option",
        Type::Result(_) => "result",
        Type::Flags(_) => "flags",
        _ => "unknown",
    }
    .to_owned()
}

/// Führt die eingebaute Fixture-Guest-Component aus (bequemer Wrapper für Tests).
pub fn run_wasi_guest(grants: &CapabilityGrants) -> Result<String> {
    let tool = wasi_command_export(WASI_GUEST_COMPONENT)?;
    Ok(invoke_component_tool(
        WASI_GUEST_COMPONENT,
        grants,
        &tool,
        &[],
        &ExecutionLimits::default(),
    )?
    .stdout)
}

/// Ermittelt den WASI-Kommando-Export eines Components (versionsunabhängig).
pub fn wasi_command_export(component_bytes: &[u8]) -> Result<String> {
    discover_component_tools(component_bytes)?
        .into_iter()
        .find(|export| is_wasi_command_export(export))
        .ok_or_else(|| anyhow::anyhow!("component exportiert kein wasi:cli/run"))
}

/// Instanziiert ein WASI-P2-Component und ruft einen seiner Exports auf. WASI wird dem Linker NUR
/// hinzugefügt, wenn der Environment-Grant vorliegt; ohne Grant bleibt der WASI-Import unerfüllt
/// und die Instanziierung schlägt VOR jeder Ausführung fehl (deny-before-instantiation).
///
/// `tool` muss ein vorhandener Export sein — ein unbekannter Name wird abgewiesen (fail-closed).
/// `WASI_CLI_RUN` führt das Component als Kommando aus, jeder andere Export wird als typisierte
/// Funktion mit `s32`-Argumenten aufgerufen. Alle Limits aus [`ExecutionLimits`] greifen.
pub fn invoke_component_tool(
    component_bytes: &[u8],
    grants: &CapabilityGrants,
    tool: &str,
    args: &[i32],
    limits: &ExecutionLimits,
) -> Result<InvocationOutcome> {
    // Ohne Cache: kompiliert jedes Mal neu. Der Host nutzt den Cache-Pfad; diese Form bleibt für
    // Aufrufer, die keinen Zustand halten wollen (Tests, Einmal-Läufe).
    let mut cache = ModuleCache::default();
    let module = cache.compile(component_bytes, grants, limits)?;
    invoke_cached_module(&module, grants, &Default::default(), tool, args, limits)
}

/// Ruft ein Tool eines bereits kompilierten Components auf (WP5). Die Grants werden hier erneut
/// angewandt — sie hängen am Aufruf, nicht am Kompilat.
pub fn invoke_cached_module(
    module: &CachedModule,
    grants: &CapabilityGrants,
    secret_values: &std::collections::BTreeMap<String, String>,
    tool: &str,
    args: &[i32],
    limits: &ExecutionLimits,
) -> Result<InvocationOutcome> {
    let engine = &module.engine;
    let component = &module.component;

    let exports = component_exports(engine, component);
    if !exports.iter().any(|export| export == tool) {
        bail!("tool '{tool}' is not exported by this component");
    }

    let mut linker = Linker::<WasiGuestHost>::new(engine);
    add_granted_wasi_to_linker(&mut linker, grants)?;

    let stdout = wasmtime_wasi::p2::pipe::MemoryOutputPipe::new(limits.max_output_bytes);
    let mut builder = wasmtime_wasi::WasiCtxBuilder::new();
    builder.stdout(stdout.clone());
    apply_grants_to_context(&mut builder, grants, secret_values)?;

    let mut store_limits = StoreLimitsBuilder::new();
    if let Some(max_memory) = limits.max_memory_bytes {
        store_limits = store_limits
            .memory_size(max_memory)
            .trap_on_grow_failure(true);
    }
    let host = WasiGuestHost {
        ctx: builder.build(),
        table: wasmtime::component::ResourceTable::new(),
        limits: store_limits.build(),
    };
    let mut store = Store::new(engine, host);
    store.limiter(|state: &mut WasiGuestHost| &mut state.limits);
    if let Some(fuel) = limits.fuel {
        store.set_fuel(fuel)?;
    }

    // Wanduhr-Deadline: ein Wachhund erhöht die Epoche nach dem Timeout, was den Guest trappt.
    let watchdog = limits.timeout_ms.map(|timeout_ms| {
        store.set_epoch_deadline(1);
        let engine = engine.clone();
        let (stop_tx, stop_rx) = std::sync::mpsc::channel::<()>();
        let handle = std::thread::spawn(move || {
            if stop_rx
                .recv_timeout(Duration::from_millis(timeout_ms))
                .is_err()
            {
                engine.increment_epoch();
            }
        });
        (stop_tx, handle)
    });

    let outcome = (|| -> Result<Option<i32>> {
        if is_wasi_command_export(tool) {
            let command = wasmtime_wasi::p2::bindings::sync::Command::instantiate(
                &mut store, component, &linker,
            )?;
            command
                .wasi_cli_run()
                .call_run(&mut store)?
                .map_err(|()| anyhow::anyhow!("guest run returned an error"))?;
            Ok(None)
        } else {
            let instance = linker.instantiate(&mut store, component)?;
            let func = instance
                .get_typed_func::<(i32,), (i32,)>(&mut store, tool)
                .map_err(|error| {
                    anyhow::anyhow!("export '{tool}' is not a (s32) -> s32 function: {error}")
                })?;
            let argument = args.first().copied().unwrap_or_default();
            let (result,) = func.call(&mut store, (argument,))?;
            Ok(Some(result))
        }
    })();

    if let Some((stop_tx, handle)) = watchdog {
        let _ = stop_tx.send(());
        let _ = handle.join();
    }

    let result = outcome?;
    let bytes = stdout.contents();
    Ok(InvocationOutcome {
        truncated: bytes.len() >= limits.max_output_bytes,
        stdout: String::from_utf8_lossy(&bytes).into_owned(),
        result,
    })
}

pub fn discover_wit(path: &Path, world_name: &str) -> Result<CapabilityInventory> {
    let mut resolve = Resolve::default();
    let (package, _) = resolve
        .push_path(path)
        .with_context(|| format!("failed to parse WIT at {}", path.display()))?;
    let world_id = resolve
        .select_world(&[package], Some(world_name))
        .with_context(|| format!("world '{world_name}' not found"))?;
    let world = &resolve.worlds[world_id];
    let imports = world
        .imports
        .keys()
        .map(|key| world_key_name(&resolve, key))
        .collect::<Vec<_>>();
    let mut capabilities = Vec::new();

    for (key, item) in &world.exports {
        match item {
            WorldItem::Interface { id, .. } => {
                let interface = &resolve.interfaces[*id];
                let interface_name = interface
                    .name
                    .as_deref()
                    .map(str::to_owned)
                    .unwrap_or_else(|| world_key_name(&resolve, key));
                for function in interface.functions.values() {
                    capabilities.push(map_function(&resolve, &interface_name, function, &imports)?);
                }
            }
            WorldItem::Function(function) => {
                capabilities.push(map_function(&resolve, "", function, &imports)?);
            }
            WorldItem::Type { .. } => {}
        }
    }

    capabilities.sort_by(|left, right| left.native_name.cmp(&right.native_name));
    Ok(CapabilityInventory {
        world: world.name.clone(),
        capabilities,
    })
}

pub fn run_runtime_probe() -> Result<RuntimeProbeReport> {
    let engine = hardened_engine(true, true)?;
    let wit_component_bytes = encode_control_plane_component()?;
    let wit_component = Component::from_binary(&engine, &wit_component_bytes)?;
    let wit_imports = component_imports(&engine, &wit_component);
    ensure_imports_granted(&wit_imports, &CapabilityGrants::default())?;
    let wit_component_exports = component_exports(&engine, &wit_component);
    let mut wit_store = Store::new(&engine, ());
    wit_store.set_fuel(100_000)?;
    wit_store.set_epoch_deadline(1);
    Linker::new(&engine).instantiate(&mut wit_store, &wit_component)?;

    let compile_started = Instant::now();
    let (component_bytes, component) = compile_component(&engine, NO_IMPORT_COMPONENT)?;
    let compile_milliseconds = compile_started.elapsed().as_secs_f64() * 1000.0;
    let imports = component_imports(&engine, &component);
    ensure_imports_granted(&imports, &CapabilityGrants::default())?;
    let exports = component_exports(&engine, &component);

    let invoke_started = Instant::now();
    let mut store = Store::new(&engine, ());
    store.set_fuel(10_000)?;
    store.set_epoch_deadline(1);
    let instance = Linker::new(&engine).instantiate(&mut store, &component)?;
    let run = instance.get_typed_func::<(i32,), (i32,)>(&mut store, "run")?;
    let (smoke_result,) = run.call(&mut store, (41,))?;
    let instantiate_and_call_milliseconds = invoke_started.elapsed().as_secs_f64() * 1000.0;

    Ok(RuntimeProbeReport {
        runtime: RUNTIME_VERSION,
        component_sha256: sha256_hex(&component_bytes),
        wit_component_sha256: sha256_hex(&wit_component_bytes),
        imports,
        exports,
        wit_component_exports,
        smoke_result,
        fuel_limit_enforced: fuel_limit_is_enforced()?,
        epoch_timeout_enforced: epoch_timeout_is_enforced()?,
        memory_limit_enforced: memory_limit_is_enforced()?,
        output_limit_enforced: output_limit_is_enforced(),
        compile_milliseconds,
        instantiate_and_call_milliseconds,
    })
}

pub fn denied_imports_are_rejected() -> Result<Vec<String>> {
    let engine = hardened_engine(false, false)?;
    let (_, component) = compile_component(&engine, DENIED_IMPORT_COMPONENT)?;
    let imports = component_imports(&engine, &component);
    match ensure_imports_granted(&imports, &CapabilityGrants::default()) {
        Ok(()) => bail!("component with host imports was unexpectedly accepted"),
        Err(_) => Ok(imports),
    }
}

pub fn compare_with_container(image: &str, samples: usize) -> Result<IsolationComparisonReport> {
    if samples < 3 {
        bail!("at least three samples are required");
    }
    let runtime = docker_output(&["version", "--format", "{{.Server.Version}}"])
        .context("Docker daemon is unavailable")?;
    let image_id = docker_output(&["image", "inspect", "--format", "{{.Id}}", image])
        .with_context(|| format!("container image '{image}' is unavailable; pull it explicitly"))?;

    let mut wasi_timings = Vec::with_capacity(samples);
    let mut container_timings = Vec::with_capacity(samples);
    for _ in 0..samples {
        let started = Instant::now();
        invoke_fresh_component()?;
        wasi_timings.push(started.elapsed().as_secs_f64() * 1000.0);

        let started = Instant::now();
        let status = Command::new("docker")
            .args([
                "run",
                "--rm",
                "--network",
                "none",
                "--read-only",
                "--cap-drop",
                "ALL",
                "--security-opt",
                "no-new-privileges",
                "--pids-limit",
                "16",
                "--memory",
                "64m",
                "--cpus",
                "0.5",
                "--user",
                "65532:65532",
                image,
                "/bin/true",
            ])
            .status()
            .context("failed to launch hardened container job")?;
        if !status.success() {
            bail!("hardened container job exited with {status}");
        }
        container_timings.push(started.elapsed().as_secs_f64() * 1000.0);
    }

    Ok(IsolationComparisonReport {
        samples,
        wasi_runtime: "wasmtime-47.0.2",
        wasi_policy: "zero imports, fuel, epoch deadline, 128-KiB memory ceiling",
        wasi_cold_start_milliseconds: timing_summary(wasi_timings),
        container_runtime: runtime,
        container_image: image.to_owned(),
        container_image_id: image_id,
        container_policy: vec![
            "network=none",
            "rootfs=read-only",
            "capabilities=none",
            "no-new-privileges",
            "pids=16",
            "memory=64m",
            "cpus=0.5",
            "uid=65532",
        ],
        container_job_milliseconds: timing_summary(container_timings),
        qualification: "startup-floor only; not a security equivalence or application throughput benchmark",
    })
}

pub fn capture_bounded<I, B>(chunks: I, max_bytes: usize) -> BoundedCapture
where
    I: IntoIterator<Item = B>,
    B: AsRef<[u8]>,
{
    let mut bytes = Vec::with_capacity(max_bytes.min(64 * 1024));
    let mut total_bytes = 0usize;
    for chunk in chunks {
        let chunk = chunk.as_ref();
        total_bytes = total_bytes.saturating_add(chunk.len());
        let remaining = max_bytes.saturating_sub(bytes.len());
        bytes.extend_from_slice(&chunk[..chunk.len().min(remaining)]);
    }
    BoundedCapture {
        truncated: total_bytes > bytes.len(),
        bytes,
        total_bytes,
    }
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn map_function(
    resolve: &Resolve,
    interface_name: &str,
    function: &Function,
    imports: &[String],
) -> Result<CapabilityDescriptorV1> {
    let input_type = match function.params.as_slice() {
        [] => "()".to_owned(),
        [parameter] => type_label(resolve, parameter.ty)?,
        parameters => format!(
            "tuple<{}>",
            parameters
                .iter()
                .map(|parameter| type_label(resolve, parameter.ty))
                .collect::<Result<Vec<_>>>()?
                .join(",")
        ),
    };
    let result_type = function
        .result
        .map(|result| type_label(resolve, result))
        .transpose()?
        .unwrap_or_else(|| "()".to_owned());
    let native_name = if interface_name.is_empty() {
        function.name.clone()
    } else {
        format!("{interface_name}.{}", function.name)
    };
    Ok(CapabilityDescriptorV1 {
        native_name,
        kind: "Tool".to_owned(),
        execution: "Synchronous".to_owned(),
        input_type,
        result_type,
        imports: imports.to_vec(),
    })
}

fn type_label(resolve: &Resolve, ty: Type) -> Result<String> {
    Ok(match ty {
        Type::Bool => "bool".to_owned(),
        Type::U8 => "u8".to_owned(),
        Type::U16 => "u16".to_owned(),
        Type::U32 => "u32".to_owned(),
        Type::U64 => "u64".to_owned(),
        Type::S8 => "s8".to_owned(),
        Type::S16 => "s16".to_owned(),
        Type::S32 => "s32".to_owned(),
        Type::S64 => "s64".to_owned(),
        Type::F32 => "f32".to_owned(),
        Type::F64 => "f64".to_owned(),
        Type::Char => "char".to_owned(),
        Type::String => "string".to_owned(),
        Type::ErrorContext => bail!("error-context is outside spike scope"),
        Type::Id(id) => {
            let definition = &resolve.types[id];
            if let Some(name) = &definition.name {
                name.clone()
            } else {
                match &definition.kind {
                    TypeDefKind::Option(inner) => {
                        format!("option<{}>", type_label(resolve, *inner)?)
                    }
                    TypeDefKind::Result(result) => format!(
                        "result<{},{}>",
                        optional_type_label(resolve, result.ok)?,
                        optional_type_label(resolve, result.err)?
                    ),
                    TypeDefKind::List(inner) => {
                        format!("list<{}>", type_label(resolve, *inner)?)
                    }
                    TypeDefKind::Tuple(tuple) => format!(
                        "tuple<{}>",
                        tuple
                            .types
                            .iter()
                            .map(|item| type_label(resolve, *item))
                            .collect::<Result<Vec<_>>>()?
                            .join(",")
                    ),
                    TypeDefKind::Type(inner) => type_label(resolve, *inner)?,
                    TypeDefKind::Record(_)
                    | TypeDefKind::Variant(_)
                    | TypeDefKind::Enum(_)
                    | TypeDefKind::Flags(_) => {
                        bail!(
                            "anonymous {} types are unsupported",
                            definition.kind.as_str()
                        )
                    }
                    TypeDefKind::Resource
                    | TypeDefKind::Handle(_)
                    | TypeDefKind::Map(_, _)
                    | TypeDefKind::FixedLengthList(_, _)
                    | TypeDefKind::Future(_)
                    | TypeDefKind::Stream(_)
                    | TypeDefKind::Unknown => {
                        bail!("{} is outside spike scope", definition.kind.as_str())
                    }
                }
            }
        }
    })
}

fn optional_type_label(resolve: &Resolve, ty: Option<Type>) -> Result<String> {
    ty.map(|value| type_label(resolve, value))
        .transpose()
        .map(|value| value.unwrap_or_else(|| "_".to_owned()))
}

fn world_key_name(resolve: &Resolve, key: &WorldKey) -> String {
    match key {
        WorldKey::Name(name) => name.clone(),
        WorldKey::Interface(id) => resolve.interfaces[*id]
            .name
            .clone()
            .unwrap_or_else(|| format!("interface-{}", id.index())),
    }
}

fn hardened_engine(consume_fuel: bool, epoch_interruption: bool) -> Result<Engine> {
    let mut config = Config::new();
    config.wasm_component_model(true);
    config.consume_fuel(consume_fuel);
    config.epoch_interruption(epoch_interruption);
    Ok(Engine::new(&config)?)
}

fn encode_control_plane_component() -> Result<Vec<u8>> {
    let mut resolve = Resolve::default();
    let package = resolve.push_str("control-plane.wit", CONTROL_PLANE_WIT)?;
    let world = resolve.select_world(&[package], Some("connector"))?;
    let mut module = dummy_module(&resolve, world, ManglingAndAbi::Standard32);
    embed_component_metadata(&mut module, &resolve, world, StringEncoding::UTF8)?;
    ComponentEncoder::default()
        .module(&module)?
        .validate(true)
        .encode()
}

fn invoke_fresh_component() -> Result<()> {
    let engine = hardened_engine(true, true)?;
    let (_, component) = compile_component(&engine, NO_IMPORT_COMPONENT)?;
    let mut store = Store::new(&engine, ());
    store.set_fuel(10_000)?;
    store.set_epoch_deadline(1);
    let instance = Linker::new(&engine).instantiate(&mut store, &component)?;
    let run = instance.get_typed_func::<(i32,), (i32,)>(&mut store, "run")?;
    let (result,) = run.call(&mut store, (41,))?;
    if result != 42 {
        bail!("unexpected component result {result}");
    }
    Ok(())
}

fn compile_component(engine: &Engine, source: &str) -> Result<(Vec<u8>, Component)> {
    let bytes = wat::parse_str(source)?;
    let component = Component::from_binary(engine, &bytes)?;
    Ok((bytes, component))
}

fn docker_output(arguments: &[&str]) -> Result<String> {
    let output = Command::new("docker")
        .args(arguments)
        .output()
        .context("failed to execute docker")?;
    if !output.status.success() {
        bail!("{}", String::from_utf8_lossy(&output.stderr).trim());
    }
    Ok(String::from_utf8(output.stdout)?.trim().to_owned())
}

fn timing_summary(mut values: Vec<f64>) -> TimingSummary {
    values.sort_by(f64::total_cmp);
    let p95_index = ((values.len() as f64 * 0.95).ceil() as usize)
        .saturating_sub(1)
        .min(values.len() - 1);
    TimingSummary {
        minimum: values[0],
        median: values[values.len() / 2],
        p95: values[p95_index],
        maximum: values[values.len() - 1],
    }
}

fn component_imports(engine: &Engine, component: &Component) -> Vec<String> {
    component
        .component_type()
        .imports(engine)
        .map(|(name, _)| name.to_owned())
        .collect()
}

fn component_exports(engine: &Engine, component: &Component) -> Vec<String> {
    let mut exports = Vec::new();
    for (name, item) in component.component_type().exports(engine) {
        exports.push(name.to_owned());
        if let ComponentItem::ComponentInstance(instance) = item.ty {
            exports.extend(
                instance
                    .exports(engine)
                    .map(|(child, _)| format!("{name}.{child}")),
            );
        }
    }
    exports.sort();
    exports
}

fn ensure_imports_granted(imports: &[String], grants: &CapabilityGrants) -> Result<()> {
    let denied = imports
        .iter()
        .filter(|import| !import_is_granted(import, grants))
        .cloned()
        .collect::<Vec<_>>();
    if denied.is_empty() {
        Ok(())
    } else {
        bail!("component imports are not granted: {}", denied.join(", "))
    }
}

fn fuel_limit_is_enforced() -> Result<bool> {
    let engine = hardened_engine(true, false)?;
    let (_, component) = compile_component(&engine, INFINITE_COMPONENT)?;
    let mut store = Store::new(&engine, ());
    store.set_fuel(1_000)?;
    let instance = Linker::new(&engine).instantiate(&mut store, &component)?;
    let spin = instance.get_typed_func::<(), ()>(&mut store, "spin")?;
    Ok(spin.call(&mut store, ()).is_err())
}

fn epoch_timeout_is_enforced() -> Result<bool> {
    let engine = hardened_engine(false, true)?;
    let (_, component) = compile_component(&engine, INFINITE_COMPONENT)?;
    let mut store = Store::new(&engine, ());
    store.set_epoch_deadline(1);
    let instance = Linker::new(&engine).instantiate(&mut store, &component)?;
    let spin = instance.get_typed_func::<(), ()>(&mut store, "spin")?;
    let interrupt_engine = engine.clone();
    let interrupter = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(20));
        interrupt_engine.increment_epoch();
    });
    let started = Instant::now();
    let trapped = spin.call(&mut store, ()).is_err();
    interrupter
        .join()
        .map_err(|_| anyhow::anyhow!("epoch interrupter panicked"))?;
    Ok(trapped && started.elapsed() < Duration::from_secs(2))
}

fn memory_limit_is_enforced() -> Result<bool> {
    let engine = hardened_engine(false, false)?;
    let (_, component) = compile_component(&engine, MEMORY_GROWTH_COMPONENT)?;
    let limits = StoreLimitsBuilder::new()
        .memory_size(128 * 1024)
        .memories(1)
        .instances(4)
        .trap_on_grow_failure(true)
        .build();
    let mut store = Store::new(&engine, limits);
    store.limiter(|state: &mut StoreLimits| state);
    let instance = Linker::new(&engine).instantiate(&mut store, &component)?;
    let grow = instance.get_typed_func::<(u32,), (i32,)>(&mut store, "grow")?;
    Ok(grow.call(&mut store, (2,)).is_err())
}

fn output_limit_is_enforced() -> bool {
    let chunks = std::iter::repeat_n(vec![b'x'; 32 * 1024], 32);
    let capture = capture_bounded(chunks, 64 * 1024);
    capture.bytes.len() == 64 * 1024 && capture.total_bytes == 1024 * 1024 && capture.truncated
}

#[cfg(test)]
mod tests {
    use ed25519_dalek::{Signer, SigningKey};

    use super::*;

    fn repository_fixture(name: &str) -> std::path::PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../docs/spikes/fixtures")
            .join(name)
    }

    #[test]
    fn wit_inventory_matches_expected_contract() -> Result<()> {
        let actual = discover_wit(&repository_fixture("control-plane.wit"), "connector")?;
        let expected: CapabilityInventory = serde_json::from_slice(&std::fs::read(
            repository_fixture("control-plane.expected.json"),
        )?)?;
        assert_eq!(actual, expected);
        Ok(())
    }

    #[test]
    fn runtime_executes_without_host_capabilities_and_enforces_limits() -> Result<()> {
        let report = run_runtime_probe()?;
        assert!(report.imports.is_empty());
        assert_eq!(report.exports, ["run"]);
        assert!(
            report
                .wit_component_exports
                .iter()
                .any(|name| name.ends_with(".run"))
        );
        assert_eq!(report.smoke_result, 42);
        assert!(report.fuel_limit_enforced);
        assert!(report.epoch_timeout_enforced);
        assert!(report.memory_limit_enforced);
        assert!(report.output_limit_enforced);
        Ok(())
    }

    #[test]
    fn filesystem_and_network_imports_are_denied_before_instantiation() -> Result<()> {
        let imports = denied_imports_are_rejected()?;
        assert!(imports.iter().any(|name| name.contains("filesystem")));
        assert!(imports.iter().any(|name| name.contains("sockets")));
        Ok(())
    }

    #[test]
    fn bounded_capture_counts_bytes_and_drains_all_chunks() {
        let capture = capture_bounded([&b"\xc3\xa4"[..], &b"1234"[..]], 3);
        assert_eq!(capture.bytes, b"\xc3\xa4\x31");
        assert_eq!(capture.total_bytes, 6);
        assert!(capture.truncated);
    }

    #[test]
    fn timing_summary_uses_nearest_rank_p95() {
        let summary = timing_summary(vec![9.0, 1.0, 4.0, 3.0, 2.0]);
        assert_eq!(summary.minimum, 1.0);
        assert_eq!(summary.median, 3.0);
        assert_eq!(summary.p95, 9.0);
        assert_eq!(summary.maximum, 9.0);
    }

    #[test]
    fn grants_default_deny_every_category() {
        let grants = CapabilityGrants::default();
        for import in [
            "wasi:filesystem/types@0.2.0",
            "wasi:sockets/tcp@0.2.0",
            "wasi:cli/environment@0.2.0",
            "wasi:clocks/wall-clock@0.2.0",
            "wasi:random/random@0.2.0",
            "custom:secret/store@1.0.0",
            "totally:unknown/interface@0.1.0",
        ] {
            assert!(
                ensure_imports_granted(&[import.to_owned()], &grants).is_err(),
                "default grants must deny {import}"
            );
        }
    }

    #[test]
    fn explicit_grant_allows_only_its_own_category() {
        let mut grants = CapabilityGrants::default();
        grants.filesystem_preopens.insert("/srv/data".to_owned());

        assert!(
            ensure_imports_granted(&["wasi:filesystem/types@0.2.0".to_owned()], &grants).is_ok()
        );
        assert!(ensure_imports_granted(&["wasi:sockets/tcp@0.2.0".to_owned()], &grants).is_err());
    }

    #[test]
    fn unknown_imports_fail_closed_even_with_every_grant() {
        let grants = CapabilityGrants {
            filesystem_preopens: BTreeSet::from(["/srv".to_owned()]),
            network_allow: BTreeSet::from(["example:443".to_owned()]),
            environment: BTreeSet::from(["TOKEN".to_owned()]),
            secrets: BTreeSet::from(["db".to_owned()]),
            clock: true,
            random: true,
        };
        assert_eq!(
            classify_import("mystery:iface/foo@0.1.0"),
            GrantCategory::Unknown
        );
        assert!(ensure_imports_granted(&["mystery:iface/foo@0.1.0".to_owned()], &grants).is_err());
    }

    #[test]
    fn classifies_wasi_p2_interfaces() {
        assert_eq!(
            classify_import("wasi:filesystem/types@0.2.0"),
            GrantCategory::Filesystem
        );
        assert_eq!(
            classify_import("wasi:sockets/network@0.2.0"),
            GrantCategory::Network
        );
        assert_eq!(
            classify_import("wasi:cli/environment@0.2.0"),
            GrantCategory::Environment
        );
        assert_eq!(
            classify_import("wasi:clocks/monotonic-clock@0.2.0"),
            GrantCategory::Clock
        );
        assert_eq!(
            classify_import("wasi:random/random@0.2.0"),
            GrantCategory::Random
        );
    }

    #[test]
    fn valid_signature_from_pinned_publisher_is_accepted() {
        let signing = SigningKey::from_bytes(&[7u8; 32]);
        let pinned = vec![pinned_publisher(signing.verifying_key())];
        let bytes = b"component-bytes";
        let signature = signing.sign(bytes);

        let key_id = verify_component_signature(bytes, &signature.to_bytes(), &pinned).unwrap();
        assert_eq!(key_id, sha256_hex(signing.verifying_key().as_bytes()));
    }

    #[test]
    fn tampered_bytes_break_verification() {
        let signing = SigningKey::from_bytes(&[7u8; 32]);
        let pinned = vec![pinned_publisher(signing.verifying_key())];
        let signature = signing.sign(b"original-bytes");

        assert!(
            verify_component_signature(b"tampered-bytes", &signature.to_bytes(), &pinned).is_err()
        );
    }

    #[test]
    fn signature_from_unpinned_key_is_rejected() {
        let trusted = SigningKey::from_bytes(&[1u8; 32]);
        let rogue = SigningKey::from_bytes(&[2u8; 32]);
        let pinned = vec![pinned_publisher(trusted.verifying_key())];
        let bytes = b"component-bytes";
        let signature = rogue.sign(bytes);

        assert!(verify_component_signature(bytes, &signature.to_bytes(), &pinned).is_err());
    }

    #[test]
    fn real_component_bytes_can_be_signed_and_verified() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (bytes, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;
        let signing = SigningKey::from_bytes(&[42u8; 32]);
        let pinned = vec![pinned_publisher(signing.verifying_key())];
        let signature = signing.sign(&bytes);

        assert!(verify_component_signature(&bytes, &signature.to_bytes(), &pinned).is_ok());
        Ok(())
    }

    #[test]
    fn grant_audit_record_captures_hash_publisher_runtime_and_grants() {
        let grants = CapabilityGrants {
            filesystem_preopens: BTreeSet::from(["/srv/data".to_owned()]),
            network_allow: BTreeSet::from(["example:443".to_owned()]),
            environment: BTreeSet::from(["TOKEN".to_owned()]),
            secrets: BTreeSet::from(["db".to_owned()]),
            clock: true,
            random: false,
        };

        let record = grant_audit_record(b"component-bytes", "publisher-id", &grants);

        assert_eq!(record.module_sha256, sha256_hex(b"component-bytes"));
        assert_eq!(record.publisher_key_id, "publisher-id");
        assert_eq!(record.runtime, "wasmtime-47.0.2");
        assert_eq!(record.granted_filesystem_preopens, ["/srv/data"]);
        assert_eq!(record.granted_network_allow, ["example:443"]);
        assert_eq!(record.granted_secrets, ["db"]);
        assert!(record.granted_clock);
        assert!(!record.granted_random);

        let json = serde_json::to_string(&record).unwrap();
        assert!(json.contains("\"moduleSha256\""));
        assert!(json.contains("\"grantedFilesystemPreopens\""));
    }

    #[test]
    fn lexical_root_containment_blocks_traversal_and_absolute_paths() {
        let root = Path::new("/srv/data");
        assert_eq!(
            resolve_within_root(root, "a/b").unwrap(),
            Path::new("/srv/data/a/b")
        );
        assert_eq!(
            resolve_within_root(root, "a/./c").unwrap(),
            Path::new("/srv/data/a/c")
        );
        assert_eq!(
            resolve_within_root(root, "a/../b").unwrap(),
            Path::new("/srv/data/b")
        );
        assert!(resolve_within_root(root, "../etc/passwd").is_err());
        assert!(resolve_within_root(root, "a/../../x").is_err());
        assert!(resolve_within_root(root, "/etc/passwd").is_err());
    }

    #[test]
    fn ungranted_sockets_are_denied() {
        let denied = CapabilityGrants::default();
        assert!(ensure_imports_granted(&["wasi:sockets/tcp@0.2.0".to_owned()], &denied).is_err());

        let mut granted = CapabilityGrants::default();
        granted.network_allow.insert("example:443".to_owned());
        assert!(ensure_imports_granted(&["wasi:sockets/tcp@0.2.0".to_owned()], &granted).is_ok());
    }

    #[test]
    fn ungranted_secrets_are_denied() {
        assert_eq!(
            classify_import("custom:secret/store@1.0.0"),
            GrantCategory::Secret
        );
        let denied = CapabilityGrants::default();
        assert!(
            ensure_imports_granted(&["custom:secret/store@1.0.0".to_owned()], &denied).is_err()
        );

        let mut granted = CapabilityGrants::default();
        granted.secrets.insert("db".to_owned());
        assert!(
            ensure_imports_granted(&["custom:secret/store@1.0.0".to_owned()], &granted).is_ok()
        );
    }

    #[test]
    fn canonical_containment_rejects_symlink_escape() -> Result<()> {
        let base = std::env::temp_dir().join(format!("mcpmcp-spike-{}", std::process::id()));
        let inside = base.join("inside");
        let outside = base.join("outside");
        std::fs::create_dir_all(&inside)?;
        std::fs::create_dir_all(&outside)?;
        std::fs::write(outside.join("secret.txt"), b"top-secret")?;

        let link = inside.join("escape");
        if create_dir_symlink(&outside, &link).is_err() {
            // Symlink-Erstellung nicht erlaubt (z. B. Windows ohne Rechte) -> Test überspringen.
            let _ = std::fs::remove_dir_all(&base);
            return Ok(());
        }

        // Zugriff „innerhalb" des Preopens, real aber hinter dem Symlink -> muss abgewiesen werden.
        let escaped = canonical_within_root(&inside, &link.join("secret.txt"));
        // Eine echt innerhalb liegende Datei bleibt erlaubt.
        std::fs::write(inside.join("ok.txt"), b"fine")?;
        let allowed = canonical_within_root(&inside, &inside.join("ok.txt"));
        let _ = std::fs::remove_dir_all(&base);

        assert!(escaped.is_err(), "symlink escape must be rejected");
        assert!(allowed.is_ok(), "a real in-root file must be allowed");
        Ok(())
    }

    #[test]
    fn wasi_guest_runs_only_when_capabilities_are_granted() -> Result<()> {
        // Ohne Grant wird WASI nicht gelinkt -> Instanziierung schlägt VOR der Ausführung fehl.
        assert!(run_wasi_guest(&CapabilityGrants::default()).is_err());

        // Mit Environment-Grant ist WASI gelinkt -> die echte Guest-Component läuft und schreibt
        // ihre Kennung nach stdout.
        let mut grants = CapabilityGrants::default();
        grants.environment.insert("MCPMCP_SPIKE".to_owned());
        let output = run_wasi_guest(&grants)?;
        assert!(
            output.contains("mcpmcp-guest-ok"),
            "unexpected guest output: {output}"
        );
        Ok(())
    }

    #[test]
    fn discover_lists_exports_without_executing() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (bytes, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;

        assert_eq!(discover_component_tools(&bytes)?, ["run"]);
        let guest_tools = discover_component_tools(WASI_GUEST_COMPONENT)?;
        assert!(
            guest_tools.iter().any(|tool| is_wasi_command_export(tool)),
            "kein WASI-Kommando-Export gefunden in {guest_tools:?}"
        );
        Ok(())
    }

    /// WP6.1: Discovery liefert Typen, nicht nur Namen — daraus entsteht im Gateway ein echtes
    /// Schema statt eines Platzhalters für jedes Tool.
    #[test]
    fn describe_reports_parameter_names_and_types() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (bytes, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;

        let tools = describe_component_tools(&bytes)?;

        assert_eq!(tools.len(), 1);
        assert_eq!(tools[0].name, "run");
        assert_eq!(tools[0].kind, ToolKind::Function);
        // Der Parametername kommt aus dem Component-Typ ("value"), nicht aus einer Konvention.
        assert_eq!(
            tools[0].params,
            [ToolParameter {
                name: "value".to_owned(),
                type_name: "s32".to_owned(),
            }]
        );
        assert_eq!(tools[0].results, ["s32"]);
        assert!(tools[0].supported);
        Ok(())
    }

    /// Der Kommando-Einstiegspunkt ist genau EIN Tool. Vorher stand seine innere `run`-Funktion
    /// zusätzlich in der Liste — zwei Katalogeinträge, die denselben Aufruf auslösen.
    #[test]
    fn describe_collapses_the_command_entry_point_to_one_tool() -> Result<()> {
        let tools = describe_component_tools(WASI_GUEST_COMPONENT)?;

        let commands: Vec<_> = tools
            .iter()
            .filter(|tool| is_wasi_command_export(&tool.name))
            .collect();
        assert_eq!(
            commands.len(),
            1,
            "erwartet genau ein Kommando-Tool: {tools:?}"
        );
        assert_eq!(commands[0].kind, ToolKind::Command);
        assert!(commands[0].supported);
        assert!(
            !tools.iter().any(|tool| tool.name.ends_with(".run")),
            "die innere run-Funktion darf nicht zusätzlich erscheinen: {tools:?}"
        );
        Ok(())
    }

    /// Eine Signatur, die der typisierte Aufrufpfad nicht bedienen kann, wird als solche gemeldet
    /// — sonst stünde im Katalog ein Tool, das bei jedem Aufruf scheitert.
    #[test]
    fn describe_marks_an_uncallable_signature_as_unsupported() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (bytes, _) = compile_component(&engine, MEMORY_GROWTH_COMPONENT)?;

        let tools = describe_component_tools(&bytes)?;

        let grow = tools.iter().find(|tool| tool.name == "grow").unwrap();
        assert_eq!(grow.params[0].type_name, "u32");
        assert!(!grow.supported);
        assert!(grow.unsupported_reason.as_ref().unwrap().contains("u32"));
        // Und der Befund stimmt: der Aufruf scheitert tatsächlich.
        assert!(
            invoke_component_tool(
                &bytes,
                &CapabilityGrants::default(),
                "grow",
                &[1],
                &ExecutionLimits::default()
            )
            .is_err()
        );
        Ok(())
    }

    /// WP5.1: Derselbe Inhalt wird genau einmal kompiliert — und der zweite Zugriff ist messbar
    /// billiger. Ohne diese Prüfung wäre „warme Starts" nur eine Zusage im Plan.
    #[test]
    fn the_cache_compiles_identical_content_once() -> Result<()> {
        let mut cache = ModuleCache::default();
        let grants = CapabilityGrants::default();
        let limits = ExecutionLimits::default();

        let first = cache.compile(WASI_GUEST_COMPONENT, &grants, &limits)?;
        let second = cache.compile(WASI_GUEST_COMPONENT, &grants, &limits)?;

        assert_eq!(cache.stats().entries, 1);
        assert_eq!(cache.stats().misses, 1);
        assert_eq!(cache.stats().hits, 1);
        assert!(
            cache.stats().last_compile_ms > 0.0,
            "die Kompilierdauer wird gemessen — sie ist die Zahl, die der Cache einspart"
        );
        // Der Treffer liefert dasselbe Kompilat, nicht ein zweites mit gleichem Inhalt.
        assert_eq!(first.compile, second.compile);
        Ok(())
    }

    /// WP5.1: Der Schlüssel trägt Inhalt, Grants und Engine-Profil. Ändert sich eines davon, darf
    /// der alte Eintrag nicht weiterverwendet werden.
    #[test]
    fn the_cache_key_invalidates_on_content_grants_and_engine_profile() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (other_bytes, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;
        let mut cache = ModuleCache::default();
        let limits = ExecutionLimits::default();

        cache.compile(WASI_GUEST_COMPONENT, &CapabilityGrants::default(), &limits)?;

        // Anderer Inhalt.
        cache.compile(&other_bytes, &CapabilityGrants::default(), &limits)?;
        assert_eq!(cache.stats().entries, 2);

        // Andere Grants.
        let mut grants = CapabilityGrants::default();
        grants.environment.insert("MCPMCP_SPIKE".to_owned());
        cache.compile(WASI_GUEST_COMPONENT, &grants, &limits)?;
        assert_eq!(cache.stats().entries, 3);

        // Anderes Engine-Profil: ohne Fuel wird anderer Code erzeugt.
        let without_fuel = ExecutionLimits {
            fuel: None,
            ..ExecutionLimits::default()
        };
        cache.compile(WASI_GUEST_COMPONENT, &grants, &without_fuel)?;
        assert_eq!(cache.stats().entries, 4);
        assert_eq!(
            cache.stats().hits,
            0,
            "keiner dieser Fälle darf ein Treffer sein"
        );
        Ok(())
    }

    /// Obergrenze im Speicher: Der Cache wächst nicht unbegrenzt, und verdrängt wird das am
    /// längsten nicht genutzte Kompilat — nicht das zuletzt eingefügte.
    #[test]
    fn the_memory_cache_evicts_the_least_recently_used_module() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (first, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;
        let mut cache = ModuleCache::default().with_max_modules(2);
        let limits = ExecutionLimits::default();

        // Drei verschiedene Grant-Sätze = drei Schlüssel für denselben Inhalt.
        let grants: Vec<CapabilityGrants> = ["a", "b", "c"]
            .iter()
            .map(|name| {
                let mut grants = CapabilityGrants::default();
                grants.environment.insert((*name).to_owned());
                grants
            })
            .collect();

        cache.compile(&first, &grants[0], &limits)?;
        cache.compile(&first, &grants[1], &limits)?;
        // Auf a zugreifen macht b zum ältesten Eintrag.
        cache.compile(&first, &grants[0], &limits)?;
        cache.compile(&first, &grants[2], &limits)?;

        assert_eq!(cache.stats().entries, 2, "die Obergrenze hält");
        assert_eq!(cache.stats().evictions, 1);
        // a war zuletzt benutzt und muss noch da sein: erneuter Zugriff ist ein Treffer.
        let hits_before = cache.stats().hits;
        cache.compile(&first, &grants[0], &limits)?;
        assert_eq!(
            cache.stats().hits,
            hits_before + 1,
            "a wurde nicht verdrängt"
        );
        // b war am längsten unbenutzt und ist weg: erneuter Zugriff kompiliert.
        let misses_before = cache.stats().misses;
        cache.compile(&first, &grants[1], &limits)?;
        assert_eq!(cache.stats().misses, misses_before + 1, "b wurde verdrängt");
        Ok(())
    }

    /// Ein kaputtes Component hinterlässt keinen Eintrag — sonst hinge ein „Kompilat" im Cache,
    /// das nie eines war.
    #[test]
    fn a_failed_compilation_leaves_no_entry() {
        let mut cache = ModuleCache::default();

        let failure = cache.compile(
            b"kein wasm",
            &CapabilityGrants::default(),
            &ExecutionLimits::default(),
        );

        assert!(failure.is_err());
        assert_eq!(cache.stats().entries, 0);
        assert_eq!(cache.stats().misses, 0);
    }

    /// Entscheidungsgrundlage für den Platten-Cache (Plan 0003, festgelegte Entscheidung 4):
    /// Was kostet die Kompilierung bei realistischer Modulgröße? Diese Kosten fallen einmal pro
    /// Host-Start an — der prozesslokale Cache deckt alles danach ab.
    ///
    /// Bewusst `#[ignore]`: eine Messung, kein Gate. Reproduzieren mit
    /// `cargo test --release -- --ignored --nocapture compilation_cost`.
    #[test]
    #[ignore = "Messung, kein Gate"]
    fn compilation_cost_by_module_size() -> Result<()> {
        let engine = hardened_engine(true, true)?;
        println!("Modulgröße -> Kompilierdauer");
        for functions in [0usize, 5_000, 20_000, 60_000] {
            let (bytes, _) = compile_component(&engine, &synthetic_component(functions))?;
            let mut cache = ModuleCache::default();
            cache.compile(
                &bytes,
                &CapabilityGrants::default(),
                &ExecutionLimits::default(),
            )?;
            println!(
                "{:>8.1} KiB -> {:>8.1} ms",
                bytes.len() as f64 / 1024.0,
                cache.stats().last_compile_ms
            );
        }
        Ok(())
    }

    /// Erzeugt ein Component mit vielen Kernfunktionen — grob so, wie ein größeres Plugin aussieht.
    fn synthetic_component(functions: usize) -> String {
        let mut wat = String::from("(component\n  (core module $m\n");
        wat.push_str("    (func (export \"run\") (param i32) (result i32) local.get 0)\n");
        for index in 0..functions {
            wat.push_str(&format!(
                "    (func $f{index} (param i32) (result i32) local.get 0 i32.const {index} i32.add i32.const 3 i32.mul)\n"
            ));
        }
        wat.push_str("  )\n  (core instance $i (instantiate $m))\n");
        wat.push_str("  (func $run (param \"value\" s32) (result s32) (canon lift (core func $i \"run\")))\n");
        wat.push_str("  (export \"run\" (func $run)))\n");
        wat
    }

    /// WP3.2: Jede Kategorie einzeln — ein Component, das genau ein Interface importiert, läuft
    /// nur mit dem passenden Grant. Ohne ihn ist das Interface gar nicht gelinkt und die
    /// Instanziierung scheitert **vor** jeder Ausführung.
    #[test]
    fn each_category_is_gated_by_its_own_grant() -> Result<()> {
        /// Setzt genau den Grant, den das jeweilige Fixture braucht.
        type SetGrant = fn(&mut CapabilityGrants);

        let engine = hardened_engine(false, false)?;
        let cases: [(&str, SetGrant); 2] = [
            (NEEDS_RANDOM_COMPONENT, |grants| grants.random = true),
            (NEEDS_CLOCK_COMPONENT, |grants| grants.clock = true),
        ];

        for (source, grant) in cases {
            let (bytes, _) = compile_component(&engine, source)?;

            let denied = invoke_component_tool(
                &bytes,
                &CapabilityGrants::default(),
                "run",
                &[7],
                &ExecutionLimits::default(),
            );
            assert!(
                denied.is_err(),
                "ohne Grant darf nichts instanziiert werden"
            );

            // Ein anderer Grant hilft nicht — die Kategorien sind getrennt.
            let mut wrong = CapabilityGrants::default();
            wrong.environment.insert("MCPMCP_SPIKE".to_owned());
            assert!(
                invoke_component_tool(&bytes, &wrong, "run", &[7], &ExecutionLimits::default())
                    .is_err(),
                "ein fremder Grant darf keine andere Kategorie freischalten"
            );

            let mut granted = CapabilityGrants::default();
            grant(&mut granted);
            let outcome =
                invoke_component_tool(&bytes, &granted, "run", &[7], &ExecutionLimits::default())?;
            assert_eq!(outcome.result, Some(7));
        }
        Ok(())
    }

    /// Welche Interfaces der Linker anbietet, ist die eigentliche Durchsetzung. `Linker::instance`
    /// schlägt bei einem bereits definierten Namen fehl — damit lässt sich die Politik für
    /// Kategorien prüfen, deren Schnittstellen zu groß sind, um sie als WAT-Fixture zu bauen.
    #[test]
    fn only_granted_interfaces_are_linked() -> Result<()> {
        fn is_linked(grants: &CapabilityGrants, interface: &str) -> Result<bool> {
            let engine = hardened_engine(false, false)?;
            let mut linker = Linker::<WasiGuestHost>::new(&engine);
            add_granted_wasi_to_linker(&mut linker, grants)?;
            Ok(linker.instance(interface).is_err())
        }

        // Die Namen tragen die WASI-Version der gepinnten wasmtime-wasi-Bindings (0.2.12);
        // Gast-Imports älterer Patch-Stände bedient wasmtime semver-kompatibel. Ändert sich die
        // Runtime-Version, schlägt dieser Test an — genau so soll es sein.
        let nothing = CapabilityGrants::default();
        // Basis: der Rückkanal des Aufrufs ist immer da.
        assert!(is_linked(&nothing, "wasi:cli/stdout@0.2.12")?);
        assert!(is_linked(&nothing, "wasi:io/streams@0.2.12")?);
        // Alles, was nach draußen führt, ist ohne Grant nicht vorhanden.
        for interface in [
            "wasi:cli/environment@0.2.12",
            "wasi:filesystem/types@0.2.12",
            "wasi:filesystem/preopens@0.2.12",
            "wasi:sockets/tcp@0.2.12",
            "wasi:sockets/ip-name-lookup@0.2.12",
            "wasi:clocks/wall-clock@0.2.12",
            "wasi:random/random@0.2.12",
        ] {
            assert!(
                !is_linked(&nothing, interface)?,
                "{interface} darf ohne Grant nicht gelinkt sein"
            );
        }

        // Ein Preopen schaltet Dateisystem frei — und sonst nichts.
        let mut filesystem = CapabilityGrants::default();
        filesystem
            .filesystem_preopens
            .insert(std::env::temp_dir().to_string_lossy().into_owned());
        assert!(is_linked(&filesystem, "wasi:filesystem/types@0.2.12")?);
        assert!(is_linked(&filesystem, "wasi:filesystem/preopens@0.2.12")?);
        assert!(!is_linked(&filesystem, "wasi:sockets/tcp@0.2.12")?);
        assert!(!is_linked(&filesystem, "wasi:random/random@0.2.12")?);

        // Ein Netzwerkziel schaltet Sockets frei — und sonst nichts.
        let mut network = CapabilityGrants::default();
        network.network_allow.insert("127.0.0.1:9".to_owned());
        assert!(is_linked(&network, "wasi:sockets/tcp@0.2.12")?);
        assert!(!is_linked(&network, "wasi:filesystem/types@0.2.12")?);
        Ok(())
    }

    /// Die Preopen-Wurzel wird aufgelöst, bevor sie geöffnet wird: Ein Pfad mit `..` landet dort,
    /// wohin er zeigt, und eine nicht existierende Wurzel ist ein Fehler statt einer stillen
    /// Auslassung — sonst liefe ein Component mit weniger Zugriff als gewährt und niemand merkte
    /// den Konfigurationsfehler.
    #[test]
    fn preopen_roots_are_resolved_and_missing_ones_fail_closed() -> Result<()> {
        let root = std::env::temp_dir().join(format!("mcpmcp-preopen-{}", std::process::id()));
        std::fs::create_dir_all(&root)?;
        let uncanonical = root.join("..").join(root.file_name().unwrap());

        {
            let mut grants = CapabilityGrants::default();
            grants
                .filesystem_preopens
                .insert(uncanonical.to_string_lossy().into_owned());
            let mut builder = wasmtime_wasi::WasiCtxBuilder::new();
            // Ohne Auflösung würde `open_ambient_dir` an dem `..` scheitern.
            apply_grants_to_context(&mut builder, &grants, &Default::default())?;

            let mut missing = CapabilityGrants::default();
            missing
                .filesystem_preopens
                .insert(root.join("gibt-es-nicht").to_string_lossy().into_owned());
            let mut second = wasmtime_wasi::WasiCtxBuilder::new();
            let failure =
                apply_grants_to_context(&mut second, &missing, &Default::default()).unwrap_err();
            assert!(failure.to_string().contains("nicht auflösbar"));
        }

        // Erst nach dem Verwerfen der Builder ist das Verzeichnis-Handle wieder frei.
        std::fs::remove_dir_all(&root)?;
        Ok(())
    }

    /// Die Netzwerk-Allowlist wird beim Aufbau des Kontexts aufgelöst, nicht pro Verbindung —
    /// sonst entschiede DNS zur Laufzeit, wohin ein Grant zeigt.
    #[test]
    fn the_network_allowlist_resolves_once_and_rejects_unlistable_targets() -> Result<()> {
        let mut allow = BTreeSet::new();
        allow.insert("127.0.0.1:8080".to_owned());

        let resolved = resolve_network_allowlist(&allow)?;

        assert!(resolved.contains(&"127.0.0.1:8080".parse().unwrap()));
        assert!(!resolved.contains(&"127.0.0.1:8081".parse().unwrap()));

        let mut broken = BTreeSet::new();
        broken.insert("kein-port".to_owned());
        assert!(resolve_network_allowlist(&broken).is_err());
        Ok(())
    }

    #[test]
    fn typed_export_is_invoked_with_its_argument() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (bytes, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;

        let outcome = invoke_component_tool(
            &bytes,
            &CapabilityGrants::default(),
            "run",
            &[41],
            &ExecutionLimits::default(),
        )?;

        assert_eq!(outcome.result, Some(42));
        assert!(!outcome.truncated);
        Ok(())
    }

    #[test]
    fn unknown_tool_is_rejected_before_execution() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (bytes, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;

        let failure = invoke_component_tool(
            &bytes,
            &CapabilityGrants::default(),
            "nope",
            &[],
            &ExecutionLimits::default(),
        )
        .unwrap_err();

        assert!(failure.to_string().contains("not exported"));
        Ok(())
    }

    #[test]
    fn per_invocation_fuel_limit_is_enforced() -> Result<()> {
        let engine = hardened_engine(false, false)?;
        let (bytes, _) = compile_component(&engine, NO_IMPORT_COMPONENT)?;
        let starved = ExecutionLimits {
            fuel: Some(1),
            ..ExecutionLimits::default()
        };

        assert!(
            invoke_component_tool(&bytes, &CapabilityGrants::default(), "run", &[41], &starved)
                .is_err(),
            "ein Fuel-Budget von 1 muss den Aufruf abbrechen"
        );
        Ok(())
    }

    #[cfg(unix)]
    fn create_dir_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
        std::os::unix::fs::symlink(target, link)
    }

    #[cfg(windows)]
    fn create_dir_symlink(target: &Path, link: &Path) -> std::io::Result<()> {
        std::os::windows::fs::symlink_dir(target, link)
    }
}
