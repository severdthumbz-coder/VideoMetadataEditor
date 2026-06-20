using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

public class WriteSelfTestTests
{
    [Fact]
    public void Run_MissingFile_ReturnsFailReport_DoesNotThrow()
    {
        var report = WriteSelfTest.Run(@"Z:\does\not\exist\nope.mp4");
        Assert.False(report.AllPassed);
        Assert.True(report.Failed >= 1);
        Assert.Contains("does not exist", report.Interpretation);
    }

    [Fact]
    public void FormatLog_IncludesHeaderStepsAndInterpretation()
    {
        var steps = new List<WriteSelfTest.StepResult>
        {
            new(1, "Container opens", WriteSelfTest.Outcome.Pass, "ok"),
            new(2, "Semicolon probe", WriteSelfTest.Outcome.Fail, "truncated at ';'"),
            new(3, "Atom layout", WriteSelfTest.Outcome.Info, "faststart OK"),
        };
        var report = new WriteSelfTest.Report(@"C:\movie.mp4", steps, "Some checks failed.");
        var log = WriteSelfTest.FormatLog(report);

        Assert.Contains("Full File Write Diagnostic", log);
        Assert.Contains(@"C:\movie.mp4", log);
        Assert.Contains("[PASS]  1. Container opens", log);
        Assert.Contains("[FAIL]  2. Semicolon probe", log);
        Assert.Contains("[INFO]  3. Atom layout", log);
        Assert.Contains("INTERPRETATION:", log);
        Assert.Contains("Some checks failed.", log);
    }

    [Fact]
    public void Report_CountsPassAndFailCorrectly()
    {
        var steps = new List<WriteSelfTest.StepResult>
        {
            new(1, "a", WriteSelfTest.Outcome.Pass, ""),
            new(2, "b", WriteSelfTest.Outcome.Pass, ""),
            new(3, "c", WriteSelfTest.Outcome.Fail, ""),
            new(4, "d", WriteSelfTest.Outcome.Info, ""),
        };
        var report = new WriteSelfTest.Report("x", steps, "");
        Assert.Equal(2, report.Passed);
        Assert.Equal(1, report.Failed);
        Assert.False(report.AllPassed);
    }
}
