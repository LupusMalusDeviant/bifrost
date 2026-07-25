use std::path::Path;

use anyhow::{Context, Result, bail};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use ed25519_dalek::{Signer, SigningKey};
use mcpmcp_wasi_component_spike::{
    compare_with_container, discover_wit, pinned_publisher, run_runtime_probe,
};

fn main() {
    if let Err(error) = run() {
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let mut arguments = std::env::args().skip(1);
    match arguments.next().as_deref() {
        Some("discover") => {
            let path = arguments
                .next()
                .context("usage: mcpmcp-wasi-component-spike discover <wit-path> [world]")?;
            let world = arguments.next().unwrap_or_else(|| "connector".to_owned());
            if arguments.next().is_some() {
                bail!("unexpected extra argument");
            }
            let inventory = discover_wit(Path::new(&path), &world)?;
            println!("{}", serde_json::to_string_pretty(&inventory)?);
        }
        Some("probe") => {
            if arguments.next().is_some() {
                bail!("usage: mcpmcp-wasi-component-spike probe");
            }
            println!("{}", serde_json::to_string_pretty(&run_runtime_probe()?)?);
        }
        Some("compare-container") => {
            let image = arguments.next().context(
                "usage: mcpmcp-wasi-component-spike compare-container <image> [samples]",
            )?;
            let samples = arguments
                .next()
                .map(|value| value.parse())
                .transpose()
                .context("samples must be a positive integer")?
                .unwrap_or(7);
            if arguments.next().is_some() {
                bail!("unexpected extra argument");
            }
            println!(
                "{}",
                serde_json::to_string_pretty(&compare_with_container(&image, samples)?)?
            );
        }
        Some("sign") => {
            let path = arguments
                .next()
                .context("usage: mcpmcp-wasi-component-spike sign <component-path> <seed-hex>")?;
            let seed_hex = arguments
                .next()
                .context("usage: mcpmcp-wasi-component-spike sign <component-path> <seed-hex>")?;
            if arguments.next().is_some() {
                bail!("unexpected extra argument");
            }
            println!(
                "{}",
                serde_json::to_string_pretty(&sign(&path, &seed_hex)?)?
            );
        }
        Some("host") => {
            // Ohne --cache-dir bleibt der Modul-Cache prozesslokal. Ein Verzeichnis wird NICHT
            // geraten: Ein weltschreibbares Temp-Verzeichnis waere hier die falsche Vorgabe.
            let cache_directory = match arguments.next().as_deref() {
                None => None,
                Some("--cache-dir") => {
                    Some(arguments.next().context("--cache-dir braucht einen Pfad")?)
                }
                Some(other) => bail!(
                    "unbekanntes Argument '{other}' — usage: mcpmcp-wasi-component-spike host [--cache-dir <pfad>]"
                ),
            };
            if arguments.next().is_some() {
                bail!("usage: mcpmcp-wasi-component-spike host [--cache-dir <pfad>]");
            }
            let disk_cache = cache_directory
                .map(mcpmcp_wasi_component_spike::disk_cache::DiskCache::open)
                .transpose()?;
            let stdin = std::io::stdin();
            let stdout = std::io::stdout();
            mcpmcp_wasi_component_spike::host::serve(
                &mut stdin.lock(),
                &mut stdout.lock(),
                disk_cache,
            )?;
        }
        _ => {
            bail!(
                "usage:\n  mcpmcp-wasi-component-spike discover <wit-path> [world]\n  mcpmcp-wasi-component-spike probe\n  mcpmcp-wasi-component-spike compare-container <image> [samples]\n  mcpmcp-wasi-component-spike host [--cache-dir <pfad>]\n  mcpmcp-wasi-component-spike sign <component-path> <seed-hex>"
            );
        }
    }
    Ok(())
}

/// Signiert Component-Bytes mit einem Ed25519-Seed und gibt Public Key, Signatur und Key-Id
/// (Base64 bzw. Hex) aus. **Test-/Fixture-Werkzeug**, kein Publisher-Key-Management: der Seed steht
/// auf der Kommandozeile und landet damit in der Shell-History. Echte Publisher-Schlüssel werden in
/// WP4 verwaltet (Trust-Store, ADR-0020); hier geht es nur darum, die committeten Testsignaturen
/// reproduzierbar erzeugen zu können.
fn sign(component_path: &str, seed_hex: &str) -> Result<serde_json::Value> {
    let seed = decode_hex(seed_hex)?;
    let seed: [u8; 32] = seed
        .try_into()
        .map_err(|_| anyhow::anyhow!("seed must be exactly 32 bytes (64 hex chars)"))?;
    let component = std::fs::read(component_path)
        .with_context(|| format!("component '{component_path}' is not readable"))?;

    let signing = SigningKey::from_bytes(&seed);
    let publisher = pinned_publisher(signing.verifying_key());
    Ok(serde_json::json!({
        "publicKey": BASE64.encode(signing.verifying_key().as_bytes()),
        "signature": BASE64.encode(signing.sign(&component).to_bytes()),
        "keyId": publisher.key_id,
    }))
}

fn decode_hex(value: &str) -> Result<Vec<u8>> {
    if !value.len().is_multiple_of(2) {
        bail!("hex input must have an even number of characters");
    }
    (0..value.len())
        .step_by(2)
        .map(|index| {
            u8::from_str_radix(&value[index..index + 2], 16).context("invalid hex digit in seed")
        })
        .collect()
}
