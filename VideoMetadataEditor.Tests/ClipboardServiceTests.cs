using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

public class ClipboardServiceTests
{
    [Fact]
    public void NullClipboardService_SetText_ReturnsFalse_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            var ok = NullClipboardService.Instance.SetText("anything");
            Assert.False(ok);
        });
        Assert.Null(ex);
    }
}
