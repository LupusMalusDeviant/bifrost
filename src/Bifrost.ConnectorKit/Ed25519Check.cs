using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Bifrost.ConnectorKit;

/// <summary>
/// Prüft eine Ed25519-Signatur — für die abgetrennte Signatur des Einstiegspunkts.
/// <para>
/// Bewusst hier und nicht über den Kern: Dessen Verifizierer ist <c>internal</c>, und ihn für ein
/// Entwicklerwerkzeug zu öffnen hieße, Produktionscode für eine Bequemlichkeit aufzumachen. Die
/// Bibliothek ist dieselbe, mit der dieses Werkzeug signiert.
/// </para>
/// </summary>
internal static class Ed25519Check
{
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException)
        {
            // Ein Public Key, der kein gueltiger Kurvenpunkt ist, ist kein Sonderfall, sondern
            // schlicht kein passender Schluessel.
            return false;
        }
    }
}
