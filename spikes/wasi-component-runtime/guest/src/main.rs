// Minimaler WASI-P2-Guest. Nutzt bewusst echte WASI-Interfaces: `wasi:cli/environment` (env::var)
// und `wasi:cli/stdout` (println). Das mit `--target wasm32-wasip2` erzeugte Artefakt ist ein
// echtes WebAssembly Component und dient dem Spike als reales Import-Fixture (nicht nur benannt).
fn main() {
    let who = std::env::var("MCPMCP_SPIKE").unwrap_or_else(|_| "anonymous".to_owned());
    // NICHT UMBENENNEN, solange das Fixture nicht neu signiert werden kann: Diese Marke steht so
    // im ausgelieferten 'fixtures/wasi-p2-guest.component.wasm', und das Component ist SIGNIERT.
    // Im Repo liegt nur der oeffentliche Schluessel — ein neu gebautes Component liesse sich hier
    // nicht signieren und wuerde von der Vertrauenspruefung abgewiesen. Marke und Fixture wechseln
    // deshalb nur gemeinsam, mit dem privaten Schluessel in der Hand.
    println!("mcpmcp-guest-ok:{who}");
}
