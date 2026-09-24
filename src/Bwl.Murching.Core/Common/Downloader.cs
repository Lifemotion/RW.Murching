using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Bwl.Murching.Common;

/// <summary>Progress of a single file download.</summary>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>HTTP downloader with resume support (<c>.part</c> files), progress reporting and optional SHA-256 verification.</summary>
public sealed class Downloader
{
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Bwl.Murching", "0.1"));
        return client;
    });

    private readonly HttpClient _http;

    public Downloader(HttpClient? http = null)
    {
        _http = http ?? SharedClient.Value;
    }

    public static Downloader Default { get; } = new();

    /// <summary>Downloads <paramref name="url"/> to <paramref name="destination"/>, resuming a previous partial download when possible.</summary>
    public async Task DownloadFileAsync(
        Uri url,
        string destination,
        IProgress<DownloadProgress>? progress = null,
        string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";

        for (var attempt = 0; ; attempt++)
        {
            long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent && attempt < 1)
            {
                // Server ignored the range request (or the file changed): start from scratch.
                File.Delete(part);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            var received = append ? existing : 0;
            long? total = response.Content.Headers.ContentLength is { } len ? len + received : null;

            await using (var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                var buffer = new byte[1 << 17];
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                var lastBytes = received;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    var elapsed = clock.Elapsed;
                    if (elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                    {
                        var speed = (received - lastBytes) / (elapsed - lastReport).TotalSeconds;
                        progress?.Report(new DownloadProgress(received, total, speed));
                        lastReport = elapsed;
                        lastBytes = received;
                    }
                }

                progress?.Report(new DownloadProgress(received, total ?? received, 0));
            }

            if (expectedSha256 is not null)
            {
                var actual = await ComputeSha256Async(part, ct).ConfigureAwait(false);
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    throw new InvalidDataException($"SHA-256 mismatch for {url}: expected {expectedSha256}, got {actual}.");
                }
            }

            File.Move(part, destination, overwrite: true);
            return;
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
