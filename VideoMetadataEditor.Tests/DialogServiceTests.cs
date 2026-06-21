using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

public class DialogServiceTests
{
    [Fact]
    public void NullDialogService_OkButton_ReturnsOk()
    {
        var r = NullDialogService.Instance.Show("m", "t", DialogButtons.Ok);
        Assert.Equal(DialogResult.Ok, r);
    }

    [Theory]
    [InlineData(DialogButtons.OkCancel)]
    [InlineData(DialogButtons.YesNo)]
    [InlineData(DialogButtons.YesNoCancel)]
    public void NullDialogService_DestructivePrompts_NeverAutoConfirm(DialogButtons buttons)
    {
        // A headless context must not silently say Yes/OK to a confirmation prompt.
        var r = NullDialogService.Instance.Show("delete everything?", "t", buttons);
        Assert.Equal(DialogResult.Cancel, r);
    }

    [Fact]
    public void NullDialogService_Pickers_ReturnNull()
    {
        Assert.Null(NullDialogService.Instance.PickFolder("t"));
        Assert.Null(NullDialogService.Instance.PickOpenFile("t", "*.*"));
        Assert.Null(NullDialogService.Instance.PickSaveFile("t", "*.*"));
    }
}
