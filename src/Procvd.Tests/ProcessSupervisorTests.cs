// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Configuration;
using Procvd.Runtime;

namespace Procvd.Tests;

public class ProcessSupervisorTests
{
    [Test]
    public async Task DependencyRestart_RestartsDependents()
    {
        var output = new TestOutputSink();
        var executor = new TestProcessExecutor();
        var coreKey = new ProcessKey("core", "core");
        var apiKey = new ProcessKey("api", "api");

        executor.EnqueueExit(coreKey, 1);

        var coreGroup = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Group,
            new ProcessRestartPolicy(),
            [],
            [
                CreateProcess(coreKey),
            ]);

        var apiGroup = new ResolvedProcessGroup(
            "api",
            GroupRestartMode.Group,
            new ProcessRestartPolicy(),
            ["core"],
            [
                CreateProcess(apiKey),
            ]);

        var config = new ResolvedProcessConfig(
            "/",
            new Dictionary<string, ResolvedProcessGroup>
            {
                ["core"] = coreGroup,
                ["api"] = apiGroup,
            });

        var supervisor = new ProcessSupervisor(
            config,
            new ProcessSupervisorOptions
            {
                Executor = executor,
                Output = output,
            });

        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = supervisor.RunAsync(runToken);

        try
        {
            await TestHelpers.WaitUntilAsync(() => executor.GetRunCount(apiKey) >= 2, TimeSpan.FromSeconds(1));
        }
        finally
        {
            runToken.Cancel();
            await runTask;
        }
    }

    private static ResolvedProcess CreateProcess(ProcessKey key) => new(
        key,
        $"/bin/{key.ProcessName}",
        key.ProcessName,
        "/",
        [],
        new Dictionary<string, string?>(),
        null,
        ProcessOutputMode.Inherit,
        null,
        0,
        0,
        null);
}
