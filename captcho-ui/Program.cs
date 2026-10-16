// Program.cs — Entry point for Windows App SDK 2.0 unpackaged app.
// Initializes the bootstrap before starting the WinUI 3 application.

using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using System.Threading.Tasks;

namespace captcho.UI;

/// <summary>
/// Program class with Windows App SDK bootstrap initialization.
/// </summary>
public static class Program
{
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2604")]
    [System.Diagnostics.DebuggerNonUserCodeAttribute]
    [System.STAThreadAttribute]
    static void Main(string[] args)
    {
        // Crash logging first (issue #81): every later failure — bootstrap,
        // XAML startup, an async void continuation, an unobserved background
        // task — must reach disk before the process can die as a stowed
        // exception. Initializes the log (writes "Logging to: <path>" once)
        // and observes finalizer-time unobserved-task faults.
        //
        // Deliberate crash semantics: the fault is logged but NOT marked
        // observed. With ThrowUnobservedTaskExceptions enabled, an unobserved
        // fault still fails the process after being logged — every
        // fire-and-forget dispatch in this app already routes through
        // SafeDispatch (issue #80), so any fault reaching here is by
        // definition an unlogged escape and should fail loudly, not vanish.
        CrashLog.Initialize();
        TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
            CrashLog.Write("UnobservedTaskException", eventArgs.Exception);

        // Initialize Windows App SDK bootstrap for unpackaged apps
        // This is required for Windows App SDK 2.0
        Bootstrap.Initialize(0x00020001); // Version 2.0.1.0 = 0x00020001

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
