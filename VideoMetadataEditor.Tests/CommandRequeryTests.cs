using VideoMetadataEditor.ViewModels;
using Xunit;

namespace VideoMetadataEditor.Tests;

// Note: RelayCommand/AsyncRelayCommand themselves require System.Windows.Input.ICommand
// (WPF) and so are not compiled into this headless test project. These tests cover the
// platform-neutral CommandRequery seam, which is what makes the command classes portable.
public class CommandRequeryTests
{
    private sealed class FakeProvider : ICommandRequeryProvider
    {
        public int Invalidations;
        public int Handlers;
        public void AddRequeryHandler(EventHandler handler) => Handlers++;
        public void RemoveRequeryHandler(EventHandler handler) => Handlers--;
        public void Invalidate() => Invalidations++;
    }

    [Fact]
    public void DefaultProvider_IsSafe_AndDoesNotThrow()
    {
        var ex = Record.Exception(() => CommandRequery.Invalidate());
        Assert.Null(ex);
    }

    [Fact]
    public void NullProvider_HandlersAndInvalidate_AreNoOps()
    {
        var p = NullCommandRequery.Instance;
        EventHandler h = (_, _) => { };
        var ex = Record.Exception(() =>
        {
            p.AddRequeryHandler(h);
            p.Invalidate();
            p.RemoveRequeryHandler(h);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Provider_CanBeSwapped_AndInvalidateRoutesThrough()
    {
        var original = CommandRequery.Provider;
        try
        {
            var fake = new FakeProvider();
            CommandRequery.Provider = fake;
            CommandRequery.Invalidate();
            CommandRequery.Invalidate();
            Assert.Equal(2, fake.Invalidations);
        }
        finally { CommandRequery.Provider = original; }
    }

    [Fact]
    public void Provider_HandlerSubscription_RoutesThrough()
    {
        var original = CommandRequery.Provider;
        try
        {
            var fake = new FakeProvider();
            CommandRequery.Provider = fake;
            EventHandler h = (_, _) => { };
            CommandRequery.Provider.AddRequeryHandler(h);
            Assert.Equal(1, fake.Handlers);
            CommandRequery.Provider.RemoveRequeryHandler(h);
            Assert.Equal(0, fake.Handlers);
        }
        finally { CommandRequery.Provider = original; }
    }
}
