using System.Diagnostics;
using System.Text;

namespace Bwl.Murching.Common;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public sealed class ProcessFailedException(string fileName, int exitCode, string stdErr)
    : Exception($"{Path.GetFileName(fileName)} exited with code {exitCode}: {Tail(stdErr)}")
{
    public string FileName { get; } = fileName;
    public int ExitCode { get; } = exitCode;
    public string StdErr { get; } = stdErr;

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(6));
    }
}

/// <summary>Small helper for running external tools (ffmpeg, ffprobe) without deadlocking on their pipes.</summary>
public static class ProcessRunner
{
    /// <summary>Runs a process and captures both text streams.</summary>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken ct = default, bool throwOnError = true)
    {
        using var process = CreateProcess(fileName, arguments);
        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var result = new ProcessResult(process.ExitCode, await stdOutTask.ConfigureAwait(false), await stdErrTask.ConfigureAwait(false));
        if (throwOnError && !result.Success)
        {
            throw new ProcessFailedException(fileName, result.ExitCode, result.StdErr);
        }

        return result;
    }

    /// <summary>Runs a process, streaming binary stdout to <paramref name="consumeStdOut"/> while stderr is collected.</summary>
    public static async Task RunStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        Func<Stream, CancellationToken, Task> consumeStdOut,
        CancellationToken ct = default)
    {
        using var process = CreateProcess(fileName, arguments);
        process.Start();
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await consumeStdOut(process.StandardOutput.BaseStream, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdErr = await stdErrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ProcessFailedException(fileName, process.ExitCode, stdErr);
        }
    }

    private static Process CreateProcess(string fileName, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        return new Process { StartInfo = psi };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}
