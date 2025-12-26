// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Itexoft.Threading;
using Procvd.Configuration;
using Procvd.Output;

namespace Procvd.Runtime;

public sealed class ProcessSupervisor
{
    private readonly IReadOnlyDictionary<string, ProcessGroupSupervisor> groups;
    private readonly ProcessDependencyGraph graph;

    public ProcessSupervisor(ResolvedProcessConfig config, ProcessSupervisorOptions? options = null)
    {
        options ??= new ProcessSupervisorOptions();

        var executor = options.Executor ?? new ProcessRunnerExecutor();
        var output = options.Output ?? new ProcessConsoleOutputSink();

        this.groups = config.Groups.ToDictionary(
            x => x.Key,
            x => new ProcessGroupSupervisor(x.Value, executor, output),
            StringComparer.Ordinal);

        this.graph = ProcessDependencyGraph.Build(config);

        foreach (var group in this.groups.Values)
            group.Restarting += this.HandleGroupRestarting;
    }

    public async Task RunAsync(CancelToken token = default)
    {
        var tasks = new List<Task>(this.groups.Count);

        foreach (var groupName in this.graph.StartOrder)
        {
            if (!this.groups.TryGetValue(groupName, out var supervisor))
                continue;

            tasks.Add(supervisor.RunAsync(token));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void HandleGroupRestarting(ProcessGroupRestartEvent message)
    {
        if (!this.graph.Dependents.TryGetValue(message.GroupName, out var dependents))
            return;

        foreach (var dependent in dependents)
        {
            if (!this.groups.TryGetValue(dependent, out var supervisor))
                continue;

            Ignore(supervisor.RequestRestartAsync());
        }
    }

    private static void Ignore(Task task) => task.ContinueWith(
        _ => { },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

public sealed class ProcessSupervisorOptions
{
    public IProcessExecutor? Executor { get; init; }

    public IProcessOutputSink? Output { get; init; }
}
