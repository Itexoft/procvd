// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Configuration;
using Procvd.Runtime;

namespace Procvd.Tests;

public class ProcessGroupSupervisorTests
{
    [Test]
    public async Task GroupMode_RestartsAllProcesses()
    {
        var output = new TestOutputSink();
        var executor = new TestProcessExecutor();
        var processA = new ProcessKey("core", "a");
        var processB = new ProcessKey("core", "b");

        executor.EnqueueExit(processA, 1);

        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Group,
            new ProcessRestartPolicy(),
            Array.Empty<string>(),
            new[]
            {
                CreateProcess(processA),
                CreateProcess(processB),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = supervisor.RunAsync(runToken);

        await TestHelpers.WaitUntilAsync(() => executor.GetRunCount(processB) >= 2, TimeSpan.FromSeconds(1));

        runToken.Cancel();
        await runTask;
    }

    [Test]
    public async Task ProcessMode_RestartsOnlyProcess()
    {
        var output = new TestOutputSink();
        var executor = new TestProcessExecutor();
        var processA = new ProcessKey("core", "a");
        var processB = new ProcessKey("core", "b");

        executor.EnqueueExit(processA, 1);

        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Process,
            new ProcessRestartPolicy(),
            Array.Empty<string>(),
            new[]
            {
                CreateProcess(processA),
                CreateProcess(processB),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = supervisor.RunAsync(runToken);

        await TestHelpers.WaitUntilAsync(() => executor.GetRunCount(processA) >= 2, TimeSpan.FromSeconds(1));

        Assert.That(executor.GetRunCount(processB), Is.EqualTo(1));

        runToken.Cancel();
        await runTask;
    }

    private static ResolvedProcess CreateProcess(ProcessKey key) => new(
        key,
        $"/bin/{key.ProcessName}",
        key.ProcessName,
        "/",
        Array.Empty<string>(),
        new Dictionary<string, string?>(),
        null,
        ProcessOutputMode.Inherit,
        null,
        0,
        0);
}
