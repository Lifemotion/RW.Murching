using System.CommandLine;
using Microsoft.Extensions.Logging;

namespace Bwl.Murching.Cli;

internal static class GlobalOptions
{
    public static readonly Option<int> Verbose = new("--verbose", "-v")
    {
        Description = "Verbose output (-v: info, -vv: debug incl. whisper.cpp logs, -vvv: trace).",
        Arity = ArgumentArity.Zero,
        Recursive = true,
        AllowMultipleArgumentsPerToken = false,
        CustomParser = result => result.Tokens.Count == 0 ? 1 : result.Tokens.Count,
    };

    public static readonly Option<bool> Quiet = new("--quiet", "-q")
    {
        Description = "Suppress progress display; print only the result.",
        Recursive = true,
    };

    public static int VerbosityOf(ParseResult parseResult)
    {
        // Count occurrences of -v / --verbose (System.CommandLine collapses repeated flags into one result with N tokens).
        var result = parseResult.GetResult(Verbose);
        if (result is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var token in parseResult.Tokens)
        {
            if (token.Value is "-v" or "--verbose")
            {
                count++;
            }
            else if (token.Value.StartsWith("-v", StringComparison.Ordinal) && token.Value.Skip(1).All(c => c == 'v'))
            {
                count += token.Value.Length - 1;
            }
        }

        return Math.Max(count, 1);
    }

    public static LogLevel LogLevelFor(int verbosity) => verbosity switch
    {
        <= 0 => LogLevel.Warning,
        1 => LogLevel.Information,
        2 => LogLevel.Debug,
        _ => LogLevel.Trace,
    };

    public static ILoggerFactory CreateLoggerFactory(int verbosity) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevelFor(verbosity));
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
                o.IncludeScopes = false;
            });
            // Route everything to stderr so stdout stays clean for the result.
            builder.AddFilter("Microsoft", LogLevel.Warning);
        });
}
