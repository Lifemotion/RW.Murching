namespace Bwl.Murching.Translation;

public interface ITranslator : IDisposable
{
    /// <summary>Human readable engine / model name for logs and summaries.</summary>
    string Name { get; }

    /// <summary>
    /// Translates <paramref name="lines"/> one-to-one (same count, same order). <paramref name="previousTranslations"/>
    /// are the last few already translated lines, given for coherence only.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> previousTranslations,
        CancellationToken ct = default);
}
