namespace Bwl.Murching.Subtitles;

/// <summary>Language-light punctuation and word heuristics shared by the cue builder and the line wrapper.</summary>
public static class TextRules
{
    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English: articles, prepositions, conjunctions, determiners, auxiliaries
        "a", "an", "the", "to", "of", "in", "on", "at", "by", "for", "with", "and", "or", "but", "as", "if", "that", "than", "from", "into", "is", "are", "was", "were", "be",
        "this", "these", "those", "my", "your", "his", "its", "our", "their", "all", "some", "any", "every", "each", "no", "both", "either", "neither", "such", "what", "which", "whose",
        "not", "very", "so", "too", "nor", "yet", "while", "because", "although", "though", "unless", "until", "when", "where", "whether",
        // Russian / Ukrainian
        "и", "а", "но", "или", "в", "во", "на", "с", "со", "к", "ко", "о", "об", "от", "до", "за", "по", "из", "у", "не", "ни", "же", "бы", "ли", "что", "чтобы", "как", "для", "при", "про", "без", "над", "под", "перед", "через", "это", "то", "та", "те", "тот",
        "мой", "моя", "моё", "мои", "твой", "твоя", "твоё", "твои", "наш", "наша", "наше", "наши", "ваш", "ваша", "ваше", "ваши", "его", "её", "их", "свой", "своя", "своё", "свои",
        "этот", "эта", "эти", "того", "той", "тем", "весь", "вся", "всё", "все", "каждый", "каждая", "каждое", "любой", "какой", "какая", "какое", "какие", "который", "которая", "которое", "которые", "чей",
        "очень", "уже", "ещё", "еще", "лишь", "только", "даже", "если", "когда", "пока", "хотя", "потому", "поэтому", "также", "тоже",
        "і", "й", "та", "але", "чи", "у", "з", "із", "зі", "до", "від", "на", "по", "що", "як", "для", "при", "про", "без", "цей", "ця", "це", "ці", "той", "мій", "твій", "наш", "ваш", "їх", "свій",
        // German / French / Spanish / Italian
        "der", "die", "das", "ein", "eine", "und", "oder", "zu", "von", "mit", "auf", "für", "im", "am",
        "le", "la", "les", "un", "une", "des", "du", "de", "et", "ou", "à", "au", "aux", "en", "dans", "pour", "par", "sur",
        "el", "los", "las", "y", "o", "con", "por", "para", "del", "al",
        "il", "lo", "gli", "uno", "una", "e", "con", "per", "nel", "nella",
    };

    public static bool EndsSentence(string word)
    {
        var w = TrimClosing(word);
        return w.Length > 0 && w[^1] is '.' or '!' or '?' or '…' or '。' or '！' or '？';
    }

    public static bool EndsClause(string word)
    {
        var w = TrimClosing(word);
        return w.Length > 0 && w[^1] is ',' or ';' or ':' or '—' or '–' or '、' or '，';
    }

    public static bool IsDialogueDash(string word) =>
        word.Length > 0 && word[0] is '-' or '–' or '—' && (word.Length == 1 || char.IsLetter(word[1]) || word[1] == ' ');

    public static bool IsFunctionWord(string word)
    {
        var w = word.TrimEnd('.', ',', ';', ':', '!', '?', '…', '"', '\'', '»', ')', ']');
        return FunctionWords.Contains(w);
    }

    private static string TrimClosing(string word) => word.TrimEnd('"', '\'', '»', ')', ']', '”', '’');
}
