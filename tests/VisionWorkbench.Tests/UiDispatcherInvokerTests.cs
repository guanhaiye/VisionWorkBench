using System.Windows.Threading;
using VisionWorkbench.App;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class UiDispatcherInvokerTests
{
    [Fact]
    public void InvokeIfRequired_MarshalsBackgroundCallbackToOwningDispatcher()
    {
        var ownerThreadId = 0;
        var callbackThreadId = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ownerThreadId = Environment.CurrentManagedThreadId;
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            timeout.Tick += (_, _) =>
            {
                failure = new TimeoutException("Dispatcher 回调未在 5 秒内执行");
                timeout.Stop();
                frame.Continue = false;
            };
            timeout.Start();

            _ = Task.Run(() => UiDispatcherInvoker.InvokeIfRequired(dispatcher, () =>
            {
                callbackThreadId = Environment.CurrentManagedThreadId;
                timeout.Stop();
                frame.Continue = false;
            }));
            Dispatcher.PushFrame(frame);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(7)), "STA Dispatcher 线程未退出");
        Assert.Null(failure);
        Assert.Equal(ownerThreadId, callbackThreadId);
    }
}
