// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Collections.Concurrent;
using Itexoft.Threading;
using Procvd.Output;
using Procvd.Runtime;

namespace Procvd.Tests;

public sealed class TestProcessExecutor : IProcessExecutor
{
    private readonly ConcurrentDictionary<ProcessKey, Queue<int>> exitCodes = new();
    private readonly ConcurrentDictionary<ProcessKey, int> runCounts = new();

    public int GetRunCount(ProcessKey key) => this.runCounts.GetValueOrDefault(key, 0);

    public void EnqueueExit(ProcessKey key, int exitCode)
    {
        var queue = this.exitCodes.GetOrAdd(key, _ => new Queue<int>());

        lock (queue)
            queue.Enqueue(exitCode);
    }

    public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, IProcessOutputSink output, CancelToken token = default)
    {
        var key = request.Process;
        this.runCounts.AddOrUpdate(key, 1, (_, count) => count + 1);

        var queue = this.exitCodes.GetOrAdd(key, _ => new Queue<int>());
        int? exitCode = null;

        lock (queue)
        {
            if (queue.Count > 0)
                exitCode = queue.Dequeue();
        }

        if (exitCode.HasValue)
            return Task.FromResult(new ProcessExecutionResult(exitCode.Value, false, null));

        if (token.IsRequested)
            return Task.FromResult(new ProcessExecutionResult(null, true, null));

        var tcs = new TaskCompletionSource<ProcessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = WaitForCancelAsync(token, tcs);

        return tcs.Task;
    }

    private static async Task WaitForCancelAsync(CancelToken token, TaskCompletionSource<ProcessExecutionResult> tcs)
    {
        if (token.IsNone)
            return;

        while (!token.IsRequested)
            await Task.Delay(10).ConfigureAwait(false);

        tcs.TrySetResult(new ProcessExecutionResult(null, true, null));
    }
}
