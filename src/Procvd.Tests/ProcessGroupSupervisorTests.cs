// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Collections.Concurrent;
using System.Diagnostics;
using Itexoft.Threading.Tasks;
using Procvd.Configuration;
using Procvd.Output;
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
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(processA),
                CreateProcess(processB),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = supervisor.RunAsync(runToken);

        try
        {
            await TestHelpers.WaitUntilAsync(() => executor.GetRunCount(processB) >= 2, TimeSpan.FromSeconds(1));
        }
        finally
        {
            runToken.Cancel();
            await runTask;
        }
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
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(processA),
                CreateProcess(processB),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = supervisor.RunAsync(runToken);

        try
        {
            await TestHelpers.WaitUntilAsync(() => executor.GetRunCount(processA) >= 2, TimeSpan.FromSeconds(1));

            Assert.That(executor.GetRunCount(processB), Is.EqualTo(1));
        }
        finally
        {
            runToken.Cancel();
            await runTask;
        }
    }

    [Test]
    public async Task ProcessMode_StartsProcessesInParallel()
    {
        var output = new TestOutputSink();
        var executor = new SynchronousDelayProcessExecutor(TimeSpan.FromMilliseconds(150));
        var processA = new ProcessKey("core", "a");
        var processB = new ProcessKey("core", "b");
        var processC = new ProcessKey("core", "c");

        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 0,
            },
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(processA),
                CreateProcess(processB),
                CreateProcess(processC),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = Task.Run(async () => await supervisor.RunAsync(runToken));
        var timer = Stopwatch.StartNew();

        try
        {
            await TestHelpers.WaitUntilAsync(() => executor.StartedCount >= 3, TimeSpan.FromSeconds(1));

            timer.Stop();

            Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            runToken.Cancel();
            await runTask;
        }
    }

    [Test]
    public async Task ProcessMode_PassesRunAsUser()
    {
        var output = new TestOutputSink();
        var executor = new CaptureRunAsExecutor();
        var processKey = new ProcessKey("core", "app");
        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 0,
            },
            new List<string>(),
            new List<ResolvedProcess>
            {
                new(
                    processKey,
                    "/bin/app",
                    "app",
                    "/",
                    new List<string>(),
                    new Dictionary<string, string?>(),
                    null,
                    ProcessOutputMode.Inherit,
                    null,
                    0,
                    0,
                    "router"),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        await supervisor.RunAsync();

        Assert.That(executor.LastRunAsUser, Is.EqualTo("router"));
    }

    [Test]
    public async Task ProcessMode_DoesNotRestartFaultedProcess()
    {
        var output = new TestOutputSink();
        var executor = new FaultedProcessExecutor();
        var processKey = new ProcessKey("core", "faulted");
        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Process,
            new ProcessRestartPolicy(),
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(processKey),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runTask = supervisor.RunAsync();

        await executor.RunSignal.WaitAsync(TimeSpan.FromSeconds(2));
        await runTask;

        Assert.That(executor.RunCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessMode_DoesNotRestartAfterStopRequestDuringRestartDelay()
    {
        var output = new TestOutputSink();
        var executor = new ImmediateExitProcessExecutor();
        var processKey = new ProcessKey("core", "delay-stop");
        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                RestartDelay = TimeSpan.FromSeconds(5),
            },
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(processKey),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.New();
        var runTask = supervisor.RunAsync(runToken);

        await executor.FirstRunSignal.WaitAsync(TimeSpan.FromSeconds(2));
        runToken.Cancel();
        await runTask;

        Assert.That(executor.RunCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GroupMode_RequestRestart_RestartsRunningGroup()
    {
        var output = new TestOutputSink();
        var executor = new TestProcessExecutor();
        var processKey = new ProcessKey("core", "group-restart");
        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Group,
            new ProcessRestartPolicy(),
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(processKey),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = supervisor.RunAsync(runToken);

        try
        {
            await TestHelpers.WaitUntilAsync(() => executor.GetRunCount(processKey) >= 1, TimeSpan.FromSeconds(1));

            await supervisor.RequestRestartAsync();

            await TestHelpers.WaitUntilAsync(() => executor.GetRunCount(processKey) >= 2, TimeSpan.FromSeconds(1));
        }
        finally
        {
            runToken.Cancel();
            await runTask;
        }
    }

    [Test]
    public async Task ProcessMode_RequestRestartDuringRestartDelay_RestartsImmediately()
    {
        var output = new TestOutputSink();
        var executor = new ImmediateExitProcessExecutor();
        var processKey = new ProcessKey("core", "delay-restart");
        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                RestartDelay = TimeSpan.FromSeconds(5),
            },
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(processKey),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.None.Branch(TimeSpan.FromSeconds(2));
        var runTask = supervisor.RunAsync(runToken);

        try
        {
            await executor.FirstRunSignal.WaitAsync(TimeSpan.FromSeconds(2));

            await supervisor.RequestRestartAsync();

            await TestHelpers.WaitUntilAsync(() => executor.RunCount >= 2, TimeSpan.FromSeconds(1));
        }
        finally
        {
            runToken.Cancel();
            await runTask;
        }
    }

    [Test]
    public async Task ProcessMode_EmitsLiveOutputWhileSiblingRestarts_WithTestExecutor()
    {
        var output = new TestOutputSink();
        var executor = new OutputAndExitProcessExecutor();
        var noisyKey = new ProcessKey("core", "noisy");
        var flappingKey = new ProcessKey("core", "flapping");
        var group = new ResolvedProcessGroup(
            "core",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 2,
                RestartDelay = TimeSpan.FromMilliseconds(10),
            },
            new List<string>(),
            new List<ResolvedProcess>
            {
                CreateProcess(noisyKey),
                CreateProcess(flappingKey),
            });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var runToken = CancelToken.New();
        var runTask = supervisor.RunAsync(runToken);

        try
        {
            await TestHelpers.WaitUntilAsync(
                () => output.HasLine(line => line.Process == noisyKey && line.Line.Contains("live-output", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(1));

            await TestHelpers.WaitUntilAsync(
                () => executor.GetRunCount(flappingKey) >= 2,
                TimeSpan.FromSeconds(1));
        }
        finally
        {
            runToken.Cancel();
            await runTask;
        }

        Assert.That(output.HasLine(line => line.Process == noisyKey && line.Line.Contains("live-output", StringComparison.Ordinal)), Is.True);
        Assert.That(executor.GetRunCount(flappingKey), Is.GreaterThanOrEqualTo(2));
    }

    private static ResolvedProcess CreateProcess(ProcessKey key) => new(
        key,
        $"/bin/{key.ProcessName}",
        key.ProcessName,
        "/",
        new List<string>(),
        new Dictionary<string, string?>(),
        null,
        ProcessOutputMode.Inherit,
        null,
        0,
        0,
        null);

    private sealed class SynchronousDelayProcessExecutor(TimeSpan delay) : IProcessExecutor
    {
        private readonly object sync = new();
        private int started;

        public int StartedCount
        {
            get
            {
                lock (this.sync)
                    return this.started;
            }
        }

        public Promise<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, IProcessOutputSink output, CancelToken cancelToken = default)
        {
            lock (this.sync)
                this.started++;

            Thread.Sleep(delay);

            return Task.FromResult(new ProcessExecutionResult(0, false, null));
        }
    }

    private sealed class CaptureRunAsExecutor : IProcessExecutor
    {
        public string? LastRunAsUser { get; private set; }

        public Promise<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, IProcessOutputSink output, CancelToken cancelToken = default)
        {
            this.LastRunAsUser = request.RunAsUser;

            return Task.FromResult(new ProcessExecutionResult(0, false, null));
        }
    }

    private sealed class FaultedProcessExecutor : IProcessExecutor
    {
        private readonly TaskCompletionSource<bool> runSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int runCount;

        public Task RunSignal => this.runSignal.Task;

        public int RunCount => Volatile.Read(ref this.runCount);

        public Promise<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, IProcessOutputSink output, CancelToken cancelToken = default)
        {
            Interlocked.Increment(ref this.runCount);
            this.runSignal.TrySetResult(true);

            return Task.FromResult(new ProcessExecutionResult(null, false, new InvalidOperationException("boom")));
        }
    }

    private sealed class ImmediateExitProcessExecutor : IProcessExecutor
    {
        private readonly TaskCompletionSource<bool> firstRunSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int runCount;

        public Task FirstRunSignal => this.firstRunSignal.Task;

        public int RunCount => Volatile.Read(ref this.runCount);

        public Promise<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, IProcessOutputSink output, CancelToken cancelToken = default)
        {
            if (Interlocked.Increment(ref this.runCount) == 1)
                this.firstRunSignal.TrySetResult(true);

            return Task.FromResult(new ProcessExecutionResult(1, false, null));
        }
    }

    private sealed class OutputAndExitProcessExecutor : IProcessExecutor
    {
        private readonly ConcurrentDictionary<ProcessKey, int> runCounts = new();

        public async Promise<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, IProcessOutputSink output, CancelToken cancelToken = default)
        {
            var count = this.runCounts.AddOrUpdate(request.Process, 1, (_, current) => current + 1);

            if (request.Process.ProcessName == "noisy")
            {
                output.Write(new ProcessOutputLine(request.Process, request.DisplayPath, ProcessOutputStream.StdOut, "live-output", DateTimeOffset.UtcNow));
                await (ValuePromise)cancelToken;

                return new ProcessExecutionResult(null, true, null);
            }

            output.Write(new ProcessOutputLine(request.Process, request.DisplayPath, ProcessOutputStream.StdErr, "restart-output", DateTimeOffset.UtcNow));

            return new ProcessExecutionResult(1, false, null);
        }

        public int GetRunCount(ProcessKey key) => this.runCounts.GetValueOrDefault(key, 0);
    }
}
