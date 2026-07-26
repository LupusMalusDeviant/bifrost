//! WP1 (Plan 0003, ADR-0020): IPC-Host für die WASI-Runtime.
//!
//! Der Rust-Host spricht mit dem .NET-Gateway über **length-prefixed JSON über stdio**: jede
//! Nachricht ist ein 4-Byte-Big-Endian-Längenpräfix gefolgt von einem JSON-Body. `stdout` gehört
//! dem Protokoll (Logs strikt auf `stderr`, wie die MCP-stdio-Server). Die Verarbeitung liegt in
//! einer reinen, testbaren [`Session`]; der Loop macht nur IO.
//!
//! Kommandos: `hello` (Versionsverhandlung **und Capability-Flags**), `load` (Signaturprüfung gegen
//! gepinnte Publisher + Grants + Kompilierung als Gesundheitstest, fail-closed), `discover`
//! (typisierte Beschreibung der aufrufbaren Exports), `invoke` (Aufruf eines Exports mit Limits und
//! Truncation-Metadaten), `cancel` (bestätigter Abbruch eines laufenden Aufrufs), `release`
//! (Freigabe eines Resource-Handles), `drain` (keine neuen Aufrufe mehr, auf die laufenden warten),
//! `health` (Bereitschaft, Phase, laufende Aufrufe, aktives Modul, Cache, offene Handles),
//! `shutdown`. Fehler sind strukturiert (`code` + `message`) und beenden den Host nicht.
//!
//! Der Lebenszyklus folgt ADR-0016: `handshake → load → discover → ready → invoke/cancel → drain →
//! stop`. Die Phase steht in `health`, und **Bereitschaft ist nicht Leben**: Ein drainierender Host
//! antwortet weiter, nimmt aber nichts mehr an. `shutdown` bricht ab, was noch läuft — wer sauber
//! aussteigen will, schickt vorher `drain`.
//!
//! Die Sitzung hält den Modul-Cache: Kompiliert wird einmal pro Inhalt, Grant-Satz und
//! Engine-Profil, nicht pro Aufruf (WP5).
//!
//! Mit `persistentInstance` hält sie zusätzlich die **laufende Guest-Instanz** — ohne die gäbe es
//! keine Resources, weil ein Handle nur in der Instanz gilt, die es ausgegeben hat. Die Instanz
//! gehört dem Upstream, die Handles je einem `caller`. Ein Reload beendet die Instanz.

use std::io::{self, Read, Write};
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::{Result, bail};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use ed25519_dalek::VerifyingKey;
use serde::{Deserialize, Serialize};

use crate::disk_cache::DiskCache;
use crate::{
    CachedModule, CapabilityGrants, ExecutionLimits, GrantAuditRecord, GuestSession,
    InvocationOutcome, ModuleCache, ModuleCacheStats, RUNTIME_VERSION, ToolDescriptor,
    describe_cached_module, grant_audit_record, invoke_cached_module_in, pinned_publisher,
    verify_component_signature,
};

/// Protokollversion des IPC-Vertrags. Inkompatible Versionen werden beim Handshake abgewiesen.
///
/// `2` (WP6.1): `discover` liefert typisierte Tool-Beschreibungen statt einer Namensliste — der
/// Aufrufer kann daraus ein echtes Schema erzeugen.
///
/// `3` (Aufrufbreite): Typen sind Bäume statt Namen, `args` und `result` sind JSON-Werte statt
/// `i32`. Wieder ein Bruch statt eines Zusatzfelds: Ein Client der Version 2 läse `result` als
/// Zahl und ließe ein Objekt oder einen Base64-Blob still unter den Tisch fallen — ein
/// Handshake-Fehler ist die ehrlichere Antwort.
/// `4` (Nebenlaeufigkeit und Abbruch): Jede Anfrage traegt eine `id`, jede Antwort gibt sie zurueck.
/// Damit duerfen Antworten in anderer Reihenfolge kommen als die Anfragen — die Voraussetzung
/// dafuer, dass mehrere Aufrufe desselben Upstreams nebeneinander laufen. Neu ist `cancel`, das
/// einen laufenden Aufruf ueber seine `id` trifft. Wieder ein Bruch statt eines Zusatzfelds: Ein
/// Client der Version 3 ordnet Antworten der Reihenfolge nach zu und wuerde unter Nebenlaeufigkeit
/// stillschweigend das falsche Ergebnis dem falschen Aufruf zuschreiben.
pub const PROTOCOL_VERSION: &str = "4";

/// Obergrenze für einen einzelnen Frame (Schutz gegen Memory-DoS über ein riesiges Längenpräfix).
const MAX_FRAME_BYTES: u32 = 64 * 1024 * 1024;

/// Was dieser Host kann (ADR-0016, WP6.1). Der Client liest die Flags beim Handshake und lehnt ab,
/// wenn ein von ihm **benötigtes** Feature fehlt — statt es beim ersten Aufruf herauszufinden.
///
/// Bewusst benannte Felder statt einer Namensliste: Ein Tippfehler in `"cancle"` wäre in einer
/// Liste ein stilles `false`, hier ein Compilerfehler. Fehlt das ganze Objekt in einer Antwort,
/// spricht ein älterer Host — auch das ist eine verwertbare Auskunft.
#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HostFeatures {
    /// Typisierte Discovery mit Typbäumen (seit Vertrag 2/3).
    pub typed_discovery: bool,
    /// `cancel` bricht einen laufenden Aufruf ab und bestätigt das (Vertrag 4).
    pub cancellation: bool,
    /// Mehrere Aufrufe gleichzeitig, zugeordnet über die Korrelations-Id (Vertrag 4).
    pub concurrency: bool,
    /// `drain` beendet die Annahme neuer Aufrufe und wartet auf die laufenden.
    pub drain: bool,
    /// `health` unterscheidet Leben von Bereitschaft.
    pub readiness: bool,
    /// Persistente Instanz je Upstream — Voraussetzung für Resources.
    pub persistent_instances: bool,
    /// `resource`-Handles über die Leitung.
    pub resources: bool,
    /// Secret-Injektion beim Laden.
    pub secrets: bool,
    /// Kompilat-Cache auf Platte.
    pub disk_cache: bool,
    /// `stream<T>` und `future<T>` — **bewusst `false`** und auf absehbare Zeit auch nicht anders
    /// (siehe `values`-Moduldoku): Ein dynamischer Host kann sie nur für fest einkompilierte
    /// Payload-Typen lesen.
    pub streams: bool,
}

impl HostFeatures {
    /// Was dieser Host tatsächlich kann.
    pub const fn current() -> Self {
        Self {
            typed_discovery: true,
            cancellation: true,
            concurrency: true,
            drain: true,
            readiness: true,
            persistent_instances: true,
            resources: true,
            secrets: true,
            disk_cache: true,
            streams: false,
        }
    }
}

/// In welcher Lebensphase die Sitzung steht (ADR-0016: `… → ready → invoke/cancel → drain → stop`).
#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum Phase {
    /// Handshake steht noch aus.
    #[default]
    Handshake,
    /// Verhandelt, aber ohne Component — Aufrufe gingen ins Leere.
    Negotiated,
    /// Bereit: geladen, und neue Aufrufe werden angenommen.
    Ready,
    /// Nimmt keine neuen Aufrufe mehr an; die laufenden dürfen zu Ende kommen.
    Draining,
}

/// Anfrage vom Gateway an den Host.
#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum Request {
    /// Handshake mit Versionsverhandlung.
    Hello {
        #[serde(rename = "protocolVersion")]
        protocol_version: String,
    },
    /// Lädt ein Component: Signatur gegen die gepinnten Publisher prüfen, Grants übernehmen.
    /// Alle Byte-Felder sind Base64 (JSON-tauglich, keine Sonderzeichen).
    Load {
        /// Component-Bytes, Base64.
        component: String,
        /// Detached Ed25519-Signatur (64 Byte), Base64.
        signature: String,
        /// Administrativ gepinnte Publisher-Public-Keys (je 32 Byte), Base64.
        #[serde(rename = "pinnedPublishers")]
        pinned_publishers: Vec<String>,
        /// Erteilte Host-Grants; fehlend = default-deny.
        #[serde(default)]
        grants: CapabilityGrants,
        /// Werte zu den in `grants.secrets` genannten Namen (WP4). Fehlend = keine Secrets.
        /// Bewusst getrennt von den Grants: Der Grant sagt, **was** ein Component sehen darf,
        /// dieses Feld liefert den Inhalt — und nur dieses Feld trägt Geheimnisse.
        #[serde(default, rename = "secretValues")]
        secret_values: std::collections::BTreeMap<String, String>,
        /// Hält die Guest-Instanz über die Aufrufe hinweg am Leben. Nötig für Resources: Ein
        /// Handle ist ein Index in die Instanz, die es ausgegeben hat.
        ///
        /// Voreinstellung ist `false`, und das ist die sicherere Wahl: Eine persistente Instanz
        /// teilt ihren internen Zustand (Globals, linearer Speicher) zwischen allen Aufrufern
        /// dieses Upstreams. Die Handle-Trennung schützt davor nicht.
        #[serde(default, rename = "persistentInstance")]
        persistent_instance: bool,
    },
    /// Listet die aufrufbaren Tools (Exports) des geladenen Components.
    Discover,
    /// Ruft ein Tool des geladenen Components auf.
    Invoke {
        /// Export-Name; unbekannte Namen werden abgewiesen.
        tool: String,
        /// Argumente für typisierte Exports, positionsweise in der Reihenfolge der Parameter.
        /// JSON-Werte statt Zahlen, seit der Host mehr als `s32` abbildet.
        #[serde(default)]
        args: Vec<serde_json::Value>,
        /// Limits für diesen Aufruf; fehlend = enge Defaults.
        #[serde(default)]
        limits: ExecutionLimits,
        /// Wem die Handles dieses Aufrufs gehören. Nur bei einer persistenten Instanz von Belang;
        /// fehlend = der namenlose Aufrufer, was für Upstreams ohne Resources genügt.
        #[serde(default)]
        caller: String,
    },
    /// Gibt ein Resource-Handle frei. Nur auf einer persistenten Instanz sinnvoll.
    Release {
        #[serde(default)]
        caller: String,
        handle: String,
    },
    /// Liveness/Readiness-Abfrage.
    Health,
    /// Bricht einen laufenden Aufruf ab. `target` ist die `id` der `invoke`-Anfrage.
    Cancel { target: u64 },
    /// Beendet die Annahme neuer Aufrufe und wartet, bis die laufenden fertig sind. Der Weg vor
    /// `shutdown`, wenn nichts abgeschnitten werden soll.
    Drain {
        /// Wie lange auf die laufenden Aufrufe gewartet wird. Fehlend = 5 Sekunden.
        #[serde(default, rename = "graceMs")]
        grace_ms: Option<u64>,
    },
    /// Ordentlicher Shutdown.
    Shutdown,
}

/// Anfrage samt Korrelations-Id, wie sie ueber die Leitung kommt (Vertrag v4).
#[derive(Debug, Deserialize)]
pub struct Envelope {
    pub id: u64,
    #[serde(flatten)]
    pub request: Request,
}

/// Antwort des Hosts an das Gateway.
#[derive(Debug, Deserialize, PartialEq, Serialize)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum Response {
    Hello {
        #[serde(rename = "protocolVersion")]
        protocol_version: String,
        runtime: String,
        host: String,
        /// Was dieser Host kann — der Client prüft daran seine Pflichtfeatures.
        features: HostFeatures,
    },
    Loaded {
        audit: GrantAuditRecord,
        /// Kompilierdauer dieses Loads — 0, wenn das Kompilat aus dem Cache kam (WP5.2).
        #[serde(rename = "compileMs")]
        compile_ms: f64,
        /// True, wenn das Component schon kompiliert vorlag.
        cached: bool,
    },
    Discovered {
        tools: Vec<ToolDescriptor>,
    },
    Invoked {
        #[serde(flatten)]
        outcome: InvocationOutcome,
    },
    Released {
        /// Wie viele Handles die Sitzung danach noch hält.
        handles: usize,
    },
    Drained {
        /// Aufrufe, die nach der Gnadenfrist **noch** liefen. 0 heißt: sauber leer.
        #[serde(rename = "inFlight")]
        in_flight: usize,
        /// `true`, wenn nichts mehr lief — dann kann `shutdown` niemanden mehr abschneiden.
        idle: bool,
    },
    Cancelled {
        target: u64,
        /// `true` nur, wenn der Aufruf wirklich geendet hat — nicht schon, wenn der Abbruch
        /// abgeschickt wurde. Ein `confirmed`, das niemand einloest, taeuschte Sicherheit vor
        /// (ADR-0019).
        confirmed: bool,
    },
    Health {
        status: String,
        loaded: bool,
        /// SHA-256 des aktiven Components — macht einen Rollback von außen nachprüfbar.
        #[serde(rename = "moduleSha256")]
        module_sha256: Option<String>,
        cache: ModuleCacheStats,
        /// Offene Resource-Handles der persistenten Instanz; 0 ohne persistente Instanz.
        #[serde(default)]
        handles: usize,
        /// **Bereitschaft**, nicht Leben: `true` nur, wenn ein Component geladen ist und der Host
        /// neue Aufrufe annimmt. Ein Host, der gerade drainiert, lebt — bereit ist er nicht, und
        /// wer beides verwechselt, schickt Aufrufe in eine sich schließende Tür.
        ready: bool,
        phase: Phase,
        /// Wie viele Aufrufe gerade laufen.
        #[serde(rename = "inFlight")]
        in_flight: usize,
    },
    Bye,
    Error {
        code: String,
        message: String,
    },
}

/// Steuert, ob der Loop nach einer Antwort weiterläuft oder ordentlich endet.
#[derive(Debug, PartialEq)]
pub enum Control {
    Continue,
    Stop,
}

/// Zustand einer IPC-Sitzung. Die Logik ist rein (kein IO) und damit direkt testbar.
#[derive(Default)]
pub struct Session {
    negotiated: bool,
    loaded: Option<LoadedComponent>,
    cache: ModuleCache,
    /// Gesetzt durch `drain`: Es werden keine neuen Aufrufe mehr angenommen.
    draining: bool,
    /// Die laufenden Aufrufe. Liegt hier statt nur im Loop, damit `health` sie melden kann.
    in_flight: Arc<InFlight>,
}

impl Session {
    /// Die Phase ergibt sich aus dem Zustand — bis auf `Draining`, das ein bewusster Schritt ist.
    pub fn phase(&self) -> Phase {
        if self.draining {
            Phase::Draining
        } else if self.loaded.is_some() {
            Phase::Ready
        } else if self.negotiated {
            Phase::Negotiated
        } else {
            Phase::Handshake
        }
    }

    /// Bereit heißt: geladen **und** annahmebereit. Nicht dasselbe wie „lebt".
    pub fn is_ready(&self) -> bool {
        matches!(self.phase(), Phase::Ready)
    }
}

/// Ein verifiziertes, geladenes und kompiliertes Component samt den dafür erteilten Grants.
/// Die Bytes bleiben liegen, damit ein Aufruf mit anderem Limit-Profil neu kompilieren kann,
/// ohne dass das Gateway das Component erneut schicken muss. `Arc`, weil sie pro Aufruf nur
/// weitergereicht werden.
struct LoadedComponent {
    bytes: Arc<Vec<u8>>,
    module: CachedModule,
    module_sha256: String,
    grants: CapabilityGrants,
    /// Werte zu den gewährten Secret-Namen. Bleiben im Prozess und gehen nie in eine Antwort:
    /// Der Audit-Datensatz nennt nur Namen.
    secret_values: Arc<std::collections::BTreeMap<String, String>>,
    /// Ob dieser Upstream eine über die Aufrufe hinweg lebende Instanz bekommt.
    persistent: bool,
    /// Die lebende Instanz, sobald der erste Aufruf sie gestartet hat.
    session: Option<GuestSession>,
    /// Das Engine-Profil (Fuel an/aus, Epoche an/aus), mit dem die Instanz gestartet wurde. Ein
    /// Aufruf mit anderem Profil bräuchte eine andere Engine — und damit eine andere Instanz, in
    /// der die bisherigen Handles nichts mehr bedeuten.
    session_profile: Option<(bool, bool)>,
}

fn error(code: &str, message: impl Into<String>) -> (Response, Control) {
    (
        Response::Error {
            code: code.to_owned(),
            message: message.into(),
        },
        Control::Continue,
    )
}

impl Session {
    /// Sitzung mit Platten-Cache: Kompilate überleben den Prozess (WP5).
    pub fn with_disk_cache(disk: DiskCache) -> Self {
        Self {
            negotiated: false,
            loaded: None,
            cache: ModuleCache::with_disk(disk),
            draining: false,
            in_flight: Arc::new(InFlight::default()),
        }
    }

    /// Verarbeitet eine Anfrage zu einer Antwort plus Loop-Steuerung.
    pub fn handle(&mut self, request: Request) -> (Response, Control) {
        match request {
            Request::Hello { protocol_version } => {
                if protocol_version != PROTOCOL_VERSION {
                    return (
                        Response::Error {
                            code: "unsupported-protocol".to_owned(),
                            message: format!(
                                "host spricht Protokoll {PROTOCOL_VERSION}, Client bot {protocol_version}"
                            ),
                        },
                        Control::Continue,
                    );
                }
                self.negotiated = true;
                (
                    Response::Hello {
                        protocol_version: PROTOCOL_VERSION.to_owned(),
                        runtime: RUNTIME_VERSION.to_owned(),
                        host: format!("mcpmcp-wasi-host/{}", env!("CARGO_PKG_VERSION")),
                        features: HostFeatures::current(),
                    },
                    Control::Continue,
                )
            }
            Request::Load {
                component,
                signature,
                pinned_publishers,
                grants,
                secret_values,
                persistent_instance,
            } => {
                if !self.negotiated {
                    return error("handshake-required", "hello muss vor load gesendet werden");
                }
                match self.load(
                    &component,
                    &signature,
                    &pinned_publishers,
                    grants,
                    secret_values,
                    persistent_instance,
                ) {
                    Ok(loaded) => (loaded, Control::Continue),
                    // Rollback (WP5.2): Ein fehlgeschlagener Load lässt das vorherige Component
                    // aktiv. Der eigene Code sagt das auch — sonst müsste der Betreiber raten,
                    // ob der Upstream jetzt tot oder auf dem alten Stand ist.
                    Err(failure) if self.loaded.is_some() => error(
                        "load-rolled-back",
                        format!("{failure} — das zuvor geladene Component bleibt aktiv"),
                    ),
                    Err(failure) => error("load-rejected", failure.to_string()),
                }
            }
            Request::Discover => {
                let Some(loaded) = self.loaded.as_ref() else {
                    return error("not-loaded", "kein Component geladen — load zuerst senden");
                };
                (
                    Response::Discovered {
                        tools: describe_cached_module(&loaded.module),
                    },
                    Control::Continue,
                )
            }
            Request::Invoke {
                tool,
                args,
                limits,
                caller,
            } if self.draining => {
                let _ = (tool, args, limits, caller);
                error(
                    "draining",
                    "der Host nimmt keine neuen Aufrufe mehr an (drain läuft)",
                )
            }
            Request::Invoke {
                tool,
                args,
                limits,
                caller,
            } => match self.loaded.as_ref().map(|loaded| loaded.persistent) {
                None => error("not-loaded", "kein Component geladen — load zuerst senden"),
                // Eine persistente Instanz gibt es genau einmal; ihr Aufruf läuft deshalb unter
                // dem Sitzungs-Lock und nicht nebenläufig.
                Some(true) => self.invoke_on_session(
                    &tool,
                    &args,
                    &limits,
                    &caller,
                    &crate::CallControl::new(),
                ),
                Some(false) => match self.prepare_detached(tool, args, limits, caller) {
                    Ok(call) => run_detached(&call, &crate::CallControl::new()),
                    Err(refusal) => *refusal,
                },
            },
            // Ohne Nebenläufigkeit kann hier nichts laufen, das abzubrechen wäre: Der Loop liest
            // erst weiter, wenn die Antwort geschrieben ist. `serve` behandelt `cancel` deshalb
            // selbst; diese Antwort gilt nur für den seriellen Weg (Tests, Einzelaufrufe).
            Request::Cancel { target } => (
                Response::Cancelled {
                    target,
                    confirmed: false,
                },
                Control::Continue,
            ),
            // Wie `cancel`: Ohne Nebenläufigkeit gibt es nichts, worauf zu warten wäre. `serve`
            // behandelt `drain` selbst und wartet dort wirklich.
            Request::Drain { .. } => {
                self.draining = true;
                let in_flight = self.in_flight.len();
                (
                    Response::Drained {
                        in_flight,
                        idle: in_flight == 0,
                    },
                    Control::Continue,
                )
            }
            Request::Release { caller, handle } => {
                let Some(session) = self
                    .loaded
                    .as_mut()
                    .and_then(|loaded| loaded.session.as_mut())
                else {
                    return error("no-session", "es läuft keine persistente Instanz");
                };
                match session.release(&caller, &handle) {
                    Ok(()) => (
                        Response::Released {
                            handles: session.handle_count(),
                        },
                        Control::Continue,
                    ),
                    Err(failure) => error("release-failed", format!("{failure:#}")),
                }
            }
            Request::Health => (
                Response::Health {
                    status: "ok".to_owned(),
                    loaded: self.loaded.is_some(),
                    module_sha256: self
                        .loaded
                        .as_ref()
                        .map(|loaded| loaded.module_sha256.clone()),
                    cache: self.cache.stats().clone(),
                    ready: self.is_ready(),
                    phase: self.phase(),
                    in_flight: self.in_flight.len(),
                    handles: self
                        .loaded
                        .as_ref()
                        .and_then(|loaded| loaded.session.as_ref())
                        .map_or(0, GuestSession::handle_count),
                },
                Control::Continue,
            ),
            Request::Shutdown => (Response::Bye, Control::Stop),
        }
    }

    /// Ruft auf der persistenten Instanz auf. Läuft unter dem Sitzungs-Lock, weil es diese
    /// Instanz genau einmal gibt — zwei gleichzeitige Aufrufe hätten denselben Store.
    fn invoke_on_session(
        &mut self,
        tool: &str,
        args: &[serde_json::Value],
        limits: &ExecutionLimits,
        caller: &str,
        control: &Arc<crate::CallControl>,
    ) -> (Response, Control) {
        let Some(loaded) = self.loaded.as_ref() else {
            return error("not-loaded", "kein Component geladen — load zuerst senden");
        };
        let bytes = Arc::clone(&loaded.bytes);
        let grants = loaded.grants.clone();
        let secrets = Arc::clone(&loaded.secret_values);
        let module = match self.cache.compile(&bytes, &grants, limits) {
            Ok(module) => module,
            Err(failure) => return error("invoke-failed", format!("{failure:#}")),
        };

        let loaded = self.loaded.as_mut().expect("gerade geprüft");
        // Ein anderes Engine-Profil hieße eine andere Instanz — und damit Handles, die ins Leere
        // zeigen. Das lieber sagen als still verlieren.
        let profile = (limits.fuel.is_some(), limits.timeout_ms.is_some());
        match loaded.session_profile {
            Some(started) if started != profile => {
                return error(
                    "session-profile-changed",
                    "die laufende Instanz wurde mit anderen Limit-Kategorien gestartet",
                );
            }
            _ => loaded.session_profile = Some(profile),
        }

        match invoke_cached_module_in(
            &module,
            &grants,
            &secrets,
            tool,
            args,
            limits,
            caller,
            Some(&mut loaded.session),
            control,
        ) {
            Ok(outcome) => (Response::Invoked { outcome }, Control::Continue),
            Err(failure) => invoke_failure(&failure, control),
        }
    }

    /// Schnürt einen Aufruf so zusammen, dass er die Sitzung nicht mehr braucht. Das Kompilieren
    /// passiert hier — es greift auf den Cache zu und gehört deshalb unter den Lock.
    fn prepare_detached(
        &mut self,
        tool: String,
        args: Vec<serde_json::Value>,
        limits: ExecutionLimits,
        caller: String,
    ) -> std::result::Result<DetachedCall, Box<(Response, Control)>> {
        let Some(loaded) = self.loaded.as_ref() else {
            return Err(Box::new(error(
                "not-loaded",
                "kein Component geladen — load zuerst senden",
            )));
        };
        // Der Aufruf darf andere Limit-Kategorien mitbringen als der Load; die stecken im
        // Engine-Profil und damit im Cache-Schlüssel. Der Cache liefert das passende Kompilat —
        // im Normalfall das vom Laden, sonst einmalig ein neues.
        let bytes = Arc::clone(&loaded.bytes);
        let grants = loaded.grants.clone();
        let secrets = Arc::clone(&loaded.secret_values);
        let module = match self.cache.compile(&bytes, &grants, &limits) {
            Ok(module) => module,
            Err(failure) => return Err(Box::new(error("invoke-failed", format!("{failure:#}")))),
        };
        Ok(DetachedCall {
            module,
            grants,
            secrets,
            tool,
            args,
            limits,
            caller,
        })
    }

    /// Dekodiert, verifiziert, **kompiliert** und übernimmt ein Component. Fail-closed: ohne
    /// gültige Signatur eines gepinnten Publishers wird nichts geladen, und der bisherige Zustand
    /// bleibt unberührt.
    ///
    /// Die Kompilierung ist hier der Gesundheitstest (WP5.2): Ein signiertes, aber kaputtes
    /// Component fällt beim Laden auf statt beim ersten Aufruf — und weil der bisherige Stand
    /// erst danach ersetzt wird, bleibt er in diesem Fall aktiv.
    fn load(
        &mut self,
        component_b64: &str,
        signature_b64: &str,
        pinned_b64: &[String],
        grants: CapabilityGrants,
        secret_values: std::collections::BTreeMap<String, String>,
        persistent_instance: bool,
    ) -> Result<Response> {
        let component = BASE64.decode(component_b64)?;
        let signature: [u8; 64] = BASE64
            .decode(signature_b64)?
            .try_into()
            .map_err(|_| anyhow::anyhow!("Signatur muss genau 64 Byte lang sein"))?;

        if pinned_b64.is_empty() {
            bail!("kein gepinnter Publisher übergeben — fail-closed");
        }
        let mut pinned = Vec::with_capacity(pinned_b64.len());
        for encoded in pinned_b64 {
            let key: [u8; 32] = BASE64
                .decode(encoded)?
                .try_into()
                .map_err(|_| anyhow::anyhow!("Publisher-Key muss genau 32 Byte lang sein"))?;
            pinned.push(pinned_publisher(VerifyingKey::from_bytes(&key)?));
        }

        let publisher_key_id = verify_component_signature(&component, &signature, &pinned)?;
        // Fail-closed vor dem Laden: Ein gewährtes Secret ohne Wert wäre sonst erst beim ersten
        // Aufruf aufgefallen — mit dem Upstream längst im Katalog.
        for name in &grants.secrets {
            if !secret_values.contains_key(name) {
                bail!("Secret '{name}' ist gewährt, aber es wurde kein Wert übergeben");
            }
        }
        let audit = grant_audit_record(&component, &publisher_key_id, &grants);

        // Kompilieren, BEVOR der bisherige Stand ersetzt wird.
        let hits_before = self.cache.stats().hits;
        let module = self
            .cache
            .compile(&component, &grants, &ExecutionLimits::default())?;
        let cached = self.cache.stats().hits > hits_before;

        self.loaded = Some(LoadedComponent {
            bytes: Arc::new(component),
            module_sha256: audit.module_sha256.clone(),
            module,
            grants,
            secret_values: Arc::new(secret_values),
            persistent: persistent_instance,
            // Ein neues Component heißt eine neue Instanz: Die Handles des vorherigen Stands
            // gehören zu einer Instanz, die es gleich nicht mehr gibt.
            session: None,
            session_profile: None,
        });
        Ok(Response::Loaded {
            compile_ms: if cached {
                0.0
            } else {
                self.cache.stats().last_compile_ms
            },
            cached,
            audit,
        })
    }
}

/// Schreibt eine Antwort als gerahmten Frame (Längenpräfix + JSON) und flusht.
pub fn write_frame<W: Write>(writer: &mut W, id: u64, response: &Response) -> Result<()> {
    let mut body = serde_json::to_vec(response)?;
    // Die Id wird in das bereits serialisierte Objekt eingesetzt, statt jede Antwortvariante um
    // ein Feld zu erweitern. `Response` bleibt damit das, was die Tests vergleichen.
    if body.first() == Some(&b'{') {
        let mut framed = format!("{{\"id\":{id},").into_bytes();
        framed.extend_from_slice(&body[1..]);
        body = framed;
    }
    let len = u32::try_from(body.len())?;
    writer.write_all(&len.to_be_bytes())?;
    writer.write_all(&body)?;
    writer.flush()?;
    Ok(())
}

/// Liest einen Frame-Body (ohne Längenpräfix). `Ok(None)` bei sauberem EOF vor dem nächsten Frame.
fn read_frame_bytes<R: Read>(reader: &mut R) -> Result<Option<Vec<u8>>> {
    let mut len_buf = [0u8; 4];
    match reader.read_exact(&mut len_buf) {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(error) => return Err(error.into()),
    }

    let len = u32::from_be_bytes(len_buf);
    if len > MAX_FRAME_BYTES {
        bail!("frame of {len} bytes exceeds the {MAX_FRAME_BYTES}-byte limit");
    }

    let mut body = vec![0u8; len as usize];
    reader.read_exact(&mut body)?;
    Ok(Some(body))
}

/// Die IPC-Schleife: Frames lesen, in der `Session` verarbeiten, Antworten rahmen. Ein
/// unparsbarer Frame ergibt eine `error`-Antwort und beendet den Host NICHT; `shutdown` und EOF
/// beenden ihn sauber.
/// Ein Aufruf, der von der Sitzung losgelöst ist: Alles, was er braucht, hält er selbst. Nur so
/// kann er außerhalb des Sitzungs-Locks laufen — und damit neben anderen Aufrufen.
struct DetachedCall {
    module: CachedModule,
    grants: CapabilityGrants,
    secrets: Arc<std::collections::BTreeMap<String, String>>,
    tool: String,
    args: Vec<serde_json::Value>,
    limits: ExecutionLimits,
    caller: String,
}

/// Führt einen losgelösten Aufruf aus. Frische Instanz, eigener Store — nichts davon wird geteilt.
fn run_detached(call: &DetachedCall, control: &Arc<crate::CallControl>) -> (Response, Control) {
    match invoke_cached_module_in(
        &call.module,
        &call.grants,
        &call.secrets,
        &call.tool,
        &call.args,
        &call.limits,
        &call.caller,
        None,
        control,
    ) {
        Ok(outcome) => (Response::Invoked { outcome }, Control::Continue),
        Err(failure) => invoke_failure(&failure, control),
    }
}

/// Ordnet einen gescheiterten Aufruf ein. Ein Trap sieht bei Abbruch und bei Zeitüberschreitung
/// gleich aus; den Unterschied kennt nur der Griff, über den abgebrochen wurde.
fn invoke_failure(
    failure: &anyhow::Error,
    control: &Arc<crate::CallControl>,
) -> (Response, Control) {
    if control.is_cancelled() {
        return error("cancelled", "der Aufruf wurde abgebrochen");
    }
    error("invoke-failed", format!("{failure:#}"))
}

/// Register der laufenden Aufrufe. `cancel` findet darüber seinen Aufruf, und weil der Eintrag
/// erst nach dem Ende verschwindet, lässt sich ein Abbruch **bestätigen** statt nur behaupten.
#[derive(Default)]
struct InFlight {
    calls: std::sync::Mutex<std::collections::HashMap<u64, Arc<crate::CallControl>>>,
    finished: std::sync::Condvar,
}

impl InFlight {
    /// Nimmt einen Aufruf auf, solange Platz ist. `false` heißt: Obergrenze erreicht.
    fn register(&self, id: u64, control: Arc<crate::CallControl>) -> bool {
        let mut calls = self.calls.lock().expect("laufende Aufrufe");
        if calls.len() >= MAX_CONCURRENT_CALLS {
            return false;
        }
        calls.insert(id, control);
        true
    }

    fn len(&self) -> usize {
        self.calls.lock().expect("laufende Aufrufe").len()
    }

    fn finish(&self, id: u64) {
        self.calls.lock().expect("laufende Aufrufe").remove(&id);
        self.finished.notify_all();
    }

    /// Wartet, bis kein Aufruf mehr läuft. `true`, wenn das innerhalb der Frist gelang.
    fn wait_idle(&self, grace: Duration) -> bool {
        let mut calls = self.calls.lock().expect("laufende Aufrufe");
        let deadline = Instant::now() + grace;
        while !calls.is_empty() {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return false;
            }
            let (next, _) = self
                .finished
                .wait_timeout(calls, remaining)
                .expect("auf das Ende warten");
            calls = next;
        }
        true
    }

    /// Bricht alles ab, was noch läuft. Für `shutdown`: Ohne das wartet der Thread-Scope am Ende
    /// von `serve` auf einen Guest, der vielleicht noch Minuten rechnet.
    fn cancel_all(&self) {
        for control in self.calls.lock().expect("laufende Aufrufe").values() {
            control.cancel();
        }
    }

    /// Bricht ab und wartet begrenzt darauf, dass der Aufruf wirklich endet. Rückgabe ist genau
    /// das `confirmed` des Vertrags: `true` erst, wenn der Aufruf weg ist.
    fn cancel(&self, target: u64, grace: Duration) -> bool {
        let mut calls = self.calls.lock().expect("laufende Aufrufe");
        let Some(control) = calls.get(&target).cloned() else {
            // Unbekannt heißt hier fast immer: schon fertig. Beides ist kein Abbruch.
            return false;
        };
        control.cancel();

        let deadline = Instant::now() + grace;
        while calls.contains_key(&target) {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return false;
            }
            let (next, _) = self
                .finished
                .wait_timeout(calls, remaining)
                .expect("auf das Ende warten");
            calls = next;
        }
        true
    }
}

/// Wie lange `cancel` auf das tatsächliche Ende wartet, bevor es `confirmed: false` meldet.
const CANCEL_GRACE: Duration = Duration::from_secs(5);

/// Vorgabe für `drain`, wenn der Aufrufer keine Frist mitgibt.
const DEFAULT_DRAIN_GRACE_MS: u64 = 5_000;

fn too_many_calls() -> (Response, Control) {
    error(
        "too-many-calls",
        format!("es laufen bereits {MAX_CONCURRENT_CALLS} Aufrufe — später erneut versuchen"),
    )
}

/// Wie viele Aufrufe gleichzeitig laufen dürfen.
///
/// Nebenläufigkeit hat einen Preis, den die serielle Fassung nicht hatte: Jeder Aufruf bekommt
/// einen eigenen Store mit eigenem Linear-Memory, und `maxMemoryBytes` gilt **pro Aufruf**. Ohne
/// Grenze wäre der Speicherbedarf des Hosts das Produkt aus Limit und Anzahl der Anfragen — ein
/// Angriff, der vorher schon daran scheiterte, dass nur ein Aufruf lief.
const MAX_CONCURRENT_CALLS: usize = 16;

fn write_response<W: Write>(
    writer: &std::sync::Mutex<&mut W>,
    id: u64,
    response: &Response,
) -> Result<()> {
    let mut guard = writer.lock().expect("Schreibseite");
    write_frame(&mut **guard, id, response)
}

/// Liest Frames, beantwortet sie und beendet sich bei `shutdown`.
///
/// Ab Vertrag v4 ist der Loop **nicht mehr streng seriell**: Aufrufe ohne persistente Instanz
/// laufen auf eigenen Threads, damit ein langsames Component den Upstream nicht blockiert. Was
/// Zustand ändert (`hello`, `load`, `release`, `shutdown`) und jeder Aufruf auf einer persistenten
/// Instanz laufen weiterhin unter dem Sitzungs-Lock — es gibt nur eine Instanz, und die kann nicht
/// zweimal gleichzeitig rechnen.
pub fn serve<R: Read, W: Write + Send>(
    reader: &mut R,
    writer: &mut W,
    disk_cache: Option<DiskCache>,
) -> Result<()> {
    let session = std::sync::Mutex::new(match disk_cache {
        Some(disk) => Session::with_disk_cache(disk),
        None => Session::default(),
    });
    let writer = std::sync::Mutex::new(writer);
    // Dasselbe Register, das auch `health` meldet — sonst wären es zwei Wahrheiten.
    let in_flight = Arc::clone(&session.lock().expect("Sitzung").in_flight);

    std::thread::scope(|scope| -> Result<()> {
        while let Some(body) = read_frame_bytes(reader)? {
            let envelope = match serde_json::from_slice::<Envelope>(&body) {
                Ok(envelope) => envelope,
                Err(failure) => {
                    // Ohne lesbare Hülle gibt es keine Id, auf die geantwortet werden könnte —
                    // 0 ist die reservierte „gehört zu keiner Anfrage"-Id.
                    write_response(
                        &writer,
                        0,
                        &Response::Error {
                            code: "bad-request".to_owned(),
                            message: failure.to_string(),
                        },
                    )?;
                    continue;
                }
            };

            let id = envelope.id;
            match envelope.request {
                // Abbruch wartet auf das Ende des Ziels — das darf den Leser nicht aufhalten,
                // sonst käme die Antwort des abgebrochenen Aufrufs nie durch.
                Request::Cancel { target } => {
                    let in_flight = &in_flight;
                    let writer = &writer;
                    scope.spawn(move || {
                        let confirmed = in_flight.cancel(target, CANCEL_GRACE);
                        let _ =
                            write_response(writer, id, &Response::Cancelled { target, confirmed });
                    });
                }
                // Drain wartet auf die laufenden Aufrufe — auf dem Leser-Thread wäre das ein
                // Deadlock, denn deren Antworten müssen ja noch hinaus.
                Request::Drain { grace_ms } => {
                    session.lock().expect("Sitzung").draining = true;
                    let grace = Duration::from_millis(grace_ms.unwrap_or(DEFAULT_DRAIN_GRACE_MS));
                    let in_flight = &in_flight;
                    let writer = &writer;
                    scope.spawn(move || {
                        let idle = in_flight.wait_idle(grace);
                        let _ = write_response(
                            writer,
                            id,
                            &Response::Drained {
                                in_flight: in_flight.len(),
                                idle,
                            },
                        );
                    });
                }
                Request::Invoke {
                    tool,
                    args,
                    limits,
                    caller,
                } if session.lock().expect("Sitzung").draining => {
                    let _ = (tool, args, limits, caller);
                    write_response(
                        &writer,
                        id,
                        &error(
                            "draining",
                            "der Host nimmt keine neuen Aufrufe mehr an (drain läuft)",
                        )
                        .0,
                    )?;
                }
                Request::Invoke {
                    tool,
                    args,
                    limits,
                    caller,
                } => {
                    // Die Entscheidung „nebenläufig oder nicht" fällt unter dem Lock, weil sie am
                    // geladenen Component hängt. Der Aufruf selbst fällt danach heraus.
                    let mut guard = session.lock().expect("Sitzung");
                    match guard.loaded.as_ref().map(|loaded| loaded.persistent) {
                        None => {
                            drop(guard);
                            write_response(
                                &writer,
                                id,
                                &error("not-loaded", "kein Component geladen — load zuerst senden")
                                    .0,
                            )?;
                        }
                        // Eine persistente Instanz gibt es genau einmal — ihre Aufrufe bleiben
                        // seriell, und zwar über denselben Lock, den auch `release` und `load`
                        // nehmen. Trotzdem laufen sie auf einem eigenen Thread: Blockierte der
                        // Leser hier, käme ein `cancel` gar nicht erst herein, und ausgerechnet
                        // der langlaufende Fall wäre nicht abbrechbar.
                        Some(true) => {
                            drop(guard);
                            let control = crate::CallControl::new();
                            if !in_flight.register(id, Arc::clone(&control)) {
                                write_response(&writer, id, &too_many_calls().0)?;
                                continue;
                            }
                            let in_flight = &in_flight;
                            let writer = &writer;
                            let session = &session;
                            scope.spawn(move || {
                                let (response, _) = {
                                    let mut guard = session.lock().expect("Sitzung");
                                    guard
                                        .invoke_on_session(&tool, &args, &limits, &caller, &control)
                                };
                                in_flight.finish(id);
                                let _ = write_response(writer, id, &response);
                            });
                        }
                        Some(false) => match guard.prepare_detached(tool, args, limits, caller) {
                            Err(refusal) => {
                                drop(guard);
                                write_response(&writer, id, &refusal.0)?;
                            }
                            Ok(call) => {
                                drop(guard);
                                // Eintragen **vor** dem Spawn: Sonst könnte ein unmittelbar
                                // folgendes `cancel` den Aufruf noch nicht finden und
                                // fälschlich melden, es gäbe nichts abzubrechen.
                                let control = crate::CallControl::new();
                                if !in_flight.register(id, Arc::clone(&control)) {
                                    write_response(&writer, id, &too_many_calls().0)?;
                                    continue;
                                }
                                let in_flight = &in_flight;
                                let writer = &writer;
                                scope.spawn(move || {
                                    let (response, _) = run_detached(&call, &control);
                                    in_flight.finish(id);
                                    let _ = write_response(writer, id, &response);
                                });
                            }
                        },
                    }
                }
                request => {
                    let (response, control) = {
                        let mut guard = session.lock().expect("Sitzung");
                        guard.handle(request)
                    };
                    write_response(&writer, id, &response)?;
                    if control == Control::Stop {
                        // Der Scope joint gleich alle Threads. Ein noch rechnender Guest hielte
                        // den Prozess sonst so lange fest, wie sein Limit erlaubt — und das
                        // Gateway würde ihn nach seiner eigenen Frist hart killen. Wer sauber
                        // aussteigen will, schickt vorher `drain`.
                        in_flight.cancel_all();
                        return Ok(());
                    }
                }
            }
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use ed25519_dalek::{Signer, SigningKey};

    use super::*;

    const GUEST: &[u8] = include_bytes!("../fixtures/wasi-p2-guest.component.wasm");

    /// Session nach erfolgreichem Handshake.
    fn negotiated() -> Session {
        let mut session = Session::default();
        session.handle(Request::Hello {
            protocol_version: PROTOCOL_VERSION.to_owned(),
        });
        session
    }

    /// Standard-Invoke auf den WASI-Kommando-Export (Name wird reflektiert, nicht geraten).
    fn run_request() -> Request {
        Request::Invoke {
            caller: String::new(),
            tool: crate::wasi_command_export(GUEST).unwrap(),
            args: vec![],
            limits: ExecutionLimits::default(),
        }
    }

    fn load_request(signing: &SigningKey, grants: CapabilityGrants) -> Request {
        Request::Load {
            persistent_instance: false,
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants,
            secret_values: Default::default(),
        }
    }

    fn frame(bytes: &[u8]) -> Vec<u8> {
        let len = u32::try_from(bytes.len()).unwrap();
        let mut framed = len.to_be_bytes().to_vec();
        framed.extend_from_slice(bytes);
        framed
    }

    fn first_response(input: &[u8]) -> Response {
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output, None).unwrap();
        let len = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
        serde_json::from_slice(&output[4..4 + len]).unwrap()
    }

    #[test]
    fn handshake_accepts_matching_version() {
        let (response, control) = Session::default().handle(Request::Hello {
            protocol_version: PROTOCOL_VERSION.to_owned(),
        });
        assert_eq!(control, Control::Continue);
        match response {
            Response::Hello {
                protocol_version,
                runtime,
                host,
                features,
            } => {
                assert_eq!(protocol_version, PROTOCOL_VERSION);
                assert!(runtime.contains("wasmtime"));
                assert!(host.starts_with("mcpmcp-wasi-host/"));
                // Der Handshake nennt die Fähigkeiten (ADR-0016) — auch die fehlenden.
                assert_eq!(features, HostFeatures::current());
                assert!(features.cancellation && features.drain && features.readiness);
                assert!(
                    !features.streams,
                    "Streams sind zurückgestellt, nicht offen"
                );
            }
            other => panic!("expected hello, got {other:?}"),
        }
    }

    #[test]
    fn handshake_rejects_mismatched_version() {
        let (response, _) = Session::default().handle(Request::Hello {
            protocol_version: "999".to_owned(),
        });
        assert!(matches!(
            response,
            Response::Error { code, .. } if code == "unsupported-protocol"
        ));
    }

    #[test]
    fn shutdown_stops_the_loop() {
        let (response, control) = Session::default().handle(Request::Shutdown);
        assert_eq!(response, Response::Bye);
        assert_eq!(control, Control::Stop);
    }

    #[test]
    fn health_reports_not_loaded_initially() {
        let (response, control) = Session::default().handle(Request::Health);
        assert_eq!(control, Control::Continue);
        assert_eq!(
            response,
            Response::Health {
                handles: 0,
                status: "ok".to_owned(),
                loaded: false,
                module_sha256: None,
                cache: ModuleCacheStats::default(),
                // Lebt, aber ohne Handshake nicht bereit — genau der Unterschied, um den es geht.
                ready: false,
                phase: Phase::Handshake,
                in_flight: 0,
            }
        );
    }

    #[test]
    fn load_requires_a_handshake_first() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let (response, _) =
            Session::default().handle(load_request(&signing, CapabilityGrants::default()));
        assert!(matches!(response, Response::Error { code, .. } if code == "handshake-required"));
    }

    #[test]
    fn load_verifies_signature_and_returns_the_audit_record() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();

        let (response, control) =
            session.handle(load_request(&signing, CapabilityGrants::default()));

        assert_eq!(control, Control::Continue);
        match response {
            Response::Loaded { audit, cached, .. } => {
                assert_eq!(audit.module_sha256, crate::sha256_hex(GUEST));
                assert_eq!(audit.runtime, RUNTIME_VERSION);
                assert!(audit.granted_filesystem_preopens.is_empty());
                assert!(!cached, "der erste Load kompiliert");
            }
            other => panic!("expected loaded, got {other:?}"),
        }
        // health spiegelt den Ladezustand.
        let (health, _) = session.handle(Request::Health);
        match health {
            Response::Health {
                handles: 0,
                loaded,
                module_sha256,
                cache,
                ..
            } => {
                assert!(loaded);
                assert_eq!(
                    module_sha256.as_deref(),
                    Some(crate::sha256_hex(GUEST).as_str())
                );
                assert_eq!(cache.entries, 1, "der Load hat genau ein Kompilat erzeugt");
            }
            other => panic!("expected health, got {other:?}"),
        }
    }

    /// Die committeten Fixture-Dateien (Component, detached Signatur, Publisher-Key) müssen
    /// zusammenpassen — dieselben drei Dateien fährt der .NET-Kompatibilitätstest (Plan 0003,
    /// WP6.2) über die echte IPC-Leitung. Bricht dieser Test, wurde eine der Dateien ohne die
    /// anderen erneuert; regenerieren mit:
    /// `mcpmcp-wasi-component-spike sign fixtures/wasi-p2-guest.component.wasm <seed-hex>`
    /// (Dev-Testvektor-Seed: 0x03 × 32 — kein Geheimnis, nur ein reproduzierbarer Testschlüssel).
    ///
    /// Dasselbe gilt für `tools-interface.component.wasm`; dessen Quelle liegt unter
    /// `guest-interface/` und wird mit `cargo build --release --target wasm32-wasip2` erzeugt.
    #[test]
    fn the_committed_signature_fixture_loads() {
        let signature = include_bytes!("../fixtures/wasi-p2-guest.component.sig");
        let publisher = include_str!("../fixtures/wasi-p2-guest.publisher.pub");

        let (response, _) = negotiated().handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signature),
            pinned_publishers: vec![publisher.trim().to_owned()],
            grants: CapabilityGrants::default(),
            secret_values: Default::default(),
        });

        match response {
            Response::Loaded { audit, .. } => {
                assert_eq!(audit.module_sha256, crate::sha256_hex(GUEST));
            }
            other => panic!("committed fixture triple no longer loads: {other:?}"),
        }
    }

    /// WP4: Ein gewährtes Secret erreicht den Guest tatsächlich — die Fixture-Component gibt den
    /// Wert von `MCPMCP_SPIKE` aus, hier also den injizierten Secret-Wert statt des Platzhalters
    /// „granted". Ohne diesen Test wäre die Injektion nur behauptet.
    #[test]
    fn a_granted_secret_reaches_the_guest() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut grants = CapabilityGrants::default();
        grants.secrets.insert("MCPMCP_SPIKE".to_owned());
        let mut session = negotiated();

        let (loaded, _) = session.handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants,
            secret_values: [("MCPMCP_SPIKE".to_owned(), "s3hr-geheim".to_owned())]
                .into_iter()
                .collect(),
        });
        let (invoked, _) = session.handle(run_request());

        match loaded {
            // Der Audit-Datensatz nennt den Namen, nie den Wert.
            Response::Loaded { audit, .. } => {
                assert_eq!(audit.granted_secrets, ["MCPMCP_SPIKE"]);
                let serialized = serde_json::to_string(&audit).unwrap();
                assert!(
                    !serialized.contains("s3hr-geheim"),
                    "der Secret-Wert darf nirgends in einer Antwort auftauchen: {serialized}"
                );
            }
            other => panic!("expected loaded, got {other:?}"),
        }
        match invoked {
            Response::Invoked { outcome } => {
                assert!(
                    outcome.stdout.contains("mcpmcp-guest-ok:s3hr-geheim"),
                    "der Guest sah den Secret-Wert nicht: {}",
                    outcome.stdout
                );
            }
            other => panic!("expected invoked, got {other:?}"),
        }
    }

    /// Fail-closed: Ein gewährter Secret-Name ohne Wert ist ein Ladefehler. Sonst liefe das
    /// Component mit einem Secret, das es für gesetzt hält.
    #[test]
    fn a_granted_secret_without_a_value_fails_the_load() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut grants = CapabilityGrants::default();
        grants.secrets.insert("MCPMCP_SPIKE".to_owned());

        let (response, _) = negotiated().handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants,
            secret_values: Default::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "load-rejected"));
    }

    /// WP5.2: Ein fehlgeschlagener Load lässt den bisherigen Stand aktiv — und sagt das auch.
    /// Der Fall ist nicht theoretisch: Ein signiertes, aber kaputtes Component käme sonst durch
    /// den Load und fiele erst beim ersten Aufruf auf, mit dem Upstream längst im Katalog.
    #[test]
    fn a_broken_component_rolls_back_to_the_previous_one() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();
        session.handle(load_request(&signing, CapabilityGrants::default()));

        // Korrekt signiert, aber kein gültiges Component: Die Signatur allein sagt nichts über
        // die Ladbarkeit.
        let broken = b"kein wasm".to_vec();
        let (response, control) = session.handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(&broken),
            signature: BASE64.encode(signing.sign(&broken).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants: CapabilityGrants::default(),
            secret_values: Default::default(),
        });

        assert_eq!(control, Control::Continue, "der Host lebt weiter");
        assert!(
            matches!(&response, Response::Error { code, .. } if code == "load-rolled-back"),
            "erwartet load-rolled-back, bekam {response:?}"
        );

        // Der alte Stand ist noch aktiv und aufrufbar.
        match session.handle(Request::Health).0 {
            Response::Health { module_sha256, .. } => {
                assert_eq!(
                    module_sha256.as_deref(),
                    Some(crate::sha256_hex(GUEST).as_str())
                );
            }
            other => panic!("expected health, got {other:?}"),
        }
        assert!(matches!(
            session.handle(Request::Discover).0,
            Response::Discovered { .. }
        ));
    }

    /// Ohne vorher geladenes Component gibt es nichts zurückzurollen — dann bleibt es beim
    /// gewöhnlichen `load-rejected`, damit der Aufrufer die beiden Lagen unterscheiden kann.
    #[test]
    fn a_first_failed_load_is_a_plain_rejection() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let broken = b"kein wasm".to_vec();

        let (response, _) = negotiated().handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(&broken),
            signature: BASE64.encode(signing.sign(&broken).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants: CapabilityGrants::default(),
            secret_values: Default::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "load-rejected"));
    }

    /// WP5.1 über den Sitzungszustand: Der zweite Load desselben Components kompiliert nicht
    /// erneut, und `health` weist das nach.
    #[test]
    fn a_second_load_of_the_same_component_reuses_the_compilation() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();

        session.handle(load_request(&signing, CapabilityGrants::default()));
        let (second, _) = session.handle(load_request(&signing, CapabilityGrants::default()));

        match second {
            Response::Loaded {
                cached, compile_ms, ..
            } => {
                assert!(cached, "das Kompilat lag schon vor");
                assert_eq!(compile_ms, 0.0, "ein Treffer kostet keine Kompilierzeit");
            }
            other => panic!("expected loaded, got {other:?}"),
        }
        match session.handle(Request::Health).0 {
            Response::Health { cache, .. } => {
                assert_eq!(cache.entries, 1);
                assert_eq!(cache.hits, 1);
                assert_eq!(cache.misses, 1);
            }
            other => panic!("expected health, got {other:?}"),
        }
    }

    /// Auch das Interface-Fixture muss zu seiner Signatur passen — sonst schlägt der
    /// .NET-Test dagegen mit einer Ursache fehl, die im Rust-Teil längst sichtbar wäre.
    #[test]
    fn the_committed_interface_fixture_loads() {
        let component = include_bytes!("../fixtures/tools-interface.component.wasm");
        let signature = include_bytes!("../fixtures/tools-interface.component.sig");
        let publisher = include_str!("../fixtures/wasi-p2-guest.publisher.pub");

        let (response, _) = negotiated().handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(component),
            signature: BASE64.encode(signature),
            pinned_publishers: vec![publisher.trim().to_owned()],
            grants: CapabilityGrants::default(),
            secret_values: Default::default(),
        });

        assert!(
            matches!(response, Response::Loaded { .. }),
            "Interface-Fixture laedt nicht: {response:?}"
        );
    }

    #[test]
    fn load_is_rejected_for_an_unpinned_publisher() {
        let trusted = SigningKey::from_bytes(&[1u8; 32]);
        let rogue = SigningKey::from_bytes(&[2u8; 32]);
        let mut session = negotiated();

        let (response, _) = session.handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(rogue.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(trusted.verifying_key().as_bytes())],
            grants: CapabilityGrants::default(),
            secret_values: Default::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "load-rejected"));
    }

    #[test]
    fn load_without_any_pinned_publisher_fails_closed() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();

        let (response, _) = session.handle(Request::Load {
            persistent_instance: false,
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![],
            grants: CapabilityGrants::default(),
            secret_values: Default::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "load-rejected"));
    }

    #[test]
    fn invoke_without_load_is_rejected() {
        let (response, _) = negotiated().handle(run_request());
        assert!(matches!(response, Response::Error { code, .. } if code == "not-loaded"));
    }

    #[test]
    fn invoke_runs_only_with_granted_capabilities() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);

        // Ohne Grant: WASI wird nicht gelinkt -> Ausfuehrung scheitert vor dem Start.
        let mut denied = negotiated();
        denied.handle(load_request(&signing, CapabilityGrants::default()));
        let (response, _) = denied.handle(run_request());
        assert!(matches!(response, Response::Error { code, .. } if code == "invoke-failed"));

        // Mit Environment-Grant laeuft die echte Component.
        let mut grants = CapabilityGrants::default();
        grants.environment.insert("MCPMCP_SPIKE".to_owned());
        let mut allowed = negotiated();
        allowed.handle(load_request(&signing, grants));
        let (response, _) = allowed.handle(run_request());
        match response {
            Response::Invoked { outcome } => {
                assert!(outcome.stdout.contains("mcpmcp-guest-ok"));
                assert!(!outcome.truncated);
            }
            other => panic!("expected invoked, got {other:?}"),
        }
    }

    #[test]
    fn discover_lists_the_loaded_components_tools() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();
        session.handle(load_request(&signing, CapabilityGrants::default()));

        let (response, control) = session.handle(Request::Discover);

        assert_eq!(control, Control::Continue);
        match response {
            Response::Discovered { tools } => {
                // Genau EIN Eintrag für den Kommando-Einstiegspunkt (WP6.1) — vor der
                // Normalisierung standen hier zusätzlich seine innere `run`-Funktion.
                let commands: Vec<_> = tools
                    .iter()
                    .filter(|tool| crate::is_wasi_command_export(&tool.name))
                    .collect();
                assert_eq!(
                    commands.len(),
                    1,
                    "erwartet genau ein Kommando-Tool: {tools:?}"
                );
                assert_eq!(commands[0].kind, crate::ToolKind::Command);
                assert!(commands[0].supported);
                assert!(commands[0].params.is_empty());
            }
            other => panic!("expected discovered, got {other:?}"),
        }
    }

    #[test]
    fn discover_without_load_is_rejected() {
        let (response, _) = negotiated().handle(Request::Discover);
        assert!(matches!(response, Response::Error { code, .. } if code == "not-loaded"));
    }

    #[test]
    fn invoking_an_unknown_tool_is_rejected() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();
        session.handle(load_request(&signing, CapabilityGrants::default()));

        let (response, _) = session.handle(Request::Invoke {
            caller: String::new(),
            tool: "does:not/exist@1.0.0".to_owned(),
            args: vec![],
            limits: ExecutionLimits::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "invoke-failed"));
    }

    #[test]
    fn serve_frames_a_hello_response_then_ends_on_eof() {
        let input = frame(br#"{"id":1,"type":"hello","protocolVersion":"4"}"#);
        assert!(matches!(first_response(&input), Response::Hello { .. }));
    }

    /// Reader, der absichtlich nur häppchenweise liefert — bildet echte Pipe-Semantik ab, bei der
    /// ein `read` weniger Bytes zurückgibt als angefragt.
    struct ChunkedReader<'a> {
        data: &'a [u8],
        chunk: usize,
    }

    impl Read for ChunkedReader<'_> {
        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            let take = self.chunk.min(buf.len()).min(self.data.len());
            buf[..take].copy_from_slice(&self.data[..take]);
            self.data = &self.data[take..];
            Ok(take)
        }
    }

    #[test]
    fn framing_survives_partial_reads() {
        let input = frame(br#"{"id":1,"type":"hello","protocolVersion":"4"}"#);
        let mut reader = ChunkedReader {
            data: &input,
            chunk: 1, // ein Byte pro read — der härteste Fall
        };
        let mut output = Vec::new();
        serve(&mut reader, &mut output, None).unwrap();

        let len = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
        let response: Response = serde_json::from_slice(&output[4..4 + len]).unwrap();
        assert!(matches!(response, Response::Hello { .. }));
    }

    #[test]
    fn framing_round_trips_across_many_payload_sizes() {
        // Deterministische Größenreihe statt Zufall: reproduzierbar in CI, deckt Grenzen um
        // Puffergrößen (1, 255, 256, 4 KiB, 64 KiB …) ab.
        for size in [0usize, 1, 2, 127, 128, 255, 256, 1023, 4096, 65_535, 65_536] {
            let response = Response::Invoked {
                outcome: InvocationOutcome {
                    stdout: "x".repeat(size),
                    truncated: false,
                    result: None,
                },
            };
            let mut buffer = Vec::new();
            write_frame(&mut buffer, 1, &response).unwrap();

            let body = read_frame_bytes(&mut &buffer[..]).unwrap().unwrap();
            let decoded: Response = serde_json::from_slice(&body).unwrap();
            assert_eq!(decoded, response, "round-trip failed for {size} bytes");
        }
    }

    #[test]
    fn an_oversized_length_prefix_is_rejected() {
        let mut input = (MAX_FRAME_BYTES + 1).to_be_bytes().to_vec();
        input.extend_from_slice(b"{}");

        let failure = read_frame_bytes(&mut &input[..]).unwrap_err();

        assert!(failure.to_string().contains("exceeds"));
    }

    #[test]
    fn a_truncated_frame_errors_instead_of_hanging() {
        // Präfix kündigt 64 Byte an, es folgen aber nur 4.
        let mut input = 64u32.to_be_bytes().to_vec();
        input.extend_from_slice(b"abcd");

        assert!(read_frame_bytes(&mut &input[..]).is_err());
    }

    #[test]
    fn a_clean_eof_between_frames_ends_the_stream() {
        assert!(read_frame_bytes(&mut &b""[..]).unwrap().is_none());
    }

    #[test]
    fn serve_rejects_a_malformed_frame_without_dying() {
        let mut input = frame(b"not valid json");
        input.extend(frame(br#"{"id":2,"type":"shutdown"}"#));
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output, None).unwrap();

        // Erste Antwort: bad-request; der Host lebt weiter und verarbeitet das folgende shutdown.
        let first_len = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
        let first: Response = serde_json::from_slice(&output[4..4 + first_len]).unwrap();
        assert!(matches!(first, Response::Error { code, .. } if code == "bad-request"));
        let rest = &output[4 + first_len..];
        let second_len = u32::from_be_bytes(rest[..4].try_into().unwrap()) as usize;
        let second: Response = serde_json::from_slice(&rest[4..4 + second_len]).unwrap();
        assert_eq!(second, Response::Bye);
    }

    const COUNTER: &[u8] = include_bytes!("../fixtures/counter.component.wasm");

    /// Lädt das Resource-Fixture in eine bereits verhandelte Sitzung.
    fn load_counter(session: &mut Session, persistent: bool) {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        // Rusts std zieht wasi:cli/environment mit, auch wenn der Guest es nicht benutzt.
        let mut grants = CapabilityGrants::default();
        grants.environment.insert("MCPMCP_SPIKE".to_owned());

        let (response, _) = session.handle(Request::Load {
            persistent_instance: persistent,
            component: BASE64.encode(COUNTER),
            signature: BASE64.encode(signing.sign(COUNTER).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants,
            secret_values: Default::default(),
        });
        assert!(
            matches!(response, Response::Loaded { .. }),
            "load muss gelingen: {response:?}"
        );
    }

    fn counter_session(persistent: bool) -> Session {
        let mut session = negotiated();
        load_counter(&mut session, persistent);
        session
    }

    fn invoke(
        session: &mut Session,
        caller: &str,
        tool: &str,
        args: Vec<serde_json::Value>,
    ) -> Response {
        session
            .handle(Request::Invoke {
                caller: caller.to_owned(),
                tool: tool.to_owned(),
                args,
                limits: ExecutionLimits::default(),
            })
            .0
    }

    fn result_of(response: Response) -> serde_json::Value {
        match response {
            Response::Invoked { outcome } => outcome.result.expect("Rückgabewert"),
            other => panic!("expected invoked, got {other:?}"),
        }
    }

    const COUNTER_NEW: &str = "mcpmcp:counter/counters@0.1.0.[constructor]counter";
    const COUNTER_BUMP: &str = "mcpmcp:counter/counters@0.1.0.[method]counter.bump";

    /// Über den echten Protokollweg: Handle aus einem Aufruf, Zustand im nächsten. Das ist der
    /// Unterschied, den `persistentInstance` ausmacht.
    #[test]
    fn a_persistent_instance_carries_handles_between_requests() {
        let mut session = counter_session(true);

        let handle = result_of(invoke(
            &mut session,
            "alice",
            COUNTER_NEW,
            vec![serde_json::json!(1)],
        ));
        let bumped = result_of(invoke(
            &mut session,
            "alice",
            COUNTER_BUMP,
            vec![handle.clone(), serde_json::json!(41)],
        ));
        assert_eq!(bumped, serde_json::json!(42));

        // Und `health` sagt, wie viele Handles offen sind — ein Leck wäre von außen sichtbar.
        match session.handle(Request::Health).0 {
            Response::Health { handles, .. } => assert_eq!(handles, 1),
            other => panic!("expected health, got {other:?}"),
        }

        // Freigeben schließt das Handle; danach ist es auch für den Eigentümer weg.
        match session.handle(Request::Release {
            caller: "alice".to_owned(),
            handle: handle[crate::values::HANDLE_FIELD]
                .as_str()
                .unwrap()
                .to_owned(),
        }) {
            (Response::Released { handles }, _) => assert_eq!(handles, 0),
            (other, _) => panic!("expected released, got {other:?}"),
        }
        match invoke(
            &mut session,
            "alice",
            COUNTER_BUMP,
            vec![handle, serde_json::json!(1)],
        ) {
            Response::Error { code, message } => {
                assert_eq!(code, "invoke-failed");
                assert!(message.contains("unbekannt"), "{message}");
            }
            other => panic!("expected error, got {other:?}"),
        }
    }

    /// Ohne `persistentInstance` bleibt es beim bisherigen Verhalten: frische Instanz pro Aufruf,
    /// also keine Handles. Der Fehler nennt den Grund.
    #[test]
    fn without_the_flag_resources_are_refused() {
        let mut session = counter_session(false);

        match invoke(
            &mut session,
            "alice",
            COUNTER_NEW,
            vec![serde_json::json!(1)],
        ) {
            Response::Error { code, message } => {
                assert_eq!(code, "invoke-failed");
                assert!(message.contains("persistente Instanz"), "{message}");
            }
            other => panic!("expected error, got {other:?}"),
        }
    }

    /// Ein neues Component beendet die Instanz. Die Handles des vorherigen Stands zeigen danach
    /// auf nichts mehr — und das sagt der Host, statt einen fremden Zähler zu bedienen.
    #[test]
    fn reloading_ends_the_session() {
        let mut session = counter_session(true);
        let handle = result_of(invoke(
            &mut session,
            "alice",
            COUNTER_NEW,
            vec![serde_json::json!(7)],
        ));

        // Dasselbe Component noch einmal laden: Der Cache liefert dasselbe Kompilat, die Instanz
        // ist trotzdem eine neue.
        load_counter(&mut session, true);

        match invoke(
            &mut session,
            "alice",
            COUNTER_BUMP,
            vec![handle, serde_json::json!(1)],
        ) {
            Response::Error { code, message } => {
                assert_eq!(code, "invoke-failed");
                assert!(message.contains("unbekannt"), "{message}");
            }
            other => panic!("expected error, got {other:?}"),
        }
    }

    /// Ein Aufruf mit anderem Limit-Profil bräuchte eine andere Engine und damit eine andere
    /// Instanz. Der Host weist das ab, statt die Handles still zu verlieren.
    #[test]
    fn a_changed_limit_profile_is_refused_while_a_session_lives() {
        let mut session = counter_session(true);
        result_of(invoke(
            &mut session,
            "alice",
            COUNTER_NEW,
            vec![serde_json::json!(1)],
        ));

        let (response, _) = session.handle(Request::Invoke {
            caller: "alice".to_owned(),
            tool: COUNTER_NEW.to_owned(),
            args: vec![serde_json::json!(1)],
            limits: ExecutionLimits {
                fuel: None,
                ..ExecutionLimits::default()
            },
        });
        match response {
            Response::Error { code, .. } => assert_eq!(code, "session-profile-changed"),
            other => panic!("expected error, got {other:?}"),
        }
    }

    /// Zerlegt einen fertigen Eingabestrom wieder in einzelne Frames.
    fn split_frames(input: &[u8]) -> Vec<Vec<u8>> {
        let mut rest = input;
        let mut frames = Vec::new();
        while rest.len() >= 4 {
            let len = u32::from_be_bytes(rest[..4].try_into().unwrap()) as usize;
            frames.push(rest[..4 + len].to_vec());
            rest = &rest[4 + len..];
        }
        frames
    }

    /// Zerlegt einen Ausgabestrom in (id, Antwort) — die Reihenfolge ist ab v4 nicht mehr die der
    /// Anfragen, also muss der Test über die Id zuordnen.
    fn responses(output: &[u8]) -> Vec<(u64, Response)> {
        let mut rest = output;
        let mut collected = Vec::new();
        while rest.len() >= 4 {
            let len = u32::from_be_bytes(rest[..4].try_into().unwrap()) as usize;
            let body = &rest[4..4 + len];
            let id = serde_json::from_slice::<serde_json::Value>(body).unwrap()["id"]
                .as_u64()
                .expect("jede Antwort trägt ihre Korrelations-Id");
            collected.push((id, serde_json::from_slice(body).unwrap()));
            rest = &rest[4 + len..];
        }
        collected
    }

    /// Baut die Frames für einen Lauf: Handshake, signierter Load, dann die übergebenen Rohframes.
    fn wire(component: &[u8], persistent: bool, requests: &[String]) -> Vec<u8> {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let load = serde_json::json!({
            "id": 2,
            "type": "load",
            "component": BASE64.encode(component),
            "signature": BASE64.encode(signing.sign(component).to_bytes()),
            "pinnedPublishers": [BASE64.encode(signing.verifying_key().as_bytes())],
            "grants": {
                "filesystemPreopens": [], "networkAllow": [], "environment": ["MCPMCP_SPIKE"],
                "secrets": [], "clock": false, "random": false
            },
            "persistentInstance": persistent,
        });
        let mut input = frame(
            format!(r#"{{"id":1,"type":"hello","protocolVersion":"{PROTOCOL_VERSION}"}}"#)
                .as_bytes(),
        );
        input.extend(frame(serde_json::to_vec(&load).unwrap().as_slice()));
        for request in requests {
            input.extend(frame(request.as_bytes()));
        }
        input
    }

    /// Ein Component, das nie fertig wird — der einzige ehrliche Weg, einen Abbruch zu prüfen.
    fn spinning_component() -> Vec<u8> {
        wat::parse_str(crate::INFINITE_COMPONENT).unwrap()
    }

    /// Jede Antwort trägt die Id ihrer Anfrage zurück. Ohne das wäre Nebenläufigkeit nicht
    /// zuzuordnen — genau der Grund für den Versionsbruch auf 4.
    #[test]
    fn every_response_carries_the_id_of_its_request() {
        let input = wire(
            COUNTER,
            false,
            &[
                r#"{"id":7,"type":"discover"}"#.to_owned(),
                r#"{"id":9,"type":"health"}"#.to_owned(),
            ],
        );
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output, None).unwrap();

        let seen = responses(&output);
        assert_eq!(seen[0].0, 1, "hello");
        assert_eq!(seen[1].0, 2, "load");
        let ids: Vec<u64> = seen.iter().map(|(id, _)| *id).collect();
        assert!(ids.contains(&7) && ids.contains(&9), "{ids:?}");
        assert!(
            seen.iter()
                .any(|(id, response)| *id == 7 && matches!(response, Response::Discovered { .. }))
        );
    }

    /// Ein laufender Aufruf lässt sich abbrechen, und der Abbruch wird **bestätigt** — nicht bloß
    /// entgegengenommen. Das Component dreht sich endlos; ohne Abbruch liefe der Test in den
    /// Timeout statt in ein `cancelled`.
    #[test]
    fn a_running_call_can_be_cancelled_and_the_cancel_is_confirmed() {
        let spinning = spinning_component();
        let input = wire(
            &spinning,
            false,
            &[
                // Grosszuegiger Timeout: Es soll der Abbruch greifen, nicht die Frist.
                r#"{"id":10,"type":"invoke","tool":"spin","limits":{"fuel":null,"timeoutMs":60000,"maxMemoryBytes":null,"maxOutputBytes":65536}}"#.to_owned(),
                r#"{"id":11,"type":"cancel","target":10}"#.to_owned(),
            ],
        );
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output, None).unwrap();

        let seen = responses(&output);
        let invoke = seen
            .iter()
            .find(|(id, _)| *id == 10)
            .expect("Aufruf-Antwort");
        let cancel = seen
            .iter()
            .find(|(id, _)| *id == 11)
            .expect("Abbruch-Antwort");

        // Eigener Code: „abgebrochen" ist etwas anderes als „gescheitert" oder „Zeit abgelaufen".
        assert!(
            matches!(&invoke.1, Response::Error { code, .. } if code == "cancelled"),
            "{:?}",
            invoke.1
        );
        assert_eq!(
            cancel.1,
            Response::Cancelled {
                target: 10,
                confirmed: true
            },
            "bestätigt heißt: der Aufruf ist wirklich beendet"
        );
    }

    /// Ein Abbruch auf eine unbekannte Id ist kein Fehler, aber auch keine Bestätigung.
    #[test]
    fn cancelling_an_unknown_call_is_not_confirmed() {
        let input = wire(
            COUNTER,
            false,
            &[r#"{"id":12,"type":"cancel","target":999}"#.to_owned()],
        );
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output, None).unwrap();

        let seen = responses(&output);
        assert_eq!(
            seen.iter().find(|(id, _)| *id == 12).map(|(_, r)| r),
            Some(&Response::Cancelled {
                target: 999,
                confirmed: false
            })
        );
    }

    /// Der Kern von v4: Zwei Aufrufe laufen nebeneinander. Der erste dreht sich, bis er
    /// abgebrochen wird — der zweite darf darauf nicht warten müssen. Vor v4 hätte der Loop erst
    /// nach der Antwort des ersten weitergelesen.
    #[test]
    fn a_slow_call_does_not_block_the_next_one() {
        let spinning = spinning_component();
        let input = wire(
            &spinning,
            false,
            &[
                r#"{"id":20,"type":"invoke","tool":"spin","limits":{"fuel":null,"timeoutMs":60000,"maxMemoryBytes":null,"maxOutputBytes":65536}}"#.to_owned(),
                r#"{"id":21,"type":"health"}"#.to_owned(),
                r#"{"id":22,"type":"cancel","target":20}"#.to_owned(),
            ],
        );
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output, None).unwrap();

        let seen = responses(&output);
        let order: Vec<u64> = seen.iter().map(|(id, _)| *id).collect();
        let health = order.iter().position(|id| *id == 21).expect("health");
        let invoke = order.iter().position(|id| *id == 20).expect("invoke");
        assert!(
            health < invoke,
            "health muss vor dem hängenden Aufruf beantwortet sein, war {order:?}"
        );
    }

    /// Bereitschaft ist nicht Leben. Ein Host ohne Component antwortet auf `health`, ist aber
    /// nicht bereit — wer das verwechselt, schickt Aufrufe an einen Upstream ohne Component.
    #[test]
    fn readiness_is_not_liveness() {
        let mut session = negotiated();
        match session.handle(Request::Health).0 {
            Response::Health {
                status,
                ready,
                phase,
                ..
            } => {
                assert_eq!(status, "ok", "der Host lebt");
                assert!(!ready, "ohne Component ist er nicht bereit");
                assert_eq!(phase, Phase::Negotiated);
            }
            other => panic!("expected health, got {other:?}"),
        }

        load_counter(&mut session, false);
        match session.handle(Request::Health).0 {
            Response::Health { ready, phase, .. } => {
                assert!(ready);
                assert_eq!(phase, Phase::Ready);
            }
            other => panic!("expected health, got {other:?}"),
        }
    }

    /// `drain` beendet die Annahme neuer Aufrufe. Das ist der Schritt vor `shutdown`, und er muss
    /// sichtbar sein: Ein Aufruf danach bekommt einen eigenen Code, keinen allgemeinen Fehler.
    #[test]
    fn draining_refuses_new_calls_and_shows_it_in_health() {
        let mut session = counter_session(false);

        match session.handle(Request::Drain { grace_ms: Some(0) }).0 {
            Response::Drained { in_flight, idle } => {
                assert_eq!(in_flight, 0);
                assert!(idle, "es lief nichts, also ist der Drain sofort sauber");
            }
            other => panic!("expected drained, got {other:?}"),
        }

        match invoke(
            &mut session,
            "alice",
            COUNTER_NEW,
            vec![serde_json::json!(1)],
        ) {
            Response::Error { code, .. } => assert_eq!(code, "draining"),
            other => panic!("expected error, got {other:?}"),
        }

        match session.handle(Request::Health).0 {
            Response::Health { ready, phase, .. } => {
                assert!(!ready, "wer drainiert, ist nicht mehr bereit");
                assert_eq!(phase, Phase::Draining);
            }
            other => panic!("expected health, got {other:?}"),
        }
    }

    /// Reader, der Frames **über die Zeit** liefert statt alle auf einmal. Bei einem Slice als
    /// Quelle rast der Loop durch und die nebenläufigen Antworten überholen einander; ein echter
    /// Client schickt gestaffelt. Nur so ist eine Reihenfolge prüfbar.
    struct PacedReader {
        frames: std::collections::VecDeque<Vec<u8>>,
        pending: Vec<u8>,
        gap: Duration,
        first: bool,
    }

    impl PacedReader {
        fn new(frames: Vec<Vec<u8>>, gap: Duration) -> Self {
            Self {
                frames: frames.into(),
                pending: Vec::new(),
                gap,
                first: true,
            }
        }
    }

    impl Read for PacedReader {
        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            if self.pending.is_empty() {
                let Some(next) = self.frames.pop_front() else {
                    return Ok(0);
                };
                if !std::mem::take(&mut self.first) {
                    std::thread::sleep(self.gap);
                }
                self.pending = next;
            }
            let take = self.pending.len().min(buf.len());
            buf[..take].copy_from_slice(&self.pending[..take]);
            self.pending.drain(..take);
            Ok(take)
        }
    }

    /// Über den Loop: `drain` wartet wirklich auf den laufenden Aufruf, statt nur ein Flag zu
    /// setzen. Der Aufruf dreht sich, wird abgebrochen — erst danach meldet der Drain `idle`.
    #[test]
    fn drain_waits_for_running_calls() {
        let spinning = spinning_component();
        let input = wire(
            &spinning,
            false,
            &[
                r#"{"id":30,"type":"invoke","tool":"spin","limits":{"fuel":null,"timeoutMs":60000,"maxMemoryBytes":null,"maxOutputBytes":65536}}"#.to_owned(),
                // Kurze Frist: Der Aufruf laeuft noch, der Drain kann nicht sauber melden.
                r#"{"id":31,"type":"drain","graceMs":100}"#.to_owned(),
                r#"{"id":32,"type":"cancel","target":30}"#.to_owned(),
                // Jetzt ist nichts mehr unterwegs.
                r#"{"id":33,"type":"drain","graceMs":2000}"#.to_owned(),
            ],
        );
        // Gestaffelt, damit die Frist des ersten Drains ablaeuft, bevor der Abbruch eintrifft.
        let mut reader = PacedReader::new(split_frames(&input), Duration::from_millis(300));
        let mut output = Vec::new();
        serve(&mut reader, &mut output, None).unwrap();
        let seen = responses(&output);

        let first = seen.iter().find(|(id, _)| *id == 31).expect("erster Drain");
        assert_eq!(
            first.1,
            Response::Drained {
                in_flight: 1,
                idle: false
            },
            "solange der Aufruf laeuft, ist der Drain nicht sauber"
        );

        let second = seen
            .iter()
            .find(|(id, _)| *id == 33)
            .expect("zweiter Drain");
        assert_eq!(
            second.1,
            Response::Drained {
                in_flight: 0,
                idle: true
            }
        );
    }

    /// `shutdown` darf nicht auf einen rechnenden Guest warten. Ohne den Abbruch hielte der
    /// Thread-Scope den Prozess so lange fest, wie das Limit erlaubt — und das Gateway würde ihn
    /// nach seiner eigenen Frist hart killen.
    #[test]
    fn shutdown_does_not_wait_for_a_running_call() {
        let spinning = spinning_component();
        let input = wire(
            &spinning,
            false,
            &[
                r#"{"id":40,"type":"invoke","tool":"spin","limits":{"fuel":null,"timeoutMs":60000,"maxMemoryBytes":null,"maxOutputBytes":65536}}"#.to_owned(),
                r#"{"id":41,"type":"shutdown"}"#.to_owned(),
            ],
        );
        let mut output = Vec::new();
        let started = std::time::Instant::now();
        serve(&mut &input[..], &mut output, None).unwrap();
        let elapsed = started.elapsed();

        assert!(
            elapsed < Duration::from_secs(30),
            "shutdown wartete {elapsed:?} — der laufende Aufruf haette abgebrochen werden muessen"
        );
        let seen = responses(&output);
        assert!(
            seen.iter()
                .any(|(id, response)| *id == 41 && *response == Response::Bye)
        );
    }
}
