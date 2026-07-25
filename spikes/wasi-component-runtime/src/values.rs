//! Typen und Werte zwischen Component-Model und JSON (Plan 0003, Aufrufbreite).
//!
//! Bis hierher konnte der Host genau eine Form aufrufen: `(s32) -> s32`. Das reicht für einen
//! Spike, nicht für Connectoren — [ADR-0017](../../../docs/adr/0017-wasi-component-runtime.md)
//! nennt `list<u8>` als begrenzte Binärdaten und `result<T,E>` als Fehlervertrag ausdrücklich.
//!
//! Zwei Festlegungen prägen die Abbildung:
//!
//! - **`list<u8>` ist kein Zahlen-Array, sondern ein Blob.** Er geht als Base64-String über die
//!   Leitung und ist der Länge nach begrenzt. Ein Megabyte Binärdaten als JSON-Array einzelner
//!   Zahlen wäre um ein Vielfaches größer und für den Aufrufer unbrauchbar.
//! - **64-Bit-Ganzzahlen sind Strings.** JSON-Zahlen sind Doubles; ab 2^53 verlöre `u64` still
//!   Stellen. Lieber ein String, den man ansieht, als eine Zahl, die man glaubt.
//!
//! Abgebildet sind alle Skalare, `string`, `char`, `list<T>`, `option<T>`, `result<T,E>` sowie die
//! zusammengesetzten Typen `record`, `variant`, `enum`, `flags` und `tuple`. Nicht abgebildet
//! bleiben Resources, Futures und Streams — sie meldet die Discovery als nicht unterstützt, statt
//! sie im Katalog anzubieten.

use anyhow::{Context, Result, bail};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use serde::{Deserialize, Serialize};
use serde_json::Value as Json;
use wasmtime::component::{Type, Val};

/// Beschreibung eines Component-Model-Typs für den IPC-Vertrag. Bewusst ein Baum und kein Name:
/// Aus `"list"` ließe sich kein Schema bauen, aus `list<u8>` gegen `list<string>` sehr wohl.
#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "kebab-case")]
pub enum TypeDescriptor {
    Bool,
    S8,
    U8,
    S16,
    U16,
    S32,
    U32,
    /// 64-Bit-Ganzzahl; über die Leitung ein Dezimalstring (Präzision).
    S64,
    /// 64-Bit-Ganzzahl ohne Vorzeichen; über die Leitung ein Dezimalstring.
    U64,
    F32,
    F64,
    Char,
    String,
    /// `list<u8>` — Base64-String, längenbegrenzt.
    Binary,
    List {
        element: Box<TypeDescriptor>,
    },
    Option {
        value: Box<TypeDescriptor>,
    },
    Result {
        #[serde(skip_serializing_if = "Option::is_none")]
        ok: Option<Box<TypeDescriptor>>,
        #[serde(skip_serializing_if = "Option::is_none")]
        err: Option<Box<TypeDescriptor>>,
    },
    /// `record` — JSON-Objekt mit benannten Feldern, alle Pflicht (WIT-Records haben keine
    /// optionalen Felder; ein `option<T>`-Feld ist vorhanden und darf `null` sein).
    Record {
        fields: Vec<FieldDescriptor>,
    },
    /// `variant` — Objekt mit **genau einem** Schlüssel: dem Fallnamen. Dieselbe Form wie
    /// `result`, das im Component Model nichts anderes als ein Variant mit zwei festen Fällen ist.
    Variant {
        cases: Vec<CaseDescriptor>,
    },
    /// `enum` — schlicht der Fallname als String.
    Enum {
        cases: Vec<String>,
    },
    /// `flags` — Liste der gesetzten Namen.
    Flags {
        names: Vec<String>,
    },
    /// `tuple` — JSON-Array fester Länge.
    Tuple {
        items: Vec<TypeDescriptor>,
    },
    /// Ein Typ, den dieser Host nicht abbildet. Er steht im Vertrag, damit die Discovery sagen
    /// kann, **warum** ein Export nicht aufrufbar ist.
    Unsupported {
        detail: String,
    },
}

/// Ein Record-Feld: Name und Typ.
#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub struct FieldDescriptor {
    pub name: String,
    #[serde(rename = "type")]
    pub type_descriptor: TypeDescriptor,
}

/// Ein Variant-Fall. `type` fehlt, wenn der Fall keine Nutzlast trägt.
#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
pub struct CaseDescriptor {
    pub name: String,
    #[serde(rename = "type", skip_serializing_if = "Option::is_none")]
    pub type_descriptor: Option<TypeDescriptor>,
}

impl TypeDescriptor {
    /// True, wenn der Typ vollständig abbildbar ist — rekursiv, ein `list<record>` ist es nicht.
    pub fn is_supported(&self) -> bool {
        match self {
            Self::Unsupported { .. } => false,
            Self::List { element } => element.is_supported(),
            Self::Option { value } => value.is_supported(),
            Self::Result { ok, err } => {
                ok.as_ref().is_none_or(|ty| ty.is_supported())
                    && err.as_ref().is_none_or(|ty| ty.is_supported())
            }
            Self::Record { fields } => fields
                .iter()
                .all(|field| field.type_descriptor.is_supported()),
            Self::Variant { cases } => cases.iter().all(|case| {
                case.type_descriptor
                    .as_ref()
                    .is_none_or(|ty| ty.is_supported())
            }),
            Self::Tuple { items } => items.iter().all(|item| item.is_supported()),
            _ => true,
        }
    }

    /// Kurzform für Meldungen, z. B. `result<string, u32>`.
    pub fn label(&self) -> String {
        match self {
            Self::Bool => "bool".to_owned(),
            Self::S8 => "s8".to_owned(),
            Self::U8 => "u8".to_owned(),
            Self::S16 => "s16".to_owned(),
            Self::U16 => "u16".to_owned(),
            Self::S32 => "s32".to_owned(),
            Self::U32 => "u32".to_owned(),
            Self::S64 => "s64".to_owned(),
            Self::U64 => "u64".to_owned(),
            Self::F32 => "f32".to_owned(),
            Self::F64 => "f64".to_owned(),
            Self::Char => "char".to_owned(),
            Self::String => "string".to_owned(),
            Self::Binary => "list<u8>".to_owned(),
            Self::List { element } => format!("list<{}>", element.label()),
            Self::Option { value } => format!("option<{}>", value.label()),
            Self::Result { ok, err } => match (ok, err) {
                (Some(ok), Some(err)) => format!("result<{}, {}>", ok.label(), err.label()),
                (Some(ok), None) => format!("result<{}>", ok.label()),
                (None, Some(err)) => format!("result<_, {}>", err.label()),
                (None, None) => "result".to_owned(),
            },
            Self::Record { fields } => format!(
                "record{{{}}}",
                fields
                    .iter()
                    .map(|field| format!("{}: {}", field.name, field.type_descriptor.label()))
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            Self::Variant { cases } => format!(
                "variant{{{}}}",
                cases
                    .iter()
                    .map(|case| match &case.type_descriptor {
                        Some(ty) => format!("{}({})", case.name, ty.label()),
                        None => case.name.clone(),
                    })
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            Self::Enum { cases } => format!("enum{{{}}}", cases.join(", ")),
            Self::Flags { names } => format!("flags{{{}}}", names.join(", ")),
            Self::Tuple { items } => format!(
                "tuple<{}>",
                items
                    .iter()
                    .map(TypeDescriptor::label)
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            Self::Unsupported { detail } => detail.clone(),
        }
    }
}

/// Übersetzt einen Component-Model-Typ in die Vertragsform.
pub fn describe(ty: &Type) -> TypeDescriptor {
    match ty {
        Type::Bool => TypeDescriptor::Bool,
        Type::S8 => TypeDescriptor::S8,
        Type::U8 => TypeDescriptor::U8,
        Type::S16 => TypeDescriptor::S16,
        Type::U16 => TypeDescriptor::U16,
        Type::S32 => TypeDescriptor::S32,
        Type::U32 => TypeDescriptor::U32,
        Type::S64 => TypeDescriptor::S64,
        Type::U64 => TypeDescriptor::U64,
        Type::Float32 => TypeDescriptor::F32,
        Type::Float64 => TypeDescriptor::F64,
        Type::Char => TypeDescriptor::Char,
        Type::String => TypeDescriptor::String,
        // Der Sonderfall, der die ADR-Zusage einlöst: Bytes sind ein Blob, keine Zahlenliste.
        Type::List(list) if matches!(list.ty(), Type::U8) => TypeDescriptor::Binary,
        Type::List(list) => TypeDescriptor::List {
            element: Box::new(describe(&list.ty())),
        },
        Type::Option(option) => TypeDescriptor::Option {
            value: Box::new(describe(&option.ty())),
        },
        Type::Result(result) => TypeDescriptor::Result {
            ok: result.ok().map(|ty| Box::new(describe(&ty))),
            err: result.err().map(|ty| Box::new(describe(&ty))),
        },
        Type::Record(record) => TypeDescriptor::Record {
            fields: record
                .fields()
                .map(|field| FieldDescriptor {
                    name: field.name.to_owned(),
                    type_descriptor: describe(&field.ty),
                })
                .collect(),
        },
        Type::Variant(variant) => TypeDescriptor::Variant {
            cases: variant
                .cases()
                .map(|case| CaseDescriptor {
                    name: case.name.to_owned(),
                    type_descriptor: case.ty.as_ref().map(describe),
                })
                .collect(),
        },
        Type::Enum(enumeration) => TypeDescriptor::Enum {
            cases: enumeration.names().map(str::to_owned).collect(),
        },
        Type::Flags(flags) => TypeDescriptor::Flags {
            names: flags.names().map(str::to_owned).collect(),
        },
        Type::Tuple(tuple) => TypeDescriptor::Tuple {
            items: tuple.types().map(|ty| describe(&ty)).collect(),
        },
        Type::Map(_) => unsupported("map"),
        Type::Own(_) | Type::Borrow(_) => unsupported("resource"),
        Type::Future(_) => unsupported("future"),
        Type::Stream(_) => unsupported("stream"),
        _ => unsupported("unbekannt"),
    }
}

fn unsupported(detail: &str) -> TypeDescriptor {
    TypeDescriptor::Unsupported {
        detail: detail.to_owned(),
    }
}

/// JSON → Component-Wert, geführt vom deklarierten Typ. `max_binary_bytes` begrenzt jeden
/// einzelnen `list<u8>`-Wert; ein Guest soll über ein Argument keinen beliebig großen Puffer
/// im Host erzwingen können.
pub fn to_val(ty: &TypeDescriptor, json: &Json, max_binary_bytes: usize) -> Result<Val> {
    match ty {
        TypeDescriptor::Bool => Ok(Val::Bool(
            json.as_bool().context("erwartet wurde true oder false")?,
        )),
        TypeDescriptor::S8 => Ok(Val::S8(
            integer(json, i64::from(i8::MIN), i64::from(i8::MAX))? as i8,
        )),
        TypeDescriptor::U8 => Ok(Val::U8(integer(json, 0, i64::from(u8::MAX))? as u8)),
        TypeDescriptor::S16 => Ok(Val::S16(
            integer(json, i64::from(i16::MIN), i64::from(i16::MAX))? as i16,
        )),
        TypeDescriptor::U16 => Ok(Val::U16(integer(json, 0, i64::from(u16::MAX))? as u16)),
        TypeDescriptor::S32 => Ok(Val::S32(
            integer(json, i64::from(i32::MIN), i64::from(i32::MAX))? as i32,
        )),
        TypeDescriptor::U32 => Ok(Val::U32(integer(json, 0, i64::from(u32::MAX))? as u32)),
        TypeDescriptor::S64 => Ok(Val::S64(wide_integer(json)?)),
        TypeDescriptor::U64 => {
            let value = wide_string(json)?;
            Ok(Val::U64(value.parse().with_context(|| {
                format!("'{value}' ist keine vorzeichenlose 64-Bit-Ganzzahl")
            })?))
        }
        TypeDescriptor::F32 => Ok(Val::Float32(number(json)? as f32)),
        TypeDescriptor::F64 => Ok(Val::Float64(number(json)?)),
        TypeDescriptor::Char => {
            let text = json
                .as_str()
                .context("erwartet wurde ein Zeichen als String")?;
            let mut chars = text.chars();
            match (chars.next(), chars.next()) {
                (Some(single), None) => Ok(Val::Char(single)),
                _ => bail!("erwartet wurde genau ein Zeichen, bekam '{text}'"),
            }
        }
        TypeDescriptor::String => Ok(Val::String(
            json.as_str()
                .context("erwartet wurde ein String")?
                .to_owned(),
        )),
        TypeDescriptor::Binary => {
            let encoded = json
                .as_str()
                .context("erwartet wurde Base64 für list<u8>")?;
            let bytes = BASE64
                .decode(encoded)
                .context("list<u8> erwartet gültiges Base64")?;
            if bytes.len() > max_binary_bytes {
                bail!(
                    "list<u8> mit {} Byte überschreitet das Limit von {max_binary_bytes}",
                    bytes.len()
                );
            }
            Ok(Val::List(bytes.into_iter().map(Val::U8).collect()))
        }
        TypeDescriptor::List { element } => {
            let items = json.as_array().context("erwartet wurde eine Liste")?;
            items
                .iter()
                .map(|item| to_val(element, item, max_binary_bytes))
                .collect::<Result<Vec<_>>>()
                .map(Val::List)
        }
        TypeDescriptor::Option { value } => match json {
            Json::Null => Ok(Val::Option(None)),
            other => Ok(Val::Option(Some(Box::new(to_val(
                value,
                other,
                max_binary_bytes,
            )?)))),
        },
        TypeDescriptor::Result { ok, err } => {
            let object = json
                .as_object()
                .context("result erwartet ein Objekt mit 'ok' oder 'err'")?;
            match (object.get("ok"), object.get("err")) {
                (Some(_), Some(_)) => bail!("result darf nicht 'ok' UND 'err' tragen"),
                (Some(payload), None) => Ok(Val::Result(Ok(payload_val(
                    ok.as_deref(),
                    payload,
                    max_binary_bytes,
                )?))),
                (None, Some(payload)) => Ok(Val::Result(Err(payload_val(
                    err.as_deref(),
                    payload,
                    max_binary_bytes,
                )?))),
                (None, None) => bail!("result erwartet 'ok' oder 'err'"),
            }
        }
        TypeDescriptor::Record { fields } => {
            let object = json.as_object().context("record erwartet ein Objekt")?;
            // Reihenfolge nach Deklaration, nicht nach JSON — die Feldreihenfolge gehört zum Typ.
            let values = fields
                .iter()
                .map(|field| {
                    let value = object
                        .get(&field.name)
                        .with_context(|| format!("Feld '{}' fehlt", field.name))?;
                    Ok((
                        field.name.clone(),
                        to_val(&field.type_descriptor, value, max_binary_bytes)
                            .with_context(|| format!("Feld '{}'", field.name))?,
                    ))
                })
                .collect::<Result<Vec<_>>>()?;

            if let Some(extra) = object
                .keys()
                .find(|key| !fields.iter().any(|field| &field.name == *key))
            {
                bail!("record kennt kein Feld '{extra}'");
            }
            Ok(Val::Record(values))
        }
        TypeDescriptor::Variant { cases } => {
            let object = json
                .as_object()
                .context("variant erwartet ein Objekt mit genau einem Fallnamen")?;
            if object.len() != 1 {
                bail!("variant erwartet genau einen Fall, bekam {}", object.len());
            }
            let (name, payload) = object.iter().next().expect("genau einer");
            let case = cases
                .iter()
                .find(|case| &case.name == name)
                .with_context(|| format!("variant kennt keinen Fall '{name}'"))?;
            Ok(Val::Variant(
                name.clone(),
                payload_val(case.type_descriptor.as_ref(), payload, max_binary_bytes)?,
            ))
        }
        TypeDescriptor::Enum { cases } => {
            let name = json
                .as_str()
                .context("enum erwartet den Fallnamen als String")?;
            if !cases.iter().any(|case| case == name) {
                bail!("enum kennt keinen Fall '{name}'");
            }
            Ok(Val::Enum(name.to_owned()))
        }
        TypeDescriptor::Flags { names } => {
            let items = json
                .as_array()
                .context("flags erwartet eine Liste von Namen")?;
            let set = items
                .iter()
                .map(|item| {
                    let name = item.as_str().context("flags erwartet Strings")?;
                    if !names.iter().any(|known| known == name) {
                        bail!("flags kennt kein Flag '{name}'");
                    }
                    Ok(name.to_owned())
                })
                .collect::<Result<Vec<_>>>()?;
            Ok(Val::Flags(set))
        }
        TypeDescriptor::Tuple { items } => {
            let values = json.as_array().context("tuple erwartet eine Liste")?;
            if values.len() != items.len() {
                bail!(
                    "tuple erwartet {} Element(e), bekam {}",
                    items.len(),
                    values.len()
                );
            }
            items
                .iter()
                .zip(values)
                .map(|(ty, value)| to_val(ty, value, max_binary_bytes))
                .collect::<Result<Vec<_>>>()
                .map(Val::Tuple)
        }
        TypeDescriptor::Unsupported { detail } => {
            bail!("Typ '{detail}' wird von diesem Host nicht abgebildet")
        }
    }
}

/// Component-Wert → JSON. Die Umkehrung von [`to_val`]; `list<u8>` wird wieder Base64.
pub fn to_json(value: &Val) -> Result<Json> {
    Ok(match value {
        Val::Bool(inner) => Json::Bool(*inner),
        Val::S8(inner) => Json::from(*inner),
        Val::U8(inner) => Json::from(*inner),
        Val::S16(inner) => Json::from(*inner),
        Val::U16(inner) => Json::from(*inner),
        Val::S32(inner) => Json::from(*inner),
        Val::U32(inner) => Json::from(*inner),
        // Als String zurück, aus demselben Grund wie beim Hinweg.
        Val::S64(inner) => Json::String(inner.to_string()),
        Val::U64(inner) => Json::String(inner.to_string()),
        Val::Float32(inner) => finite(f64::from(*inner))?,
        Val::Float64(inner) => finite(*inner)?,
        Val::Char(inner) => Json::String(inner.to_string()),
        Val::String(inner) => Json::String(inner.clone()),
        Val::List(items) => {
            if items.iter().all(|item| matches!(item, Val::U8(_))) && !items.is_empty() {
                let bytes: Vec<u8> = items
                    .iter()
                    .map(|item| match item {
                        Val::U8(byte) => *byte,
                        _ => unreachable!("gerade geprüft"),
                    })
                    .collect();
                Json::String(BASE64.encode(bytes))
            } else {
                Json::Array(items.iter().map(to_json).collect::<Result<Vec<_>>>()?)
            }
        }
        Val::Option(inner) => match inner {
            Some(value) => to_json(value)?,
            None => Json::Null,
        },
        Val::Result(inner) => {
            let (key, payload) = match inner {
                Ok(payload) => ("ok", payload),
                Err(payload) => ("err", payload),
            };
            let mut object = serde_json::Map::new();
            object.insert(
                key.to_owned(),
                match payload {
                    Some(value) => to_json(value)?,
                    None => Json::Null,
                },
            );
            Json::Object(object)
        }
        Val::Record(fields) => {
            let mut object = serde_json::Map::with_capacity(fields.len());
            for (name, value) in fields {
                object.insert(name.clone(), to_json(value)?);
            }
            Json::Object(object)
        }
        // Ein Variant ist ein Objekt mit genau einem Schlüssel — dieselbe Form wie `result`,
        // das im Component Model nichts anderes ist.
        Val::Variant(name, payload) => {
            let mut object = serde_json::Map::with_capacity(1);
            object.insert(
                name.clone(),
                match payload {
                    Some(value) => to_json(value)?,
                    None => Json::Null,
                },
            );
            Json::Object(object)
        }
        Val::Enum(name) => Json::String(name.clone()),
        Val::Flags(names) => Json::Array(
            names
                .iter()
                .map(|name| Json::String(name.clone()))
                .collect(),
        ),
        Val::Tuple(items) => Json::Array(items.iter().map(to_json).collect::<Result<Vec<_>>>()?),
        other => bail!("Rückgabewert {other:?} lässt sich nicht als JSON darstellen"),
    })
}

fn payload_val(
    ty: Option<&TypeDescriptor>,
    json: &Json,
    max_binary_bytes: usize,
) -> Result<Option<Box<Val>>> {
    match ty {
        // `result<_, E>`: Der ok-Zweig trägt keinen Wert, also darf auch keiner kommen.
        None if json.is_null() => Ok(None),
        None => bail!("dieser result-Zweig trägt keinen Wert, bekam aber einen"),
        Some(ty) => Ok(Some(Box::new(to_val(ty, json, max_binary_bytes)?))),
    }
}

fn integer(json: &Json, minimum: i64, maximum: i64) -> Result<i64> {
    let value = json.as_i64().context("erwartet wurde eine ganze Zahl")?;
    if value < minimum || value > maximum {
        bail!("{value} liegt außerhalb von {minimum}..={maximum}");
    }
    Ok(value)
}

fn wide_integer(json: &Json) -> Result<i64> {
    let text = wide_string(json)?;
    text.parse()
        .with_context(|| format!("'{text}' ist keine 64-Bit-Ganzzahl"))
}

/// 64-Bit-Werte kommen als String; eine Zahl wird akzeptiert, solange sie verlustfrei ist.
fn wide_string(json: &Json) -> Result<String> {
    match json {
        Json::String(text) => Ok(text.clone()),
        Json::Number(number) => Ok(number.to_string()),
        other => bail!("erwartet wurde eine 64-Bit-Zahl als String, bekam {other}"),
    }
}

fn number(json: &Json) -> Result<f64> {
    json.as_f64().context("erwartet wurde eine Zahl")
}

/// NaN und Unendlich haben in JSON keine Entsprechung — lieber ein klarer Fehler als `null`.
fn finite(value: f64) -> Result<Json> {
    serde_json::Number::from_f64(value)
        .map(Json::Number)
        .with_context(|| format!("{value} ist in JSON nicht darstellbar"))
}

#[cfg(test)]
mod tests {
    use super::*;

    const LIMIT: usize = 1024;

    fn roundtrip(ty: &TypeDescriptor, json: Json) {
        let value = to_val(ty, &json, LIMIT).expect("hin");
        let back = to_json(&value).expect("zurück");
        assert_eq!(back, json, "Rundlauf für {}", ty.label());
    }

    #[test]
    fn primitives_round_trip() {
        roundtrip(&TypeDescriptor::Bool, Json::Bool(true));
        roundtrip(&TypeDescriptor::S32, Json::from(-42));
        roundtrip(&TypeDescriptor::U32, Json::from(7u32));
        roundtrip(&TypeDescriptor::F64, Json::from(1.5));
        roundtrip(&TypeDescriptor::String, Json::String("hallo".to_owned()));
        roundtrip(&TypeDescriptor::Char, Json::String("ß".to_owned()));
    }

    /// Der Grund für die String-Form: Diese Zahl überlebt einen JSON-Double nicht.
    #[test]
    fn wide_integers_survive_as_strings() {
        let huge = u64::MAX.to_string();
        let value = to_val(&TypeDescriptor::U64, &Json::String(huge.clone()), LIMIT).unwrap();

        assert_eq!(value, Val::U64(u64::MAX));
        assert_eq!(to_json(&value).unwrap(), Json::String(huge));
    }

    /// list<u8> ist ein Blob, kein Zahlen-Array — die ADR-Zusage in einer Zeile.
    #[test]
    fn binary_is_base64_not_an_array_of_numbers() {
        let encoded = BASE64.encode([0u8, 1, 2, 253, 254, 255]);

        let value = to_val(
            &TypeDescriptor::Binary,
            &Json::String(encoded.clone()),
            LIMIT,
        )
        .unwrap();

        assert_eq!(
            value,
            Val::List(vec![
                Val::U8(0),
                Val::U8(1),
                Val::U8(2),
                Val::U8(253),
                Val::U8(254),
                Val::U8(255),
            ])
        );
        assert_eq!(to_json(&value).unwrap(), Json::String(encoded));
    }

    /// „Begrenzte Binärdaten" heißt begrenzt: Ein zu großes Argument wird abgewiesen, nicht
    /// stillschweigend gekürzt.
    #[test]
    fn oversized_binary_is_rejected() {
        let encoded = BASE64.encode(vec![0u8; LIMIT + 1]);

        let failure = to_val(&TypeDescriptor::Binary, &Json::String(encoded), LIMIT).unwrap_err();

        assert!(failure.to_string().contains("überschreitet"));
    }

    #[test]
    fn results_carry_ok_or_err_but_never_both() {
        let ty = TypeDescriptor::Result {
            ok: Some(Box::new(TypeDescriptor::String)),
            err: Some(Box::new(TypeDescriptor::U32)),
        };

        roundtrip(&ty, serde_json::json!({"ok": "fertig"}));
        roundtrip(&ty, serde_json::json!({"err": 404}));
        assert!(to_val(&ty, &serde_json::json!({"ok": "a", "err": 1}), LIMIT).is_err());
        assert!(to_val(&ty, &serde_json::json!({}), LIMIT).is_err());
    }

    #[test]
    fn a_result_branch_without_payload_takes_null() {
        let ty = TypeDescriptor::Result {
            ok: None,
            err: Some(Box::new(TypeDescriptor::String)),
        };

        roundtrip(&ty, serde_json::json!({"ok": null}));
        assert!(to_val(&ty, &serde_json::json!({"ok": "unerwartet"}), LIMIT).is_err());
    }

    #[test]
    fn options_map_to_null() {
        let ty = TypeDescriptor::Option {
            value: Box::new(TypeDescriptor::S32),
        };

        roundtrip(&ty, Json::Null);
        roundtrip(&ty, Json::from(5));
    }

    #[test]
    fn lists_of_other_types_stay_arrays() {
        let ty = TypeDescriptor::List {
            element: Box::new(TypeDescriptor::String),
        };

        roundtrip(&ty, serde_json::json!(["a", "b"]));
    }

    #[test]
    fn out_of_range_integers_are_rejected() {
        assert!(to_val(&TypeDescriptor::U8, &Json::from(256), LIMIT).is_err());
        assert!(to_val(&TypeDescriptor::S8, &Json::from(-129), LIMIT).is_err());
        assert!(to_val(&TypeDescriptor::U32, &Json::from(-1), LIMIT).is_err());
    }

    fn record_type() -> TypeDescriptor {
        TypeDescriptor::Record {
            fields: vec![
                FieldDescriptor {
                    name: "x".to_owned(),
                    type_descriptor: TypeDescriptor::S32,
                },
                FieldDescriptor {
                    name: "label".to_owned(),
                    type_descriptor: TypeDescriptor::String,
                },
            ],
        }
    }

    #[test]
    fn records_map_to_objects_with_all_fields() {
        roundtrip(&record_type(), serde_json::json!({"x": -1, "label": "a"}));

        // Fehlendes Feld und unbekanntes Feld sind beides Fehler — ein Record hat genau die
        // Felder seines Typs, nicht mehr und nicht weniger.
        assert!(to_val(&record_type(), &serde_json::json!({"x": 1}), LIMIT).is_err());
        assert!(
            to_val(
                &record_type(),
                &serde_json::json!({"x": 1, "label": "a", "extra": 2}),
                LIMIT
            )
            .is_err()
        );
    }

    #[test]
    fn variants_carry_exactly_one_case() {
        let ty = TypeDescriptor::Variant {
            cases: vec![
                CaseDescriptor {
                    name: "leer".to_owned(),
                    type_descriptor: None,
                },
                CaseDescriptor {
                    name: "zahl".to_owned(),
                    type_descriptor: Some(TypeDescriptor::S32),
                },
            ],
        };

        roundtrip(&ty, serde_json::json!({"zahl": 42}));
        roundtrip(&ty, serde_json::json!({"leer": null}));
        assert!(to_val(&ty, &serde_json::json!({"zahl": 1, "leer": null}), LIMIT).is_err());
        assert!(to_val(&ty, &serde_json::json!({"unbekannt": 1}), LIMIT).is_err());
        // Ein Fall ohne Nutzlast nimmt auch keine an.
        assert!(to_val(&ty, &serde_json::json!({"leer": 5}), LIMIT).is_err());
    }

    #[test]
    fn enums_and_flags_use_their_declared_names() {
        let colour = TypeDescriptor::Enum {
            cases: vec!["rot".to_owned(), "gruen".to_owned()],
        };
        let options = TypeDescriptor::Flags {
            names: vec!["a".to_owned(), "b".to_owned()],
        };

        roundtrip(&colour, Json::String("rot".to_owned()));
        roundtrip(&options, serde_json::json!(["a", "b"]));
        roundtrip(&options, serde_json::json!([]));
        assert!(to_val(&colour, &Json::String("blau".to_owned()), LIMIT).is_err());
        assert!(to_val(&options, &serde_json::json!(["c"]), LIMIT).is_err());
    }

    #[test]
    fn tuples_keep_their_length() {
        let ty = TypeDescriptor::Tuple {
            items: vec![TypeDescriptor::S32, TypeDescriptor::String],
        };

        roundtrip(&ty, serde_json::json!([1, "a"]));
        assert!(to_val(&ty, &serde_json::json!([1]), LIMIT).is_err());
        assert!(to_val(&ty, &serde_json::json!([1, "a", 2]), LIMIT).is_err());
    }

    /// Verschachtelung ist der eigentliche Nutzen: ein Record mit Liste von Varianten.
    #[test]
    fn composites_nest() {
        let ty = TypeDescriptor::Record {
            fields: vec![FieldDescriptor {
                name: "eintraege".to_owned(),
                type_descriptor: TypeDescriptor::List {
                    element: Box::new(TypeDescriptor::Variant {
                        cases: vec![CaseDescriptor {
                            name: "text".to_owned(),
                            type_descriptor: Some(TypeDescriptor::String),
                        }],
                    }),
                },
            }],
        };

        assert!(ty.is_supported());
        roundtrip(
            &ty,
            serde_json::json!({"eintraege": [{"text": "a"}, {"text": "b"}]}),
        );
    }

    #[test]
    fn unsupported_types_are_visible_and_refused() {
        let ty = TypeDescriptor::Unsupported {
            detail: "record".to_owned(),
        };

        assert!(!ty.is_supported());
        assert!(to_val(&ty, &Json::Null, LIMIT).is_err());
        // Auch verschachtelt: eine Liste von Records ist nicht abbildbar.
        assert!(
            !TypeDescriptor::List {
                element: Box::new(ty),
            }
            .is_supported()
        );
    }
}
