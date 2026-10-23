using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin;

/// <summary>Runs work on Revit's API thread. Must be constructed inside an API context (OnStartup).</summary>
public sealed class Dispatcher : IExternalEventHandler
{
    private readonly ConcurrentQueue<(Func<UIApplication, CallResponse> Work, TaskCompletionSource<CallResponse> Done)> _queue = new();
    private readonly ExternalEvent _event;

    public Dispatcher() => _event = ExternalEvent.Create(this);

    public async Task<CallResponse> Run(Func<UIApplication, CallResponse> work, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<CallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((work, done));
        var raised = _event.Raise();
        if (raised is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut)
            done.TrySetResult(CallResponse.Fail("Error: Revit refused the request (" + raised + "). Try again."));

        var finished = await Task.WhenAny(done.Task, Task.Delay(timeout));
        if (finished != done.Task)
        {
            // Completing the task here also stops Execute from running the work later,
            // so a timed-out modify can never land after the caller gave up.
            done.TrySetResult(CallResponse.Fail(
                "Error: Revit did not get to the request in time. Close any open dialog or finish the current command, then retry."));
        }
        return await done.Task;
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var item))
        {
            if (item.Done.Task.IsCompleted) continue;
            try
            {
                item.Done.TrySetResult(item.Work(app));
            }
            catch (Exception ex)
            {
                item.Done.TrySetResult(CallResponse.Fail("Error: " + ex.Message));
            }
        }
    }

    public string GetName() => "Claude MCP";
}
