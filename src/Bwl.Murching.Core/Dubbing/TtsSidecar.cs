using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bwl.Murching.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bwl.Murching.Dubbing;

public sealed record TtsResult(bool Ok, TimeSpan Duration, int SampleRate, string? Error, TimeSpan Elapsed);

/// <summary>
/// Drives <c>scripts/tts_worker.py</c>: a long-lived Python process that loads a voice-cloning TTS model once and
/// answers JSON-line requests. Keeps the heavy torch stack out of the .NET process.
/// </summary>
public sealed class TtsSidecar : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task _stderrPump;
    private int _nextId;

    private TtsSidecar(Process process, string engine, int sampleRate, string device, ILogger logger, Task stderrPump)
    {
        _process = process;
        _logger = logger;
        _stderrPump = stderrPump;
        Engine = engine;
        SampleRate = sampleRate;
        Device = device;
    }

    public string Engine { get; }

    public int SampleRate { get; }

    public string Device { get; }

    /// <summary>Locates the TTS virtual environment's python.exe: explicit path, <c>MURCH_TTS_PYTHON</c>, repo tools/tts-venv, app data.</summary>
    public static string? FindPython(string? explicitPath = null)
    {
        var candidates = new List<string?>
        {
            explicitPath,
            Environment.GetEnvironmentVariable("MURCH_TTS_PYTHON"),
            AppPaths.ToolsDir is { } tools ? Path.Combine(tools, "tts-venv", "Scripts", "python.exe") : null,
            Path.Combine(AppPaths.DataRoot, "tts-venv", "Scripts", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "tts-venv", "Scripts", "python.exe"),
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }

    /// <summary>Locates scripts/tts_worker.py (repo checkout or next to the application).</summary>
    public static string? FindWorkerScript()
    {
        var candidates = new List<string?>
        {
            AppPaths.RepoRoot is { } root ? Path.Combine(root, "scripts", "tts_worker.py") : null,
            Path.Combine(AppContext.BaseDirectory, "scripts", "tts_worker.py"),
            Path.Combine(AppContext.BaseDirectory, "tts_worker.py"),
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }

    public static async Task<TtsSidecar> StartAsync(string python, string workerScript, string engine, string device, ILogger? logger = null, CancellationToken ct = default)
    {
        logger ??= NullLogger.Instance;
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(workerScript),
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(workerScript);
        psi.ArgumentList.Add("--engine");
        psi.ArgumentList.Add(engine);
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(device);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["COQUI_TOS_AGREED"] = "1";

        var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.AutoFlush = true;
        process.StandardInput.NewLine = "\n";

        var stderrPump = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length > 0)
                {
                    logger.LogDebug("tts: {Line}", line);
                }
            }
        }, CancellationToken.None);

        logger.LogInformation("Starting TTS worker ({Engine} on {Device}); the first start downloads the model (~2 GB)", engine, device);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                await stderrPump.ConfigureAwait(false);
                throw new InvalidOperationException($"TTS worker exited during start-up (exit code {process.ExitCode}). Run with -vv to see its log; the venv may be missing packages.");
            }

            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                logger.LogDebug("tts stdout: {Line}", line);
                continue;
            }

            var evt = node?["event"]?.GetValue<string>();
            if (evt == "ready")
            {
                var sr = node?["sample_rate"]?.GetValue<int>() ?? 24000;
                var dev = node?["device"]?.GetValue<string>() ?? device;
                logger.LogInformation("TTS worker ready: {Engine} @ {Rate} Hz on {Device}", engine, sr, dev);
                return new TtsSidecar(process, engine, sr, dev, logger, stderrPump);
            }

            if (evt == "fatal")
            {
                throw new InvalidOperationException("TTS worker failed to start: " + node?["error"]?.GetValue<string>());
            }
        }
    }

    public async Task<TtsResult> SynthesizeAsync(string text, string language, string referenceWav, string outputWav, double speed = 1.0, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId).ToString();
            var request = new JsonObject
            {
                ["id"] = id,
                ["text"] = text,
                ["language"] = language,
                ["reference"] = Path.GetFullPath(referenceWav),
                ["output"] = Path.GetFullPath(outputWav),
                ["speed"] = speed,
            };
            var clock = Stopwatch.StartNew();
            await _process.StandardInput.WriteLineAsync(request.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).ConfigureAwait(false);

            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException($"TTS worker exited unexpectedly (exit code {(_process.HasExited ? _process.ExitCode : -1)}).");
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    _logger.LogDebug("tts stdout: {Line}", line);
                    continue;
                }

                if (node?["id"]?.GetValue<string>() != id)
                {
                    continue;
                }

                var ok = node["ok"]?.GetValue<bool>() ?? false;
                return new TtsResult(
                    ok,
                    TimeSpan.FromSeconds(node["duration"]?.GetValue<double>() ?? 0),
                    node["sample_rate"]?.GetValue<int>() ?? SampleRate,
                    node["error"]?.GetValue<string>(),
                    clock.Elapsed);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("{\"cmd\":\"quit\"}").ConfigureAwait(false);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // best effort
        }

        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        _process.Dispose();
        _gate.Dispose();
    }
}
