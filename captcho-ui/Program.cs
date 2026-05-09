// Program.cs — Entry point for Windows App SDK 2.0 unpackaged app.
// Initializes the bootstrap before starting the WinUI 3 application.

using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

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
