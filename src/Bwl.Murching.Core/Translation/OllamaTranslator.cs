using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bwl.Murching.Translation;

/// <summary>
/// Subtitle translation through the Ollama chat API with structured (JSON schema) output.
/// Lines are numbered, the model must return exactly as many lines; mismatches are retried and finally fall back
/// to one request per line, so a batch can never silently shift the subtitles.
/// </summary>
public sealed class OllamaTranslator : ITranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["ru"] = "Russian", ["uk"] = "Ukrainian", ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish",
        ["it"] = "Italian", ["pt"] = "Portuguese", ["pl"] = "Polish", ["nl"] = "Dutch", ["sv"] = "Swedish", ["no"] = "Norwegian",
        ["da"] = "Danish", ["fi"] = "Finnish", ["cs"] = "Czech", ["tr"] = "Turkish", ["ja"] = "Japanese", ["ko"] = "Korean",
        ["zh"] = "Chinese", ["ar"] = "Arabic", ["he"] = "Hebrew", ["hi"] = "Hindi", ["et"] = "Estonian", ["lv"] = "Latvian",
        ["lt"] = "Lithuanian", ["ka"] = "Georgian", ["hy"] = "Armenian", ["kk"] = "Kazakh", ["be"] = "Belarusian",
    };

    private readonly HttpClient _http;
    private readonly TranslationOptions _options;
    private readonly ILogger _logger;
    private bool _thinkSupported = true;

    public OllamaTranslator(TranslationOptions options, ILogger? logger = null, HttpClient? http = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _http = http ?? new HttpClient { BaseAddress = options.Endpoint, Timeout = options.RequestTimeout };
    }

    public string Name => $"ollama/{_options.Model}";

    public static string LanguageName(string code) => LanguageNames.TryGetValue(code, out var n) ? n : code;

    /// <summary>Checks that the server answers and the model is present (pulls nothing).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/tags", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct).ConfigureAwait(false);
            var names = json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).Where(n => n is not null).ToList() ?? [];
            var wanted = _options.Model.Contains(':') ? _options.Model : _options.Model + ":latest";
            var found = names.Any(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (!found)
            {
                _logger.LogWarning("Ollama model {Model} is not installed; available: {Models}. Run 'ollama pull {Model}'.", _options.Model, string.Join(", ", names), _options.Model);
            }

            return found;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Ollama is not reachable at {Endpoint}: {Message}", _options.Endpoint, ex.Message);
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> lines, IReadOnlyList<string> previousTranslations, CancellationToken ct = default)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await RequestAsync(lines, previousTranslations, strictReminder: attempt > 0, ct).ConfigureAwait(false);
            if (result is not null && result.Count == lines.Count)
            {
                return result;
            }

            _logger.LogDebug("Translation batch of {Count} lines came back with {Got} lines (attempt {Attempt})", lines.Count, result?.Count ?? -1, attempt + 1);
        }

        // Last resort: one line at a time (slow but never misaligned).
        _logger.LogInformation("Falling back to line-by-line translation for a batch of {Count}", lines.Count);
        var output = new List<string>(lines.Count);
        var context = previousTranslations.ToList();
        foreach (var line in lines)
        {
            var single = await RequestAsync([line], context.TakeLast(_options.ContextLines).ToList(), strictReminder: true, ct).ConfigureAwait(false);
            var translated = single is { Count: > 0 } ? single[0] : line;
            output.Add(translated);
            context.Add(translated);
        }

        return output;
    }

    private async Task<List<string>?> RequestAsync(IReadOnlyList<string> lines, IReadOnlyList<string> previous, bool strictReminder, CancellationToken ct)
    {
        var target = LanguageName(_options.TargetLanguage);
        var source = _options.SourceLanguage is { } s && s != "auto" && s != "und" ? LanguageName(s) : null;

        var system = new StringBuilder();
        system.Append("You are a professional subtitle translator. Translate ").Append(source is null ? "the lines" : $"from {source}").Append(" into ").Append(target).Append(". ");
        system.Append("Rules: translate each numbered line separately and return exactly the same number of lines in the same order; ");
        system.Append("keep the meaning, tone, register, profanity and humour; keep names and nicknames (transliterate into the target script when natural); ");
        system.Append("be concise like real subtitles; do not merge, split, explain or add anything; if a line is untranslatable noise, return it unchanged. ");
        system.Append("Return JSON: {\"lines\": [\"...\", ...]}.");
        if (!string.IsNullOrWhiteSpace(_options.Glossary))
        {
            system.Append(" Terminology and style hints: ").Append(_options.Glossary.Trim());
        }

        var user = new StringBuilder();
        if (previous.Count > 0)
        {
            user.Append("Previous lines (already translated, for context only, do not repeat them):\n");
            foreach (var p in previous.TakeLast(_options.ContextLines))
            {
                user.Append("  ").Append(p).Append('\n');
            }

            user.Append('\n');
        }

        user.Append(CultureInfo.InvariantCulture, $"Translate these {lines.Count} lines into {target}");
        if (strictReminder)
        {
            user.Append(CultureInfo.InvariantCulture, $" and return EXACTLY {lines.Count} strings in \"lines\"");
        }

        user.Append(":\n");
        for (var i = 0; i < lines.Count; i++)
        {
            user.Append(CultureInfo.InvariantCulture, $"{i + 1}: {lines[i]}\n");
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["minItems"] = lines.Count,
                    ["maxItems"] = lines.Count,
                },
            },
            ["required"] = new JsonArray("lines"),
        };

        var request = new ChatRequest
        {
            Model = _options.Model,
            Stream = false,
            Think = _thinkSupported ? false : null,
            Format = schema,
            KeepAlive = "10m",
            Options = new ChatOptions { Temperature = _options.Temperature, NumCtx = _options.ContextWindow, NumPredict = Math.Max(512, lines.Sum(l => l.Length) * 3) },
            Messages =
            [
                new ChatMessage("system", system.ToString()),
                new ChatMessage("user", user.ToString()),
            ],
        };

        using var response = await _http.PostAsJsonAsync("/api/chat", request, JsonOptions, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (_thinkSupported && body.Contains("think", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Model does not accept the 'think' switch; retrying without it");
                _thinkSupported = false;
                return await RequestAsync(lines, previous, strictReminder, ct).ConfigureAwait(false);
            }

            throw new HttpRequestException($"Ollama {response.StatusCode}: {Truncate(body, 300)}");
        }

        try
        {
            var chat = JsonSerializer.Deserialize<ChatResponse>(body, JsonOptions);
            var content = chat?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var parsed = JsonNode.Parse(content);
            var array = parsed?["lines"]?.AsArray();
            if (array is null)
            {
                return null;
            }

            return array.Select(n => (n?.GetValue<string>() ?? string.Empty).Trim()).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Unparseable translator output: {Body}", Truncate(body, 300));
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class ChatRequest
    {
        public required string Model { get; init; }

        public required List<ChatMessage> Messages { get; init; }

        public bool Stream { get; init; }

        public bool? Think { get; init; }

        public JsonNode? Format { get; init; }

        public string? KeepAlive { get; init; }

        public ChatOptions? Options { get; init; }
    }

    private sealed record ChatMessage(string Role, string Content);

    private sealed class ChatOptions
    {
        public float Temperature { get; init; }

        public int NumCtx { get; init; }

        public int NumPredict { get; init; }
    }

    private sealed class ChatResponse
    {
        public ChatResponseMessage? Message { get; init; }
    }

    private sealed class ChatResponseMessage
    {
        public string? Role { get; init; }

        public string? Content { get; init; }
    }
}
