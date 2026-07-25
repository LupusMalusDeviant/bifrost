//! Guest-Component, das `mcpmcp:spike/tools` implementiert — die Fixture-Gegenprobe zum
//! Interface-Aufruf im Host.
//!
//! Das WIT kommt bewusst aus `docs/spikes/fixtures/control-plane.wit` statt aus einer Kopie:
//! Derselbe Vertrag beschreibt hier den Guest und dort den Host-Test. Liefen beide auseinander,
//! wäre der Test wertlos, ohne dass es jemand merkt.

wit_bindgen::generate!({
    path: "../../../docs/spikes/fixtures/control-plane.wit",
    world: "connector",
});

use exports::mcpmcp::spike::tools::{Guest, Mode, Request, Response};

struct Component;

impl Guest for Component {
    /// Nimmt den Record entgegen und antwortet mit einem Record — oder mit einem Fehlerstring.
    /// Die Logik ist absichtlich trivial; geprüft wird der Weg der Werte, nicht ihr Inhalt.
    fn run(input: Request) -> Result<Response, String> {
        if input.name.is_empty() {
            return Err("name darf nicht leer sein".to_owned());
        }

        let mode = match input.mode {
            Mode::Fast => "fast",
            Mode::Safe => "safe",
        };
        // Alle Feldarten einmal anfassen: Skalar, String, Enum, Liste, Option.
        Ok(Response {
            accepted: matches!(input.mode, Mode::Safe),
            message: format!(
                "{}:{}:{}:{}:{}",
                input.id,
                input.name,
                mode,
                input.tags.join("+"),
                input.note.unwrap_or_else(|| "-".to_owned())
            ),
        })
    }
}

export!(Component);
