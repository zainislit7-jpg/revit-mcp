using System.Windows.Forms;
using AcadMcp.Core;

namespace AcadMcp.Addin;

/// <summary>
/// Runs work on AutoCAD's UI thread (the AutoCAD API is single-threaded).
/// A hidden control created on that thread marshals calls through the Windows message loop,
/// which keeps working while AutoCAD sits idle at the command prompt.
/// </summary>
sealed class MainThread
{
    readonly Control _control = new();

    public MainThread() => _ = _control.Handle; // forces handle creation on the current (UI) thread

    public async Task<CallReply> Run(Func<CallReply> work, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<CallReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = 0;
        _control.BeginInvoke(() =>
        {
            // If the bridge already gave up, do nothing: a late write would surprise the user.
            if (Interlocked.CompareExchange(ref abandoned, 0, 0) == 1) return;
            try { tcs.TrySetResult(work()); }
            catch (System.Exception e) { tcs.TrySetResult(CallReply.Fail("Error: " + e.Message)); }
        });
        try
        {
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref abandoned, 1);
            return CallReply.Fail("Error: AutoCAD is busy (a command or dialog is open). Press Esc in AutoCAD and retry.");
        }
    }
}
