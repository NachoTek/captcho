// SafeDispatch.cs — Observed fire-and-forget dispatch (issue #80).
//
// A stowed exception (0xc000027b) is an unobserved async failure rethrown on
// the XAML dispatcher: the WER record names Microsoft.UI.Xaml.dll as the
// crash site, but the faulting async operation is lost. Every fire-and-forget
// dispatch — Global Hotkey-triggered and launch-behavior-triggered Workflow
// Session tasks, fire-and-forget dialog presentation — must run through this
// seam so a fault is observed and routed to a failure sink instead of
// escaping.
//
// This is observability, not logging: the global UnhandledException /
// UnobservedTaskException disk handlers are tracked separately (issue #81).

using System;
using System.Threading.Tasks;

namespace captcho.UI;

/// <summary>
/// Runs a fire-and-forget operation so its outcome is always observed. The
/// operation's invocation, its task, and the failure sink are all guarded:
/// nothing that escapes this method can become an unobserved task fault.
/// </summary>
public static class SafeDispatch
{
    /// <summary>
    /// Starts <paramref name="operation"/> and observes its task. A
    /// synchronous throw from the operation, or a later fault, is routed to
    /// <paramref name="onFailure"/> on the operation's captured context (the
    /// UI dispatcher for WinUI-started tasks). A sink that itself throws is
    /// swallowed — the sink must never produce a new unobserved fault.
    /// </summary>
    public static void Run(Func<Task> operation, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onFailure);

        Task task;
        try
        {
            task = operation();
        }
        catch (Exception ex)
        {
            Notify(onFailure, ex);
            return;
        }

        // Deliberately async void: this is the observe-and-report shim, and
        // every continuation path is caught. No ConfigureAwait(false) — the
        // sink must run on the operation's captured (UI) context.
        async void Observe()
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                Notify(onFailure, ex);
            }
        }

        Observe();
    }

    private static void Notify(Action<Exception> onFailure, Exception ex)
    {
        try
        {
            onFailure(ex);
        }
        catch
        {
            // The failure sink must never produce an unobserved fault of its
            // own; a broken sink is dropped.
        }
    }
}
