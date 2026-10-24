namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Der Gateway mit dem echten Rust-Host als <c>BIFROST_WASI_HOST</c> — für die
/// <c>WasiRealHost*</c>-Tests. Ist kein Host gebaut, bleibt es beim Stub; die Tests selbst
/// überspringen dann über <see cref="WasiHostPaths.RequireHost"/>, die Fixture darf das nicht.
/// </summary>
public sealed class RealWasiHostGatewayFixture : GatewayFixture
{
    protected override string WasiHost => WasiHostPaths.Locate() ?? base.WasiHost;
}
