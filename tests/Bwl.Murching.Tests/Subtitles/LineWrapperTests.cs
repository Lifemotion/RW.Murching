using Bwl.Murching.Subtitles;

namespace Bwl.Murching.Tests.Subtitles;

public class LineWrapperTests
{
    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Short_text_stays_on_one_line()
    {
        var lines = LineWrapper.Wrap(Words("Hello there, general Kenobi."), 42, 2);
        Assert.Equal(["Hello there, general Kenobi."], lines);
    }

    [Fact]
    public void Two_lines_are_balanced_when_no_punctuation()
    {
        var lines = LineWrapper.Wrap(Words("one two three four five six seven eight nine ten eleven twelve"), 42, 2);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= 42, l));
        Assert.InRange(Math.Abs(lines[0].Length - lines[1].Length), 0, 8);
    }

    [Fact]
    public void Break_prefers_sentence_end_even_if_unbalanced()
    {
        var lines = LineWrapper.Wrap(Words("Басня Эзопа. Перевод Льва Николаевича Толстого."), 42, 2);
        Assert.Equal(["Басня Эзопа.", "Перевод Льва Николаевича Толстого."], lines);
    }

    [Fact]
    public void Break_prefers_clause_comma()
    {
        var lines = LineWrapper.Wrap(Words("Для более подробной информации, посетите сайт проекта LibriVox"), 42, 2);
        Assert.Equal("Для более подробной информации,", lines[0]);
    }

    [Fact]
    public void Break_before_dialogue_dash()
    {
        var lines = LineWrapper.Wrap(Words("- Ты идёшь? - Нет, я остаюсь дома сегодня."), 42, 2);
        Assert.Equal("- Ты идёшь?", lines[0]);
        Assert.Equal("- Нет, я остаюсь дома сегодня.", lines[1]);
    }

    [Fact]
    public void Does_not_orphan_function_word_at_line_end()
    {
        var lines = LineWrapper.Wrap(Words("we went to the store and bought a lot of very tasty fresh bread"), 42, 2);
        Assert.Equal(2, lines.Count);
        Assert.False(TextRules.IsFunctionWord(lines[0].Split(' ')[^1]), lines[0]);
    }

    [Fact]
    public void Respects_max_lines_of_three()
    {
        var text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon";
        var lines = LineWrapper.Wrap(Words(text), 40, 3);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= 40, l));
        Assert.Equal(text, string.Join(' ', lines));
    }

    [Fact]
    public void Greedy_line_count_is_minimal()
    {
        Assert.Equal(1, LineWrapper.GreedyLineCount(Words("a b c"), 10));
        Assert.Equal(2, LineWrapper.GreedyLineCount(Words("aaaa bbbb cccc"), 9));
        Assert.Equal(3, LineWrapper.GreedyLineCount(Words("aaaa bbbb cccc"), 4));
    }
}
