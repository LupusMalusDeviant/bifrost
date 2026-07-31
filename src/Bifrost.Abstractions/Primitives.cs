namespace Bifrost.Abstractions;

/// <summary>Eindeutige Id eines registrierten Upstream-Servers.</summary>
public readonly record struct ServerId(Guid Value)
{
    public static ServerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>Eindeutige Id einer Identität (Agent via API-Key oder UI-User).</summary>
public readonly record struct IdentityId(Guid Value)
{
    public static IdentityId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>Monoton steigende Versionsnummer einer Upstream-Server-Konfiguration (FR-10).</summary>
public readonly record struct ConfigVersionId(int Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Eindeutige Id eines verteilten Assets (Skill/Prompt/Instruction, FR-40).</summary>
public readonly record struct AssetId(Guid Value)
{
    public static AssetId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>Monoton steigende Version eines Assets.</summary>
public readonly record struct AssetVersion(int Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Namespaced Tool-Name im Format <c>{serverSlug}__{toolName}</c> (FR-03).
/// Der Separator ist doppelter Unterstrich; Server-Slugs dürfen ihn deshalb nicht enthalten.
/// </summary>
public readonly record struct NamespacedToolName
{
    public const string Separator = "__";

    public string Value { get; }

    public NamespacedToolName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static NamespacedToolName Create(string serverSlug, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (serverSlug.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Server-Slug darf '{Separator}' nicht enthalten.", nameof(serverSlug));
        }

        return new NamespacedToolName($"{serverSlug}{Separator}{toolName}");
    }

    /// <summary>Zerlegt in Server-Slug und Original-Tool-Namen. False, wenn kein Separator enthalten ist.</summary>
    public bool TrySplit(out string serverSlug, out string toolName)
    {
        var idx = Value?.IndexOf(Separator, StringComparison.Ordinal) ?? -1;
        if (idx <= 0)
        {
            serverSlug = string.Empty;
            toolName = string.Empty;
            return false;
        }

        serverSlug = Value![..idx];
        toolName = Value[(idx + Separator.Length)..];
        return toolName.Length > 0;
    }

    public override string ToString() => Value ?? string.Empty;
}
