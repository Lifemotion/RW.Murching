using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Bwl.Murching.Runtime;

/// <summary>
/// Process-wide Whisper.net runtime configuration. The native library is chosen once, on first use, so this must run
/// before <em>any</em> Whisper.net object is created (the VAD included).
/// </summary>
public static class WhisperRuntime
{
    private static readonly Lock Gate = new();
    private static IDisposable? _logBridge;
    private static ILogger? _bridgedLogger;
    private static ComputeDevice? _configuredDevice;

    /// <summary>Device the runtime was configured for, or null when nothing has been configured yet.</summary>
    public static ComputeDevice? ConfiguredDevice => _configuredDevice;

    /// <summary>The native runtime that Whisper.net actually loaded (null until the first model/VAD is created).</summary>
    public static RuntimeLibrary? LoadedLibrary => RuntimeOptions.LoadedLibrary;

    public static bool LoadedGpuRuntime => LoadedLibrary is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 or RuntimeLibrary.Vulkan or RuntimeLibrary.CoreML or RuntimeLibrary.OpenVino;

    /// <summary>Routes whisper.cpp logs into <paramref name="logger"/>, prepares CUDA and sets the runtime probe order.</summary>
    public static void Configure(ComputeDevice device, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        lock (Gate)
        {
            BridgeLogs(logger);

            if (RuntimeOptions.LoadedLibrary is not null)
            {
                if (_configuredDevice != device)
                {
                    logger.LogDebug("Whisper runtime already loaded ({Loaded}); request for {Device} ignored", RuntimeOptions.LoadedLibrary, device);
                }

                return;
            }

            _configuredDevice = device;
            if (device != ComputeDevice.Cpu)
            {
                CudaRuntime.Prepare(logger);
            }

            RuntimeOptions.RuntimeLibraryOrder = device switch
            {
                ComputeDevice.Cpu => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
                ComputeDevice.Cuda => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
                _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            };
        }
    }

    /// <summary>Configures with <see cref="ComputeDevice.Auto"/> unless something already configured the runtime.</summary>
    public static void EnsureConfigured(ILogger? logger = null)
    {
        lock (Gate)
        {
            if (_configuredDevice is null)
            {
                Configure(ComputeDevice.Auto, logger);
            }
        }
    }

    /// <summary>whisper.cpp build information (CPU features, CUDA archs). Triggers the native load.</summary>
    public static string GetRuntimeInfo(ComputeDevice device = ComputeDevice.Auto, ILogger? logger = null)
    {
        Configure(device, logger);
        return WhisperFactory.GetRuntimeInfo() ?? string.Empty;
    }

    private static void BridgeLogs(ILogger logger)
    {
        if (ReferenceEquals(_bridgedLogger, logger) || logger is NullLogger)
        {
            return;
        }

        _logBridge?.Dispose();
        _bridgedLogger = logger;
        _logBridge = LogProvider.AddLogger((level, message) =>
        {
            var text = message?.TrimEnd();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var mapped = level switch
            {
                WhisperLogLevel.Error => LogLevel.Error,
                WhisperLogLevel.Warning => LogLevel.Warning,
                WhisperLogLevel.Info => LogLevel.Debug,
                _ => LogLevel.Trace,
            };
            logger.Log(mapped, "whisper: {Message}", text);
        });
    }
}
