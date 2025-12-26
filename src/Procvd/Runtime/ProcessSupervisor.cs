// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Itexoft.Threading;
using Itexoft.Threading.Tasks;
using Procvd.Configuration;
using Procvd.Output;

namespace Procvd.Runtime;

public sealed class ProcessSupervisor
{
    private readonly ProcessDependencyGraph graph;
    private readonly IReadOnlyDictionary<string, ProcessGroupSupervisor> groups;
    private readonly ProcessRunnerExecutor? runnerExecutor;

    public ProcessSupervisor(ResolvedProcessConfig config, ProcessSupervisorOptions? options = null)
    {
        options ??= new ProcessSupervisorOptions();

        var executor = options.Executor ?? new ProcessRunnerExecutor();
        var output = options.Output ?? new ProcessConsoleOutputSink();

        this.runnerExecutor = executor as ProcessRunnerExecutor;
        this.groups = config.Groups.ToDictionary(x => x.Key, x => new ProcessGroupSupervisor(x.Value, executor, output), StringComparer.Ordinal);

        this.graph = ProcessDependencyGraph.Build(config);

        foreach (var group in this.groups.Values)
            group.Restarting += this.HandleGroupRestarting;
    }

    public void KillAllRunningProcesses()
    {
        if (this.runnerExecutor is null)
            throw new InvalidOperationException("Process runner executor is not available.");

        this.runnerExecutor.KillAll();
    }

    public async Promise RunAsync(CancelToken token = default)
    {
        var tasks = new List<Promise>(this.groups.Count);

        foreach (var groupName in this.graph.StartOrder)
        {
            if (!this.groups.TryGetValue(groupName, out var supervisor))
                continue;

            tasks.Add(Promise.RunAsync(() => supervisor.RunAsync(token)));
        }

        if (tasks.Count == 0)
            return;

        await Promise.WhenAll(tasks);
    }

    private void HandleGroupRestarting(ProcessGroupRestartEvent message)
    {
        if (!this.graph.Dependents.TryGetValue(message.GroupName, out var dependents))
            return;

        foreach (var dependent in dependents)
        {
            if (!this.groups.TryGetValue(dependent, out var supervisor))
                continue;

            supervisor.RequestRestartAsync().GetAwaiter().GetResult();
        }
    }
}

public sealed class ProcessSupervisorOptions
{
    public IProcessExecutor? Executor { get; init; }

    public IProcessOutputSink? Output { get; init; }
}
