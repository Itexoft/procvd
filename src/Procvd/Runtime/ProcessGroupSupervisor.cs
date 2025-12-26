// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Itexoft.Threading;
using Itexoft.Threading.Tasks;
using Procvd.Configuration;
using Procvd.Output;

namespace Procvd.Runtime;

public sealed class ProcessGroupSupervisor(ResolvedProcessGroup group, IProcessExecutor executor, IProcessOutputSink output)
{
    private readonly Lock stateSync = new();
    private int groupRestartCount;
    private bool restartDelayActive;
    private bool restartRequested;
    private TaskCompletionSource<bool> restartSignal = CreateRestartSignal();
    private CancelToken? runToken;

    public event Action<ProcessGroupRestartEvent>? Restarting;

    public async Promise RunAsync(CancelToken token = default)
    {
        while (!token.IsRequested)
        {
            var runToken = CancelToken.New();
            this.BeginRun(runToken);

            ProcessGroupRestartReason? reason = null;

            try
            {
                using var stopBridge = token.Bridge(out var stopCancellationToken);
                await using var stopRegistration = stopCancellationToken.UnsafeRegister(static state => ((CancelToken)state!).Cancel(), runToken);

                reason = group.RestartMode == GroupRestartMode.Group
                    ? await this.RunGroupModeAsync(token, runToken)
                    : await this.RunProcessModeAsync(token, runToken);
            }
            finally
            {
                this.EndRun(runToken);
            }

            if (token.IsRequested)
                break;

            if (reason == ProcessGroupRestartReason.ExternalRequest)
                this.TryConsumeRestartRequest();
            else if (reason is null && this.TryConsumeRestartRequest())
                reason = ProcessGroupRestartReason.ExternalRequest;

            if (reason is null)
                break;

            if (!this.TryRegisterGroupRestart())
            {
                output.WriteEvent(
                    new ProcessOutputEvent(
                        new ProcessKey(group.Name, "group"),
                        group.Name,
                        ProcessOutputEventKind.Failed,
                        DateTimeOffset.UtcNow,
                        null,
                        "restart limit reached"));

                break;
            }

            output.WriteEvent(
                new ProcessOutputEvent(new ProcessKey(group.Name, "group"), group.Name, ProcessOutputEventKind.Restarting, DateTimeOffset.UtcNow));

            this.Restarting?.Invoke(new ProcessGroupRestartEvent(group.Name, reason.Value));

            if (reason != ProcessGroupRestartReason.ExternalRequest)
            {
                this.SetRestartDelayActive(true);

                try
                {
                    await this.DelayRestartAsync(token);
                }
                finally
                {
                    this.SetRestartDelayActive(false);
                }
            }
        }
    }

    public Promise RequestRestartAsync()
    {
        TaskCompletionSource<bool> restartSignal;
        CancelToken? tokenToCancel;

        lock (this.stateSync)
        {
            restartSignal = this.restartSignal;

            if (this.runToken is { } current && !current.IsRequested)
            {
                this.restartRequested = true;
                tokenToCancel = current;
            }
            else
            {
                tokenToCancel = null;

                if (!this.restartDelayActive)
                    this.restartRequested = true;
            }
        }

        restartSignal.TrySetResult(true);
        tokenToCancel?.Cancel();

        return Promise.Completed;
    }

    private void BeginRun(CancelToken token)
    {
        lock (this.stateSync)
        {
            this.runToken = token;

            if (!this.restartRequested)
            {
                if (this.restartSignal.Task.IsCompleted)
                    this.restartSignal = CreateRestartSignal();

                return;
            }

            token.Cancel();
        }
    }

    private void EndRun(CancelToken token)
    {
        lock (this.stateSync)
        {
            if (this.runToken is { } current && current == token)
                this.runToken = null;
        }
    }

    private async Promise<ProcessGroupRestartReason?> RunGroupModeAsync(CancelToken stopToken, CancelToken runToken)
    {
        var tasks = new List<Promise<ProcessExecutionResult>>();
        var restartSignal = this.GetRestartSignalTask();

        try
        {
            foreach (var process in group.Processes)
                tasks.Add(this.ExecuteProcessOnceAsync(process, stopToken, runToken));

            while (tasks.Count != 0)
            {
                var completed = FindCompletedTask(tasks);

                if (completed is not null)
                {
                    var result = await completed;

                    if (stopToken.IsRequested)
                        return null;

                    if (!result.IsCancelled)
                    {
                        runToken.Cancel();

                        return ProcessGroupRestartReason.ProcessExit;
                    }

                    tasks.Remove(completed);

                    continue;
                }

                if (restartSignal.IsCompleted)
                    return stopToken.IsRequested ? null : ProcessGroupRestartReason.ExternalRequest;

                stopToken.ThrowIf();
                await Promise.Delay(16, stopToken);
            }

            return this.IsRestartRequested() ? ProcessGroupRestartReason.ExternalRequest : null;
        }
        catch (OperationCanceledException) when (stopToken.IsRequested)
        {
            return null;
        }
        finally
        {
            runToken.Cancel();

            try
            {
                await Promise.WhenAll(tasks);
            }
            catch
            {
                // ignore process shutdown failures
            }
        }
    }

    private async Promise<ProcessGroupRestartReason?> RunProcessModeAsync(CancelToken stopToken, CancelToken runToken)
    {
        var tasks = new List<Promise>();
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStartBarrier = new ProcessStartBarrier(group.Processes.Count);

        try
        {
            foreach (var process in group.Processes)
                tasks.Add(this.RunProcessLoopAsync(process, stopToken, runToken, startGate.Task, firstStartBarrier));

            startGate.TrySetResult(true);

            await Promise.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stopToken.IsRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (this.IsRestartRequested())
        {
            return ProcessGroupRestartReason.ExternalRequest;
        }
        catch (Exception ex)
        {
            output.WriteEvent(
                new ProcessOutputEvent(
                    new ProcessKey(group.Name, "group"),
                    group.Name,
                    ProcessOutputEventKind.Failed,
                    DateTimeOffset.UtcNow,
                    null,
                    $"process loop failed: {ex.Message}"));

            throw;
        }
        finally
        {
            startGate.TrySetResult(true);
        }

        if (stopToken.IsRequested)
            return null;

        return this.IsRestartRequested() ? ProcessGroupRestartReason.ExternalRequest : null;
    }

    private async Promise RunProcessLoopAsync(
        ResolvedProcess process,
        CancelToken stopToken,
        CancelToken runToken,
        Promise startGate,
        ProcessStartBarrier firstStartBarrier)
    {
        await startGate;

        var firstRun = true;
        var restarts = 0;

        while (!runToken.IsRequested && !stopToken.IsRequested)
        {
            ProcessExecutionResult result;

            try
            {
                if (firstRun)
                    firstStartBarrier.Arrive();

                result = await this.ExecuteProcessOnceAsync(process, stopToken, runToken);
            }
            catch (OperationCanceledException) when (stopToken.IsRequested || runToken.IsRequested)
            {
                break;
            }

            if (stopToken.IsRequested || runToken.IsRequested || result.IsCancelled)
                break;

            if (result.IsFaulted)
                break;

            if (firstRun)
            {
                firstRun = false;

                if (!firstStartBarrier.Completion.IsCompleted)
                {
                    using var stopBridge = stopToken.Bridge(out var stopCancellationToken);
                    using var runBridge = runToken.Bridge(out var runCancellationToken);
                    using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(stopCancellationToken, runCancellationToken);

                    try
                    {
                        await firstStartBarrier.Completion.WaitAsync(linkedSource.Token);
                    }
                    catch (OperationCanceledException) when (stopToken.IsRequested || runToken.IsRequested)
                    {
                        break;
                    }
                }
            }

            if (!this.TryRegisterProcessRestart(ref restarts))
            {
                output.WriteEvent(
                    new ProcessOutputEvent(
                        process.Key,
                        process.DisplayPath,
                        ProcessOutputEventKind.Failed,
                        DateTimeOffset.UtcNow,
                        null,
                        "restart limit reached"));

                break;
            }

            try
            {
                await this.DelayRestartAsync(stopToken, runToken);
            }
            catch (OperationCanceledException) when (stopToken.IsRequested)
            {
                break;
            }
        }
    }

    private async Promise<ProcessExecutionResult> ExecuteProcessOnceAsync(ResolvedProcess process, CancelToken stopToken, CancelToken runToken)
    {
        var request = new ProcessExecutionRequest(
            process.Key,
            process.ExecutablePath,
            process.DisplayPath,
            process.WorkingDirectory,
            process.Arguments,
            process.Environment,
            process.ShellCommand,
            process.OutputMode,
            process.OutputPath,
            process.OutputMaxBytes,
            process.OutputMaxFiles,
            process.RunAsUser);

        using var stopBridge = stopToken.Bridge(out var stopCancellationToken);
        using var runBridge = runToken.Bridge(out var runCancellationToken);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(stopCancellationToken, runCancellationToken);

        return await executor.RunAsync(request, output, linkedSource.Token);
    }

    private bool TryRegisterProcessRestart(ref int restarts)
    {
        var max = group.RestartPolicy.MaxRestarts;

        if (max.HasValue && restarts >= max.Value)
            return false;

        restarts++;

        return true;
    }

    private bool TryRegisterGroupRestart()
    {
        var max = group.RestartPolicy.MaxRestarts;

        if (this.groupRestartCount >= max)
            return false;

        this.groupRestartCount++;

        return true;
    }

    private async Promise DelayRestartAsync(CancelToken stopToken, CancelToken interruptToken = default)
    {
        var delay = group.RestartPolicy.RestartDelay;

        if (delay <= TimeSpan.Zero)
            return;

        if (stopToken.IsRequested || interruptToken.IsRequested)
            return;

        var restartSignal = this.GetRestartSignalTask();

        if (restartSignal.IsCompleted)
            return;

        using var stopBridge = stopToken.Bridge(out var stopCancellationToken);
        var delayTask = Task.Delay(delay, stopCancellationToken);
        Task? interruptTask = null;

        if (!interruptToken.IsNone)
        {
            var interruptSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            interruptToken.Register(() => interruptSignal.TrySetResult(true));
            interruptTask = interruptSignal.Task;

            if (interruptTask.IsCompleted)
                return;
        }

        var completed = interruptTask is null
            ? await Task.WhenAny(delayTask, restartSignal)
            : await Task.WhenAny(delayTask, restartSignal, interruptTask);

        if (completed == restartSignal || completed == interruptTask)
            return;

        try
        {
            await delayTask;
        }
        catch (OperationCanceledException) when (stopToken.IsRequested) { }
    }

    private static Promise<ProcessExecutionResult>? FindCompletedTask(List<Promise<ProcessExecutionResult>> tasks)
    {
        foreach (var task in tasks)
        {
            if (task.IsCompleted)
                return task;
        }

        return null;
    }

    private Task GetRestartSignalTask()
    {
        lock (this.stateSync)
            return this.restartSignal.Task;
    }

    private bool IsRestartRequested()
    {
        lock (this.stateSync)
            return this.restartRequested;
    }

    private bool TryConsumeRestartRequest()
    {
        lock (this.stateSync)
        {
            if (!this.restartRequested)
                return false;

            this.restartRequested = false;
            this.restartSignal = CreateRestartSignal();

            return true;
        }
    }

    private void SetRestartDelayActive(bool value)
    {
        lock (this.stateSync)
            this.restartDelayActive = value;
    }

    private sealed class ProcessStartBarrier
    {
        private int remaining;
        private readonly TaskCompletionSource<bool> completion;

        public ProcessStartBarrier(int count)
        {
            this.remaining = count;
            this.completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.Completion = count == 0 ? Task.CompletedTask : this.completion.Task;
        }

        public Task Completion { get; }

        public void Arrive()
        {
            if (this.Completion.IsCompleted)
                return;

            if (Interlocked.Decrement(ref this.remaining) != 0)
                return;

            this.completion.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> CreateRestartSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
