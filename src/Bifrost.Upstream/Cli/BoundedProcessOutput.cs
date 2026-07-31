using System.Text;

namespace Bifrost.Upstream.Cli;

internal sealed record CapturedProcessStream(
    string Text,
    long TotalBytes,
    int CapturedBytes,
    bool Truncated);

internal static class BoundedProcessOutput
{
    public static async Task<CapturedProcessStream> ReadAsync(
        Stream stream, int maxBytes, Encoding encoding, CancellationToken ct)
    {
        var captured = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var buffer = new byte[8 * 1024];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            var remaining = maxBytes - checked((int)captured.Length);
            if (remaining > 0)
            {
                await captured.WriteAsync(
                    buffer.AsMemory(0, Math.Min(read, remaining)), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        var bytes = captured.ToArray();
        return new CapturedProcessStream(
            encoding.GetString(bytes),
            total,
            bytes.Length,
            total > bytes.Length);
    }
}
