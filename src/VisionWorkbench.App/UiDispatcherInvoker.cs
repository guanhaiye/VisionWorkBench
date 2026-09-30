using System.Windows.Threading;

namespace VisionWorkbench.App;

/// <summary>Runs UI-bound callbacks on the owning WPF dispatcher.</summary>
public static class UiDispatcherInvoker
{
    public static void InvokeIfRequired(Dispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
