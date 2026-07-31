using Bifrost.Upstream.Http;

namespace Bifrost.Upstream.OpenRpc;

/// <summary>
/// Holt eine OpenRPC-Beschreibung. Die Prüfung selbst steht in
/// <see cref="RemoteSpecFetcher"/> — sie ist nicht OpenRPC-eigen, und zwei Kopien einer
/// Sicherheitsprüfung driften auseinander, sobald eine davon nachgebessert wird.
/// </summary>
internal static class SpecFetcher
{
    /// <summary>Obergrenze für eine Beschreibung.</summary>
    public const long MaxBytes = RemoteSpecFetcher.MaxBytes;

    public static Task<string> FetchAsync(
        Uri location, bool allowPrivateTargets, TimeSpan timeout, CancellationToken ct)
        => RemoteSpecFetcher.FetchAsync(
            location, allowPrivateTargets, timeout, Fail, ct);

    public static Task EnsureTargetAllowedAsync(
        Uri target, bool allowPrivateTargets, CancellationToken ct)
        => RemoteSpecFetcher.EnsureTargetAllowedAsync(target, allowPrivateTargets, Fail, ct);

    private static Exception Fail(string message) => new OpenRpcImportException(message);
}

/// <summary>Fehler beim Import einer OpenRPC-Beschreibung.</summary>
public sealed class OpenRpcImportException : Exception
{
    public OpenRpcImportException(string message) : base(message) { }

    public OpenRpcImportException() { }

    public OpenRpcImportException(string message, Exception innerException)
        : base(message, innerException) { }
}
