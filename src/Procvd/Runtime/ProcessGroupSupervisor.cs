// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Itexoft.Threading;
using Itexoft.Threading.Core;
using Procvd.Configuration;
using Procvd.Output;

namespace Procvd.Runtime;

public sealed class ProcessGroupSupervisor(ResolvedProcessGroup group, IProcessExecutor executor, IProcessOutputSink output)
{
    private readonly AsyncLock restartLock = new();
    private CancelToken? runToken;
    private bool restartRequested;
    private int groupRestartCount;

    public event Action<ProcessGroupRestartEvent>? Restarting;

    public async Task RunAsync(CancelToken token = default)
    {
        while (!token.IsRequested)
        {
            var runToken = token.Branch();
            await this.SetRunTokenAsync(runToken);

            ProcessGroupRestartReason? reason;

            try
            {
                using var stopBridge = token.Bridge(out var stopCancellationToken);
                using var stopRegistration = stopCancellationToken.UnsafeRegister(
                    static state => ((CancelToken)state!).Cancel(),
                    runToken);

                reason = group.RestartMode == GroupRestartMode.Group
                    ? await this.RunGroupModeAsync(token, runToken)
                    : await this.RunProcessModeAsync(token, runToken);
            }
            catch (OperationCanceledException)
            {
                runToken.Cancel();
                await this.ClearRunTokenAsync(runToken);
                break;
            }

            await this.ClearRunTokenAsync(runToken);

            if (token.IsRequested || reason is null)
                break;

            if (!this.TryRegisterGroupRestart())
            {
                output.WriteEvent(new ProcessOutputEvent(
                    new ProcessKey(group.Name, "group"),
                    group.Name,
                    ProcessOutputEventKind.Failed,
                    DateTimeOffset.UtcNow,
                    null,
                    "restart limit reached"));

                break;
            }

            output.WriteEvent(new ProcessOutputEvent(
                new ProcessKey(group.Name, "group"),
                group.Name,
                ProcessOutputEventKind.Restarting,
                DateTimeOffset.UtcNow));

            this.Restarting?.Invoke(new ProcessGroupRestartEvent(group.Name, reason.Value));

            await this.DelayRestartAsync(token);
        }
    }

    public async Task RequestRestartAsync()
    {
        await using var enter = await this.restartLock.EnterAsync();

        if (this.runToken is null)
        {
            this.restartRequested = true;
            return;
        }

        var token = this.runToken.Value;

        if (!token.IsRequested)
            token.Cancel();
    }

    private async Task SetRunTokenAsync(CancelToken token)
    {
        await using var enter = await this.restartLock.EnterAsync();
        this.runToken = token;

        if (this.restartRequested)
        {
            this.restartRequested = false;
            token.Cancel();
        }
    }

    private async Task ClearRunTokenAsync(CancelToken token)
    {
        await using var enter = await this.restartLock.EnterAsync();

        if (this.runToken is { } current && current == token)
            this.runToken = null;
    }

    private async Task<ProcessGroupRestartReason?> RunGroupModeAsync(CancelToken stopToken, CancelToken runToken)
    {
        var tasks = new List<Task<ProcessExecutionResult>>();

        try
        {
            foreach (var process in group.Processes)
                tasks.Add(this.ExecuteProcessOnceAsync(process, runToken));

            ProcessGroupRestartReason? reason = null;

            while (tasks.Count != 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                var result = await completed.ConfigureAwait(false);

                if (stopToken.IsRequested)
                    return null;

                if (!result.IsCancelled)
                {
                    reason = ProcessGroupRestartReason.ProcessExit;
                    runToken.Cancel();
                    break;
                }

                if (runToken.IsRequested)
                {
                    reason = ProcessGroupRestartReason.ExternalRequest;
                    break;
                }

                tasks.Remove(completed);
            }

            if (stopToken.IsRequested)
                return null;

            return reason ?? ProcessGroupRestartReason.ExternalRequest;
        }
        finally
        {
            runToken.Cancel();

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // ignore process shutdown failures
            }
        }
    }

    private async Task<ProcessGroupRestartReason?> RunProcessModeAsync(CancelToken stopToken, CancelToken runToken)
    {
        var tasks = new List<Task>();

        try
        {
            foreach (var process in group.Processes)
                tasks.Add(this.RunProcessLoopAsync(process, runToken));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // ignore process loop failures
        }

        if (stopToken.IsRequested)
            return null;

        return runToken.IsRequested ? ProcessGroupRestartReason.ExternalRequest : null;
    }

    private async Task RunProcessLoopAsync(ResolvedProcess process, CancelToken token)
    {
        var restarts = 0;

        while (!token.IsRequested)
        {
            var result = await this.ExecuteProcessOnceAsync(process, token);

            if (token.IsRequested || result.IsCancelled)
                break;

            if (!this.TryRegisterProcessRestart(ref restarts))
            {
                output.WriteEvent(new ProcessOutputEvent(
                    process.Key,
                    process.DisplayPath,
                    ProcessOutputEventKind.Failed,
                    DateTimeOffset.UtcNow,
                    null,
                    "restart limit reached"));

                break;
            }

            await this.DelayRestartAsync(token);
        }
    }

    private async Task<ProcessExecutionResult> ExecuteProcessOnceAsync(ResolvedProcess process, CancelToken token)
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
            process.OutputMaxFiles);

        return await executor.RunAsync(request, output, token);
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

    private async Task DelayRestartAsync(CancelToken token)
    {
        var delay = group.RestartPolicy.RestartDelay;

        if (delay <= TimeSpan.Zero)
            return;

        using (token.Bridge(out var cancellationToken))
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore cancellation during restart delay
            }
        }
    }
}
