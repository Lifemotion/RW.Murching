using Bwl.Murching.Pipeline;

namespace Bwl.Murching.Tests.Pipeline;

public class SubtitleJobTests
{
    [Fact]
    public void Default_output_path_adds_language_and_extension()
    {
        var path = SubtitleJob.DefaultOutputPath(@"C:\media\talk.mp4", "ru", ".srt");
        Assert.Equal(@"C:\media\talk.ru.srt", path);
    }

    [Fact]
    public void Default_output_path_without_language()
    {
        Assert.Equal(@"C:\media\talk.srt", SubtitleJob.DefaultOutputPath(@"C:\media\talk.mp4", null, ".srt"));
        Assert.Equal(@"C:\media\talk.srt", SubtitleJob.DefaultOutputPath(@"C:\media\talk.mp4", "und", ".srt"));
    }

    [Fact]
    public void Output_hint_directory_is_used()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"murch-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = SubtitleJob.DefaultOutputPath(@"C:\media\talk.mp4", "en", ".vtt", dir);
            Assert.Equal(Path.Combine(dir, "talk.en.vtt"), path);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}
