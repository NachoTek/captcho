// SafeDispatchTests.cs — Headless tests for observed fire-and-forget dispatch
// (issue #80).
//
// A stowed exception (0xc000027b) is an unobserved async WinRT failure
// rethrown on the XAML dispatcher. The discards MainWindow uses for
// Global Hotkey-triggered and launch-behavior-triggered Workflow Session
// tasks, and for fire-and-forget dialog presentation, must never leave a
// fault unobserved. SafeDispatch is the seam that observes those dispatches:
// a faulting (or synchronously throwing) operation routes its exception to a
// failure sink instead of escaping unobserved.

using System;
using System.Threading;
using System.Threading.Tasks;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class SafeDispatchTests
{
    private static readonly TimeSpan SinkTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CompletedOperation_NeverInvokesFailureSink()
    {
        var ranToCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sinkCalls = 0;

        SafeDispatch.Run(
            async () => { await Task.Yield(); ranToCompletion.SetResult(); },
            _ => Interlocked.Increment(ref sinkCalls));

        await ranToCompletion.Task.WaitAsync(SinkTimeout);
        // Give a wrong implementation time to fire the sink.
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref sinkCalls));
    }

    [Fact]
    public async Task FaultingOperation_ExceptionGoesToSinkNotCaller()
    {
        var sinkCalled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        Exception? escaped = null;
        try
        {
            SafeDispatch.Run(
                () => Task.FromException(new InvalidOperationException("workflow fault")),
                ex => sinkCalled.SetResult(ex));
        }
        catch (Exception ex)
        {
            escaped = ex;
        }

        Assert.Null(escaped);
        var received = await sinkCalled.Task.WaitAsync(SinkTimeout);
        Assert.IsType<InvalidOperationException>(received);
        Assert.Equal("workflow fault", received.Message);
    }

    [Fact]
    public async Task SynchronouslyThrowingOperation_ExceptionGoesToSink()
    {
        var sinkCalled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The operation throws before producing a Task — the invocation must
        // be inside the observed scope too. A throw lambda converts to any
        // delegate type, so no generic overload is needed.
        SafeDispatch.Run(
            (Func<Task>)(() => throw new InvalidOperationException("sync fault")),
            ex => sinkCalled.SetResult(ex));

        var received = await sinkCalled.Task.WaitAsync(SinkTimeout);
        Assert.IsType<InvalidOperationException>(received);
        Assert.Equal("sync fault", received.Message);
    }

    [Fact]
    public async Task FaultingOperation_SinkThatThrows_IsSwallowed()
    {
        // The failure sink must never itself produce an unobserved fault.
        var sinkAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        SafeDispatch.Run(
            () => Task.FromException(new InvalidOperationException("boom")),
            _ =>
            {
                sinkAttempted.SetResult();
                throw new ArgumentException("broken sink");
            });

        await sinkAttempted.Task.WaitAsync(SinkTimeout);
        // Give the swallowing continuation time to run; the contract is that
        // nothing escapes and the process survives.
        await Task.Yield();
        await Task.Delay(50);
        Assert.True(sinkAttempted.Task.IsCompleted);
    }
}
