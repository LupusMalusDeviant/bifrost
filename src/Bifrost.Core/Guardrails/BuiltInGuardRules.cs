using Bifrost.Abstractions;

namespace Bifrost.Core.Guardrails;

/// <summary>
/// Kuratierter Startregelsatz (ADR-0011). Abgeleitet aus gitleaks (MIT) — bewusst eine kleine
/// Auswahl statt aller 222 Regeln: Jede kompilierte NonBacktracking-Regel kostet rund 530 KB
/// Speicher, und Präzision zählt hier mehr als Vollständigkeit, weil ein Fehlalarm einen
/// Arbeitsschritt abbricht statt nur eine Logzeile zu erzeugen.
///
/// Aufgenommen sind nur Muster mit eindeutigem Anker (Präfix oder Rahmen). Bewusst NICHT
/// aufgenommen:
/// <list type="bullet">
/// <item>AWS Secret Access Key — 40 Zeichen Base64 ohne Struktur; gitleaks hat aus gutem Grund
/// keine eigenständige Regel dafür.</item>
/// <item>Stripe <c>pk_live_</c> — das ist der öffentliche Schlüssel und per Definition kein Secret.</item>
/// <item>Generische 32-Hex-Muster — nicht von Hashes, UUIDs und ETags unterscheidbar.</item>
/// </list>
/// </summary>
public static class BuiltInGuardRules
{
    /// <summary>
    /// Der Auslieferungszustand. Alle Regeln blocken ab Werk (ADR-0011, E1) — bis auf JWT,
    /// siehe Begründung dort.
    /// </summary>
    public static IReadOnlyList<GuardRule> All { get; } =
    [
        new("aws-access-key", "AWS Access Key ID",
            @"\b((?:A3T[A-Z0-9]|AKIA|ASIA|ABIA|ACCA)[A-Z2-7]{16})\b",
            Keyword: null, GuardDirection.Both, GuardMode.Block),

        new("github-token", "GitHub Token (PAT, OAuth, App, Refresh)",
            @"\bgh[pousr]_[0-9a-zA-Z]{36}\b",
            "gh", GuardDirection.Both, GuardMode.Block),

        new("github-fine-grained", "GitHub Fine-Grained PAT",
            @"\bgithub_pat_\w{82}\b",
            "github_pat_", GuardDirection.Both, GuardMode.Block),

        new("gitlab-token", "GitLab Personal Access Token",
            @"\bglpat-[\w-]{20}\b",
            "glpat-", GuardDirection.Both, GuardMode.Block),

        // Der stabile Anker ist das eingebettete "T3BlbkFJ" (Base64 von "OpenAI") — das ältere
        // Muster sk-[A-Za-z0-9]{48} ist veraltet und ein zuverlässiger Fehlalarm-Erzeuger.
        new("openai-key", "OpenAI API-Key",
            @"\bsk-[A-Za-z0-9_-]*T3BlbkFJ[A-Za-z0-9_-]{20,}\b",
            "T3BlbkFJ", GuardDirection.Both, GuardMode.Block),

        new("anthropic-key", "Anthropic API-Key",
            @"\bsk-ant-api\d{2}-[a-zA-Z0-9_\-]{93}AA\b",
            "sk-ant-", GuardDirection.Both, GuardMode.Block),

        new("slack-bot-token", "Slack Bot/User Token",
            @"\bxox[bpe]-[0-9]{10,13}-[0-9]{10,13}[a-zA-Z0-9-]*\b",
            "xox", GuardDirection.Both, GuardMode.Block),

        new("slack-webhook", "Slack Webhook-URL",
            @"hooks\.slack\.com/(?:services|workflows|triggers)/[A-Za-z0-9+/]{43,56}",
            "hooks.slack.com", GuardDirection.Both, GuardMode.Block),

        // Kein Vorfilter: Die Varianten test/live/prod teilen keine gemeinsame Zeichenfolge,
        // die sich als einzelnes Ordinal-Keyword eignet.
        new("stripe-secret-key", "Stripe Secret/Restricted Key",
            @"\b(?:sk|rk)_(?:test|live|prod)_[a-zA-Z0-9]{10,99}\b",
            Keyword: null, GuardDirection.Both, GuardMode.Block),

        new("google-api-key", "Google API-Key",
            @"\bAIza[\w-]{35}\b",
            "AIza", GuardDirection.Both, GuardMode.Block),

        new("npm-token", "npm Access Token",
            @"\bnpm_[a-zA-Z0-9]{36}\b",
            "npm_", GuardDirection.Both, GuardMode.Block),

        new("sendgrid-key", "SendGrid API-Key",
            @"\bSG\.[a-zA-Z0-9=_\-\.]{66}\b",
            "SG.", GuardDirection.Both, GuardMode.Block),

        new("huggingface-token", "HuggingFace Token",
            @"\bhf_[a-zA-Z]{34}\b",
            "hf_", GuardDirection.Both, GuardMode.Block),

        // Anfang UND Ende UND ausreichend Rumpf verlangen: Das oft kopierte
        // "-----BEGIN.*PRIVATE KEY-----" trifft jede Erwähnung in einer Dokumentation.
        new("private-key", "Privater Schlüssel (PEM)",
            @"-----BEGIN[ A-Za-z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----[\s\S]{64,}?-----END",
            "PRIVATE KEY", GuardDirection.Both, GuardMode.Block),

        // Beobachten statt blockieren: Header und Payload eines JWT sind nur Base64, nicht
        // verschlüsselt. Öffentliche ID-Tokens, abgelaufene Test-Fixtures und Doku-Beispiele
        // matchen identisch. Diese eine Regel auf Block zu stellen wäre der schnellste Weg,
        // dass jemand die ganze Guardrail abschaltet.
        new("jwt", "JSON Web Token (unzuverlässig — siehe Beschreibung)",
            @"\bey[A-Za-z0-9_-]{17,}\.ey[A-Za-z0-9_/\\-]{17,}\.[A-Za-z0-9_/\\-]{10,}={0,2}",
            "ey", GuardDirection.Both, GuardMode.Observe),
    ];
}
