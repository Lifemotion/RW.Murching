using Microsoft.Extensions.Logging;

namespace Bwl.Murching.Dubbing;

/// <summary>PyTorch models (XTTS-v2, Chatterbox) behind the Python worker, exposed as an <see cref="ITtsEngine"/>.</summary>
public sealed class SidecarTtsEngine : ITtsEngine
{
    private readonly TtsSidecar _sidecar;

    private SidecarTtsEngine(TtsSidecar sidecar)
    {
        _sidecar = sidecar;
    }

    public string Name => _sidecar.Engine;

    public string Description => $"{_sidecar.Engine} via python sidecar on {_sidecar.Device} @ {_sidecar.SampleRate} Hz";

    public bool SupportsVoiceCloning => true;

    public bool SupportsSpeed => _sidecar.Engine == "xtts";

    public static async Task<SidecarTtsEngine> StartAsync(string engine, string device, string? pythonPath, ILogger? logger, CancellationToken ct)
    {
        var python = TtsSidecar.FindPython(pythonPath)
                     ?? throw new InvalidOperationException("TTS environment not found. Create it with scripts/setup-tts.ps1 (Python 3.10–3.12 venv with torch + coqui-tts) or point MURCH_TTS_PYTHON at its python.exe. Engines 'qwen' need no Python.");
        var worker = TtsSidecar.FindWorkerScript()
                     ?? throw new InvalidOperationException("scripts/tts_worker.py not found next to the application or in the repository.");
        var sidecar = await TtsSidecar.StartAsync(python, worker, engine, device, logger, ct).ConfigureAwait(false);
        return new SidecarTtsEngine(sidecar);
    }

    public Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default)
    {
        if (request.ReferenceWav is null)
        {
            throw new ArgumentException("The sidecar engines need a reference clip.", nameof(request));
        }

        return _sidecar.SynthesizeAsync(request.Text, request.Language, request.ReferenceWav, request.OutputWav, request.Speed, ct);
    }

    public ValueTask DisposeAsync() => _sidecar.DisposeAsync();
}
