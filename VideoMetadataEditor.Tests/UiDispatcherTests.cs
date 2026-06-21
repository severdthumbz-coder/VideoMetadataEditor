using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

public class UiDispatcherTests
{
    [Fact]
    public void NullUiDispatcher_RunsActionInline()
    {
        var ran = false;
        NullUiDispatcher.Instance.Post(() => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void NullUiDispatcher_IsOnUiThread_True()
    {
        Assert.True(NullUiDispatcher.Instance.IsOnUiThread);
    }

    [Fact]
    public void NullUiDispatcher_InvalidateCommands_DoesNotThrow()
    {
        var ex = Record.Exception(() => NullUiDispatcher.Instance.InvalidateCommands());
        Assert.Null(ex);
    }
}
