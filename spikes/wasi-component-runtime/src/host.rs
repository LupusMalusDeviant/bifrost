//! WP1 (Plan 0003, ADR-0020): IPC-Host für die WASI-Runtime.
//!
//! Der Rust-Host spricht mit dem .NET-Gateway über **length-prefixed JSON über stdio**: jede
//! Nachricht ist ein 4-Byte-Big-Endian-Längenpräfix gefolgt von einem JSON-Body. `stdout` gehört
//! dem Protokoll (Logs strikt auf `stderr`, wie die MCP-stdio-Server). Die Verarbeitung liegt in
//! einer reinen, testbaren [`Session`]; der Loop macht nur IO.
//!
//! Kommandos: `hello` (Versionsverhandlung), `load` (Signaturprüfung gegen gepinnte Publisher +
//! Grants, fail-closed), `discover` (typisierte Beschreibung der aufrufbaren Exports), `invoke`
//! (Aufruf eines Exports mit Limits und Truncation-Metadaten), `health`, `shutdown`. Fehler sind
//! strukturiert (`code` + `message`) und beenden den Host nicht.

use std::io::{self, Read, Write};

use anyhow::{Result, bail};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use ed25519_dalek::VerifyingKey;
use serde::{Deserialize, Serialize};

use crate::{
    CapabilityGrants, ExecutionLimits, GrantAuditRecord, InvocationOutcome, RUNTIME_VERSION,
    ToolDescriptor, describe_component_tools, grant_audit_record, invoke_component_tool,
    pinned_publisher, verify_component_signature,
};

/// Protokollversion des IPC-Vertrags. Inkompatible Versionen werden beim Handshake abgewiesen.
///
/// `2` (WP6.1): `discover` liefert typisierte Tool-Beschreibungen statt einer Namensliste — der
/// Aufrufer kann daraus ein echtes Schema erzeugen. Bewusst ein Bruch statt eines Zusatzfelds:
/// Ein Client, der die alte Namensliste erwartet, soll am Handshake scheitern und nicht an einem
/// stillschweigend anders geformten `tools`-Feld.
pub const PROTOCOL_VERSION: &str = "2";

/// Obergrenze für einen einzelnen Frame (Schutz gegen Memory-DoS über ein riesiges Längenpräfix).
const MAX_FRAME_BYTES: u32 = 64 * 1024 * 1024;

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
    },
    /// Listet die aufrufbaren Tools (Exports) des geladenen Components.
    Discover,
    /// Ruft ein Tool des geladenen Components auf.
    Invoke {
        /// Export-Name; unbekannte Namen werden abgewiesen.
        tool: String,
        /// Argumente für typisierte Exports (Spike: `s32`).
        #[serde(default)]
        args: Vec<i32>,
        /// Limits für diesen Aufruf; fehlend = enge Defaults.
        #[serde(default)]
        limits: ExecutionLimits,
    },
    /// Liveness/Readiness-Abfrage.
    Health,
    /// Ordentlicher Shutdown.
    Shutdown,
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
    },
    Loaded {
        audit: GrantAuditRecord,
    },
    Discovered {
        tools: Vec<ToolDescriptor>,
    },
    Invoked {
        #[serde(flatten)]
        outcome: InvocationOutcome,
    },
    Health {
        status: String,
        loaded: bool,
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
#[derive(Debug, Default)]
pub struct Session {
    negotiated: bool,
    loaded: Option<LoadedComponent>,
}

/// Ein verifiziertes, geladenes Component samt den dafür erteilten Grants.
#[derive(Debug)]
struct LoadedComponent {
    bytes: Vec<u8>,
    grants: CapabilityGrants,
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
                    },
                    Control::Continue,
                )
            }
            Request::Load {
                component,
                signature,
                pinned_publishers,
                grants,
            } => {
                if !self.negotiated {
                    return error("handshake-required", "hello muss vor load gesendet werden");
                }
                match self.load(&component, &signature, &pinned_publishers, grants) {
                    Ok(audit) => (Response::Loaded { audit }, Control::Continue),
                    Err(failure) => error("load-rejected", failure.to_string()),
                }
            }
            Request::Discover => {
                let Some(loaded) = self.loaded.as_ref() else {
                    return error("not-loaded", "kein Component geladen — load zuerst senden");
                };
                match describe_component_tools(&loaded.bytes) {
                    Ok(tools) => (Response::Discovered { tools }, Control::Continue),
                    Err(failure) => error("discover-failed", failure.to_string()),
                }
            }
            Request::Invoke { tool, args, limits } => {
                let Some(loaded) = self.loaded.as_ref() else {
                    return error("not-loaded", "kein Component geladen — load zuerst senden");
                };
                match invoke_component_tool(&loaded.bytes, &loaded.grants, &tool, &args, &limits) {
                    Ok(outcome) => (Response::Invoked { outcome }, Control::Continue),
                    Err(failure) => error("invoke-failed", failure.to_string()),
                }
            }
            Request::Health => (
                Response::Health {
                    status: "ok".to_owned(),
                    loaded: self.loaded.is_some(),
                },
                Control::Continue,
            ),
            Request::Shutdown => (Response::Bye, Control::Stop),
        }
    }

    /// Dekodiert, verifiziert und übernimmt ein Component. Fail-closed: ohne gültige Signatur
    /// eines gepinnten Publishers wird nichts geladen und der bisherige Zustand bleibt unberührt.
    fn load(
        &mut self,
        component_b64: &str,
        signature_b64: &str,
        pinned_b64: &[String],
        grants: CapabilityGrants,
    ) -> Result<GrantAuditRecord> {
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
        let audit = grant_audit_record(&component, &publisher_key_id, &grants);
        self.loaded = Some(LoadedComponent {
            bytes: component,
            grants,
        });
        Ok(audit)
    }
}

/// Schreibt eine Antwort als gerahmten Frame (Längenpräfix + JSON) und flusht.
pub fn write_frame<W: Write>(writer: &mut W, response: &Response) -> Result<()> {
    let body = serde_json::to_vec(response)?;
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
pub fn serve<R: Read, W: Write>(reader: &mut R, writer: &mut W) -> Result<()> {
    let mut session = Session::default();
    while let Some(body) = read_frame_bytes(reader)? {
        match serde_json::from_slice::<Request>(&body) {
            Ok(request) => {
                let (response, control) = session.handle(request);
                write_frame(writer, &response)?;
                if control == Control::Stop {
                    return Ok(());
                }
            }
            Err(error) => {
                write_frame(
                    writer,
                    &Response::Error {
                        code: "bad-request".to_owned(),
                        message: error.to_string(),
                    },
                )?;
            }
        }
    }
    Ok(())
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
            tool: crate::wasi_command_export(GUEST).unwrap(),
            args: vec![],
            limits: ExecutionLimits::default(),
        }
    }

    fn load_request(signing: &SigningKey, grants: CapabilityGrants) -> Request {
        Request::Load {
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants,
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
        serve(&mut &input[..], &mut output).unwrap();
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
            } => {
                assert_eq!(protocol_version, PROTOCOL_VERSION);
                assert!(runtime.contains("wasmtime"));
                assert!(host.starts_with("mcpmcp-wasi-host/"));
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
                status: "ok".to_owned(),
                loaded: false,
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
            Response::Loaded { audit } => {
                assert_eq!(audit.module_sha256, crate::sha256_hex(GUEST));
                assert_eq!(audit.runtime, RUNTIME_VERSION);
                assert!(audit.granted_filesystem_preopens.is_empty());
            }
            other => panic!("expected loaded, got {other:?}"),
        }
        // health spiegelt den Ladezustand.
        let (health, _) = session.handle(Request::Health);
        assert_eq!(
            health,
            Response::Health {
                status: "ok".to_owned(),
                loaded: true,
            }
        );
    }

    /// Die committeten Fixture-Dateien (Component, detached Signatur, Publisher-Key) müssen
    /// zusammenpassen — dieselben drei Dateien fährt der .NET-Kompatibilitätstest (Plan 0003,
    /// WP6.2) über die echte IPC-Leitung. Bricht dieser Test, wurde eine der Dateien ohne die
    /// anderen erneuert; regenerieren mit:
    /// `mcpmcp-wasi-component-spike sign fixtures/wasi-p2-guest.component.wasm <seed-hex>`
    /// (Dev-Testvektor-Seed: 0x03 × 32 — kein Geheimnis, nur ein reproduzierbarer Testschlüssel).
    #[test]
    fn the_committed_signature_fixture_loads() {
        let signature = include_bytes!("../fixtures/wasi-p2-guest.component.sig");
        let publisher = include_str!("../fixtures/wasi-p2-guest.publisher.pub");

        let (response, _) = negotiated().handle(Request::Load {
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signature),
            pinned_publishers: vec![publisher.trim().to_owned()],
            grants: CapabilityGrants::default(),
        });

        match response {
            Response::Loaded { audit } => {
                assert_eq!(audit.module_sha256, crate::sha256_hex(GUEST));
            }
            other => panic!("committed fixture triple no longer loads: {other:?}"),
        }
    }

    #[test]
    fn load_is_rejected_for_an_unpinned_publisher() {
        let trusted = SigningKey::from_bytes(&[1u8; 32]);
        let rogue = SigningKey::from_bytes(&[2u8; 32]);
        let mut session = negotiated();

        let (response, _) = session.handle(Request::Load {
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(rogue.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(trusted.verifying_key().as_bytes())],
            grants: CapabilityGrants::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "load-rejected"));
    }

    #[test]
    fn load_without_any_pinned_publisher_fails_closed() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();

        let (response, _) = session.handle(Request::Load {
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![],
            grants: CapabilityGrants::default(),
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
            tool: "does:not/exist@1.0.0".to_owned(),
            args: vec![],
            limits: ExecutionLimits::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "invoke-failed"));
    }

    #[test]
    fn serve_frames_a_hello_response_then_ends_on_eof() {
        let input = frame(br#"{"type":"hello","protocolVersion":"2"}"#);
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
        let input = frame(br#"{"type":"hello","protocolVersion":"2"}"#);
        let mut reader = ChunkedReader {
            data: &input,
            chunk: 1, // ein Byte pro read — der härteste Fall
        };
        let mut output = Vec::new();
        serve(&mut reader, &mut output).unwrap();

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
            write_frame(&mut buffer, &response).unwrap();

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
        input.extend(frame(br#"{"type":"shutdown"}"#));
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output).unwrap();

        // Erste Antwort: bad-request; der Host lebt weiter und verarbeitet das folgende shutdown.
        let first_len = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
        let first: Response = serde_json::from_slice(&output[4..4 + first_len]).unwrap();
        assert!(matches!(first, Response::Error { code, .. } if code == "bad-request"));
        let rest = &output[4 + first_len..];
        let second_len = u32::from_be_bytes(rest[..4].try_into().unwrap()) as usize;
        let second: Response = serde_json::from_slice(&rest[4..4 + second_len]).unwrap();
        assert_eq!(second, Response::Bye);
    }
}
