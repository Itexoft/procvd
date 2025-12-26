// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Collections.Concurrent;
using Itexoft.Threading.Tasks;
using Procvd.Output;
using Procvd.Runtime;

namespace Procvd.Tests;

public sealed class TestProcessExecutor : IProcessExecutor
{
    private readonly ConcurrentDictionary<ProcessKey, Queue<int>> exitCodes = new();
    private readonly ConcurrentDictionary<ProcessKey, int> runCounts = new();

    public async Promise<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, IProcessOutputSink output, CancelToken cancelToken = default)
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
            return new ProcessExecutionResult(exitCode.Value, false, null);

        await (ValuePromise)cancelToken;
        return new ProcessExecutionResult(null, true, null);
    }

    public int GetRunCount(ProcessKey key) => this.runCounts.GetValueOrDefault(key, 0);

    public void EnqueueExit(ProcessKey key, int exitCode)
    {
        var queue = this.exitCodes.GetOrAdd(key, _ => new Queue<int>());

        lock (queue)
            queue.Enqueue(exitCode);
    }
}
