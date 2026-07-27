using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// EF-Core-Modell (ADR-0007). Provider-neutral gehalten: JSON-Blobs für Listen,
/// DateTimeOffset immer UTC (Npgsql-timestamptz-Kompatibilität). Die Schema-Erzeugung läuft über
/// EF-Migrationen je Provider; <see cref="DatabaseInitializer"/> stempelt dabei v1.0-Datenbanken,
/// die noch aus der EnsureCreated-Zeit stammen, auf die Baseline.
/// </summary>
public sealed class McpMcpDbContext : DbContext
{
    public McpMcpDbContext(DbContextOptions<McpMcpDbContext> options)
        : base(options)
    {
    }

    public DbSet<ConfigVersionRow> ConfigVersions => Set<ConfigVersionRow>();

    public DbSet<IdentityRow> Identities => Set<IdentityRow>();

    public DbSet<RoleRow> Roles => Set<RoleRow>();

    public DbSet<ProfileRow> Profiles => Set<ProfileRow>();

    public DbSet<ApiKeyRow> ApiKeys => Set<ApiKeyRow>();

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    public DbSet<UiUserRow> UiUsers => Set<UiUserRow>();

    public DbSet<AssetRow> Assets => Set<AssetRow>();

    public DbSet<ToolDescriptionOverrideRow> ToolDescriptionOverrides => Set<ToolDescriptionOverrideRow>();

    public DbSet<RedactionRuleRow> RedactionRules => Set<RedactionRuleRow>();

    public DbSet<GuardRuleRow> GuardRules => Set<GuardRuleRow>();

    public DbSet<ApprovalRequestRow> ApprovalRequests => Set<ApprovalRequestRow>();

    public DbSet<ApprovalToolRow> ApprovalTools => Set<ApprovalToolRow>();

    public DbSet<WebhookRow> Webhooks => Set<WebhookRow>();

    public DbSet<PublisherKeyRow> PublisherKeys => Set<PublisherKeyRow>();

    public DbSet<TaskRow> Tasks => Set<TaskRow>();

    public DbSet<ConnectorPackageRow> ConnectorPackages => Set<ConnectorPackageRow>();

    public DbSet<ToolDefinitionPinRow> ToolDefinitionPins => Set<ToolDefinitionPinRow>();

    public DbSet<UpstreamOAuthTokenRow> UpstreamOAuthTokens => Set<UpstreamOAuthTokenRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConfigVersionRow>(e =>
        {
            e.HasKey(r => new { r.ServerId, r.Version });
            e.Property(r => r.Payload).IsRequired();
        });

        modelBuilder.Entity<IdentityRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.RolesJson).IsRequired();
        });

        modelBuilder.Entity<RoleRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.GrantsJson).IsRequired();
        });

        modelBuilder.Entity<ProfileRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.PinnedToolsJson).IsRequired();
        });

        modelBuilder.Entity<ApiKeyRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Label).HasMaxLength(200).IsRequired();
            e.Property(r => r.Hash).HasMaxLength(500).IsRequired();
            e.HasIndex(r => r.IdentityId);
        });

        modelBuilder.Entity<AuditEventRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedOnAdd();
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.CallerId);
            e.HasIndex(r => r.ServerId);
            e.HasIndex(r => r.Tool);
            e.HasIndex(r => r.Status);
        });

        modelBuilder.Entity<UiUserRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Username).HasMaxLength(200).IsRequired();
            e.HasIndex(r => r.Username).IsUnique();
            e.Property(r => r.PasswordHash).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<AssetRow>(e =>
        {
            e.HasKey(r => new { r.Id, r.Version });
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.Content).IsRequired();
        });

        modelBuilder.Entity<ToolDescriptionOverrideRow>(e =>
        {
            e.HasKey(r => r.Tool);
            e.Property(r => r.Tool).HasMaxLength(300);
            e.Property(r => r.Description).IsRequired();
        });

        modelBuilder.Entity<RedactionRuleRow>(e =>
        {
            e.HasKey(r => r.Tool);
            e.Property(r => r.Tool).HasMaxLength(300);
            e.Property(r => r.Patterns).IsRequired();
        });

        modelBuilder.Entity<GuardRuleRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(100);
            e.Property(r => r.Description).IsRequired().HasMaxLength(300);
            e.Property(r => r.Pattern).IsRequired().HasMaxLength(1000);
            e.Property(r => r.Keyword).HasMaxLength(100);
        });

        modelBuilder.Entity<ApprovalRequestRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Tool).IsRequired().HasMaxLength(300);
            e.Property(r => r.Fingerprint).IsRequired().HasMaxLength(64);
            e.Property(r => r.CallerDescription).HasMaxLength(500);
            // Der Consume-Pfad sucht nach genau dieser Kombination — auf dem Hot Path.
            e.HasIndex(r => new { r.CallerId, r.Tool, r.Fingerprint, r.State });
        });

        modelBuilder.Entity<TaskRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Tool).IsRequired().HasMaxLength(300);
            e.Property(r => r.InputFingerprint).IsRequired().HasMaxLength(64);
            e.Property(r => r.OwnerDescription).HasMaxLength(500);
            e.Property(r => r.FailureCode).HasMaxLength(100);
            e.Property(r => r.FailureMessage).HasMaxLength(2000);
            // Derselbe Hot-Path-Index wie bei den Freigaben, die hier aufgehen (ADR-0019): Der
            // Consume-Pfad läuft vor JEDEM Tool-Call und darf durch das allgemeinere Modell nicht
            // langsamer werden.
            e.HasIndex(r => new { r.OwnerId, r.Tool, r.InputFingerprint, r.State });
            // Der Verfallslauf sucht nach fälligen, nicht-terminalen Vorgängen.
            e.HasIndex(r => new { r.State, r.ExpiresAtTicks });
            // Die Liste sortiert nach Alter.
            e.HasIndex(r => r.CreatedAtTicks);
        });

        modelBuilder.Entity<ApprovalToolRow>(e =>
        {
            e.HasKey(r => r.Tool);
            e.Property(r => r.Tool).HasMaxLength(300);
        });

        modelBuilder.Entity<PublisherKeyRow>(e =>
        {
            // Der SHA-256-Fingerprint ist der Schlüssel: derselbe Public Key kann nicht zweimal
            // mit unterschiedlichem Vertrauensstand dastehen.
            e.HasKey(r => r.KeyId);
            e.Property(r => r.KeyId).HasMaxLength(64);
            e.Property(r => r.PublicKey).IsRequired().HasMaxLength(64);
            e.Property(r => r.Label).IsRequired().HasMaxLength(200);
            // Ausdrücklicher Default, damit die Migration bestehende Zeilen auf ThirdParty setzt.
            // Ohne ihn nähme EF die 0 — und die heißt 'Core', also die höchste Stufe. Ein Update
            // hätte damit jedem bereits gepinnten Schlüssel stillschweigend mehr Rechte gegeben,
            // als er je hatte.
            e.Property(r => r.TrustLevel).HasDefaultValue((int)ConnectorTrustLevel.ThirdParty);
        });

        modelBuilder.Entity<UpstreamOAuthTokenRow>(e =>
        {
            // Ein Token je Upstream. Getrennt von der Konfigurationshistorie, weil sich ein Token
            // laufend erneuert — jede Erneuerung als Konfigurationsversion zu führen wäre Unsinn.
            e.HasKey(r => r.ServerId);
            e.Property(r => r.Payload).IsRequired();
            e.Property(r => r.Issuer).IsRequired().HasMaxLength(500);
        });

        modelBuilder.Entity<ToolDefinitionPinRow>(e =>
        {
            // Ein Pin je Upstream und Tool-Name. Der Name ist der native, nicht der namespaced —
            // ein Slug-Wechsel darf den festgehaltenen Stand nicht verlieren.
            e.HasKey(r => new { r.ServerId, r.Tool });
            e.Property(r => r.Tool).HasMaxLength(300);
            e.Property(r => r.AcceptedHash).IsRequired().HasMaxLength(64);
            e.Property(r => r.PendingHash).HasMaxLength(64);
        });

        modelBuilder.Entity<ConnectorPackageRow>(e =>
        {
            // Paket-Id plus Version: Genau eine Zeile je ausgelieferter Fassung, und ein Rollback
            // findet die vorherige, weil sie noch dasteht.
            e.HasKey(r => new { r.PackageId, r.Version });
            e.Property(r => r.PackageId).HasMaxLength(128);
            e.Property(r => r.Version).HasMaxLength(128);
            e.Property(r => r.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(r => r.PublisherKeyId).IsRequired().HasMaxLength(64);
            e.Property(r => r.ManifestSha256).IsRequired().HasMaxLength(64);
            e.Property(r => r.Directory).IsRequired().HasMaxLength(500);
            e.Property(r => r.GrantedCapabilities).HasMaxLength(4000);
            e.Property(r => r.FailureReason).HasMaxLength(2000);
            // Die Auflösung „Paket → aktive Version" läuft bei jedem Upstream-Start.
            e.HasIndex(r => new { r.PackageId, r.State });
        });

        modelBuilder.Entity<WebhookRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).IsRequired().HasMaxLength(200);
            e.Property(r => r.Tool).IsRequired().HasMaxLength(300);
            // Das HMAC-Secret liegt DataProtection-verschlüsselt (ADR-0013), nicht als Hash —
            // es wird zum Nachrechnen der Signatur gebraucht.
            e.Property(r => r.EncryptedSecret).IsRequired();
        });

        // Provider-neutral: Zeitstempel als UTC-Ticks (bigint). SQLite kann DateTimeOffset weder
        // sortieren noch in ExecuteDelete vergleichen; long funktioniert identisch auf beiden Providern.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(UtcTicksConverter.Instance);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(NullableUtcTicksConverter.Instance);
                }
            }
        }
    }

    private static class UtcTicksConverter
    {
        public static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long> Instance =
            new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
    }

    private static class NullableUtcTicksConverter
    {
        public static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?> Instance =
            new(
                v => v.HasValue ? v.Value.UtcTicks : null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
    }
}

/// <summary>Verschlüsselte Config-Version (FR-10). Payload = DataProtection-verschlüsseltes JSON der kompletten UpstreamServerConfig (NFR-04).</summary>
public sealed class ConfigVersionRow
{
    public Guid ServerId { get; set; }

    public int Version { get; set; }

    public byte[] Payload { get; set; } = [];

    public DateTimeOffset SavedAt { get; set; }
}

public sealed class IdentityRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Kind { get; set; }

    public Guid? ProfileId { get; set; }

    public string RolesJson { get; set; } = "[]";
}

public sealed class RoleRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? RateLimitPerMinute { get; set; }

    public string GrantsJson { get; set; } = "[]";
}

public sealed class ProfileRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool LazyToolsEnabled { get; set; }

    public string PinnedToolsJson { get; set; } = "[]";
}

public sealed class ApiKeyRow
{
    public Guid Id { get; set; }

    public Guid IdentityId { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Format: {iterations}.{saltBase64}.{hashBase64} — niemals der Klartext-Key (NFR-04).</summary>
    public string Hash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AuditEventRow
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public Guid? CallerId { get; set; }

    public int Origin { get; set; }

    public int Kind { get; set; }

    public Guid? ServerId { get; set; }

    public string? Tool { get; set; }

    public int? Status { get; set; }

    public string? RedactedArgumentsJson { get; set; }

    public long? RequestBytes { get; set; }

    public long? ResponseBytes { get; set; }

    public double? DurationMs { get; set; }

    /// <summary>Profil/Rollen des Aufrufers im Klartext (FR-21).</summary>
    public string? CallerRoles { get; set; }

    /// <summary>Klartext bei Systemereignissen, z.B. Upstream-Zustandswechsel (FR-22).</summary>
    public string? Detail { get; set; }

    /// <summary>Maskierter Ergebnis-Payload — nur im Debug-Modus befüllt (FR-24).</summary>
    public string? RedactedResponseJson { get; set; }

    /// <summary>Verbindet Ereignisse derselben Invocation (ADR-0019).</summary>
    public Guid? CorrelationId { get; set; }
}

public sealed class UiUserRow
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>PBKDF2-Hash im Format {iterations}.{salt}.{hash} — nie das Klartext-Passwort (NFR-04).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public int Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Serverseitig überschriebene Tool-Beschreibung (FR-14), Schlüssel ist der namespaced Tool-Name.</summary>
public sealed class ToolDescriptionOverrideRow
{
    public string Tool { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

/// <summary>Zusätzliche Redaction-Muster eines Tools (FR-24), kommasepariert.</summary>
public sealed class RedactionRuleRow
{
    public string Tool { get; set; } = string.Empty;

    public string Patterns { get; set; } = string.Empty;
}

/// <summary>Erkennungsregel der Secret-Guardrail (ADR-0011).</summary>
public sealed class GuardRuleRow
{
    public string Id { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    /// <summary>Vorfilter; null bedeutet, dass der Regex immer ausgeführt wird.</summary>
    public string? Keyword { get; set; }

    public int Direction { get; set; }

    public int Mode { get; set; }

    public bool Enabled { get; set; } = true;

    public bool IsCustom { get; set; }
}

/// <summary>Eine Freigabe-Anfrage in der Queue (FR-32, ADR-0012).</summary>
public sealed class ApprovalRequestRow
{
    public Guid Id { get; set; }

    public Guid CallerId { get; set; }

    public string CallerDescription { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Redigierte Argumente als JSON — nie die rohen.</summary>
    public string? RedactedArgumentsJson { get; set; }

    public int State { get; set; }

    public long RequestedAtTicks { get; set; }

    public long ExpiresAtTicks { get; set; }
}

/// <summary>
/// Ein persistierter Vorgang (ADR-0019, TaskV1). Zeitstempel liegen als UTC-Ticks, weil SQLite
/// <c>DateTimeOffset</c> nicht sortieren kann.
/// </summary>
public sealed class TaskRow
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public string OwnerDescription { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public Guid? ServerId { get; set; }

    public int Origin { get; set; }

    public Guid CorrelationId { get; set; }

    public int State { get; set; }

    public int Revision { get; set; }

    public int? Progress { get; set; }

    public string InputFingerprint { get; set; } = string.Empty;

    /// <summary>Redigierte Eingabe als JSON — nie die rohe.</summary>
    public string? RedactedInputJson { get; set; }

    /// <summary>Redigiertes Ergebnis als JSON.</summary>
    public string? RedactedResultJson { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    public string? ExpectedInputSchemaJson { get; set; }

    public int Cancellation { get; set; }

    /// <summary>
    /// Gesetzt, sobald ein Aufruf diesen Vorgang eingelöst hat. Die Freigabe-Semantik aus ADR-0012
    /// ist <b>einmalig</b>; der Zustandsautomat von ADR-0019 kennt aber keinen Unterschied zwischen
    /// „freigegeben" und „schon eingelöst" — beides ist `working`. Ohne diese Spalte liefe ein
    /// zweiter identischer Call erneut durch, und eine erteilte Zustimmung würde zum Dauerfreifahrtschein.
    /// </summary>
    public long? ClaimedAtTicks { get; set; }

    public long CreatedAtTicks { get; set; }

    public long UpdatedAtTicks { get; set; }

    public long ExpiresAtTicks { get; set; }
}

/// <summary>Markiert ein Tool als freigabepflichtig (FR-32).</summary>
public sealed class ApprovalToolRow
{
    public string Tool { get; set; } = string.Empty;
}

/// <summary>Ein registrierter Webhook (FR-20, ADR-0013).</summary>
public sealed class WebhookRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid CallerId { get; set; }

    public string Tool { get; set; } = string.Empty;

    /// <summary>HMAC-Secret, DataProtection-verschlüsselt.</summary>
    public byte[] EncryptedSecret { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public long CreatedAtTicks { get; set; }
}

/// <summary>
/// Ein gepinnter Publisher-Schlüssel für WASI-Components (Plan 0003, WP4, ADR-0020). Der Public
/// Key ist kein Geheimnis und liegt deshalb im Klartext (Base64) — geschützt werden muss nicht
/// seine Vertraulichkeit, sondern seine Integrität, und die hängt am Schreibzugriff auf die DB.
/// Entzogene Schlüssel bleiben stehen, damit ältere Audit-Zeilen zuordenbar bleiben.
/// </summary>
public sealed class PublisherKeyRow
{
    /// <summary>SHA-256-Fingerprint des Public Keys, hex — dieselbe Id wie im Host-Audit.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Ed25519-Public-Key, Base64 (32 Byte).</summary>
    public string PublicKey { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public long AddedAtTicks { get; set; }

    /// <summary>Gesetzt = entzogen. Null = vertrauenswürdig.</summary>
    public long? RevokedAtTicks { get; set; }

    /// <summary>
    /// Vertrauensstufe nach ADR-0016 als <see cref="ConnectorTrustLevel"/>. Vorgabe 2
    /// (<c>ThirdParty</c>): Ein gepinnter Schlüssel darf laufen, aber nicht alles verlangen — und
    /// bestehende Zeilen aus der Zeit vor den Paketen dürfen durch die Migration nicht
    /// stillschweigend zu „offiziell" werden.
    /// </summary>
    public int TrustLevel { get; set; } = (int)ConnectorTrustLevel.ThirdParty;
}

/// <summary>
/// Ein OAuth-Token für einen Upstream. <see cref="Payload"/> ist der DataProtection-verschlüsselte
/// Blob mit Access- und Refresh-Token — dieselbe Behandlung wie Upstream-Credentials (NFR-04).
/// Ablaufzeit und Issuer stehen im Klartext daneben, weil danach gefiltert und entschieden wird,
/// ohne den Blob zu entschlüsseln.
/// </summary>
public sealed class UpstreamOAuthTokenRow
{
    public Guid ServerId { get; set; }

    public byte[] Payload { get; set; } = [];

    public string Issuer { get; set; } = string.Empty;

    public long? ExpiresAtTicks { get; set; }

    public long ObtainedAtTicks { get; set; }
}

/// <summary>
/// Der festgehaltene Fingerabdruck einer Tool-Definition (Rug-Pull-Erkennung). Eine Zeile je
/// Upstream und Tool; <see cref="PendingHash"/> gesetzt heißt: abweichende Fassung gesehen, Tool
/// ist bis zur Annahme aus dem Katalog genommen.
/// </summary>
public sealed class ToolDefinitionPinRow
{
    public Guid ServerId { get; set; }

    public string Tool { get; set; } = string.Empty;

    public string AcceptedHash { get; set; } = string.Empty;

    public long AcceptedAtTicks { get; set; }

    public string? PendingHash { get; set; }

    public long? PendingSinceTicks { get; set; }
}

/// <summary>
/// Eine installierte Connector-Paketversion (ADR-0016). Die Dateien liegen auf der Platte unter
/// <see cref="Directory"/>; diese Zeile sagt, welche Fassung gilt und woher sie kommt.
/// </summary>
public sealed class ConnectorPackageRow
{
    public string PackageId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Transport nach <see cref="UpstreamTransportKind"/>.</summary>
    public int Transport { get; set; }

    /// <summary>Fingerprint des Herausgebers, dessen Signatur das Manifest getragen hat.</summary>
    public string PublisherKeyId { get; set; } = string.Empty;

    /// <summary>Stufe zum Zeitpunkt der Installation — eine spätere Änderung wertet nicht rückwirkend auf.</summary>
    public int TrustLevel { get; set; }

    /// <summary>SHA-256 über die signierten Manifest-Bytes; identifiziert die Fassung eindeutig.</summary>
    public string ManifestSha256 { get; set; } = string.Empty;

    public string Directory { get; set; } = string.Empty;

    /// <summary>Zustand nach <see cref="PackageState"/>.</summary>
    public int State { get; set; }

    public long InstalledAtTicks { get; set; }

    public long? ActivatedAtTicks { get; set; }

    /// <summary>Erteilte Zugriffe, mit '\n' getrennt — Anzeige und Audit, keine Durchsetzung.</summary>
    public string? GrantedCapabilities { get; set; }

    /// <summary>Warum die Probe gescheitert ist. Nur bei <see cref="PackageState.Failed"/> gesetzt.</summary>
    public string? FailureReason { get; set; }
}

/// <summary>Versioniertes Text-Asset (Skill/Prompt/Instruction, FR-40, WP6.4). Append-only pro Version.</summary>
public sealed class AssetRow
{
    public Guid Id { get; set; }

    public int Version { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; set; }
}
