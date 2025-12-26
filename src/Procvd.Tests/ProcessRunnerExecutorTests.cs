// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Configuration;
using Procvd.Output;
using Procvd.Runtime;

namespace Procvd.Tests;

public class ProcessRunnerExecutorTests
{
    [Test]
    public async Task RunAsync_Inherit_EmitsLines()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var plan = BuildShellEchoPlan("inherit-test");

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "inherit"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        var result = await executor.RunAsync(request, output, token);

        Assert.That(result.IsCancelled, Is.False);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(output.Lines.Any(line => line.Line.Contains("inherit-test", StringComparison.Ordinal)), Is.True);
        Assert.That(output.Events.Any(e => e.Kind == ProcessOutputEventKind.Starting), Is.True);
        Assert.That(output.Events.Any(e => e.Kind == ProcessOutputEventKind.Exited), Is.True);
    }

    [Test]
    public async Task RunAsync_FileOutput_EmitsLines()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var logPath = Path.Combine(temp.Path, "logs", "main", "echo.log");

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "echo"),
            GetShellExecutable(),
            GetShellExecutable(),
            temp.Path,
            [],
            new Dictionary<string, string?>(),
            "echo file-test",
            ProcessOutputMode.File,
            logPath,
            1024 * 1024,
            3,
            null);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        var result = await executor.RunAsync(request, output, token);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(output.Lines.Any(line => line.Line.Contains("file-test", StringComparison.Ordinal)), Is.True);
        Assert.That(File.Exists(logPath), Is.True);
    }

    [Test]
    public async Task RunAsync_FileOutput_RotatesExistingLog()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var logPath = Path.Combine(temp.Path, "logs", "main", "rotate.log");
        var logDirectory = Path.GetDirectoryName(logPath)!;
        Directory.CreateDirectory(logDirectory);

        await File.WriteAllTextAsync(logPath, new string('x', 256));

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "rotate"),
            GetShellExecutable(),
            GetShellExecutable(),
            temp.Path,
            [],
            new Dictionary<string, string?>(),
            "echo rotate-test",
            ProcessOutputMode.File,
            logPath,
            64,
            2,
            null);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        await executor.RunAsync(request, output, token);

        var rotated = logPath + ".1";
        Assert.That(File.Exists(rotated), Is.True);
        Assert.That((await File.ReadAllTextAsync(logPath)).Contains("rotate-test", StringComparison.Ordinal), Is.True);
    }

    [Test]
    public async Task RunAsync_FileOutput_UsesDirectArguments()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var logPath = Path.Combine(temp.Path, "logs", "main", "direct.log");
        var plan = BuildDirectEchoPlan("direct-test");

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "direct"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.File,
            logPath,
            1024 * 1024,
            3,
            null);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        var result = await executor.RunAsync(request, output, token);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(output.Lines.Any(line => line.Line.Contains("direct-test", StringComparison.Ordinal)), Is.True);
        Assert.That(File.Exists(logPath), Is.True);
    }

    [Test]
    public async Task RunAsync_Inherit_EmitsLiveProcessLineBeforeExit()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var plan = BuildEchoThenSleepPlan("live-before-exit", 30);

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "live"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);

        var token = CancelToken.New();
        var runTask = executor.RunAsync(request, output, token);

        try
        {
            await TestHelpers.WaitUntilAsync(
                () => output.HasLine(line => line.Process == request.Process && line.Line.Contains("live-before-exit", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            token.Cancel();
            await runTask;
        }

        Assert.That(output.HasLine(line => line.Process == request.Process && line.Line.Contains("live-before-exit", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task RunAsync_ProcessMode_RestartsUntilLimit()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var plan = BuildExitPlan(1);

        var process = new ResolvedProcess(
            new ProcessKey("main", "fail"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);

        var group = new ResolvedProcessGroup(
            "main",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 2,
                RestartDelay = TimeSpan.FromMilliseconds(10),
            },
            [],
            [process]);

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        await supervisor.RunAsync();

        var exitedCount = output.Events.Count(e => e.Kind == ProcessOutputEventKind.Exited);
        var failedCount = output.Events.Count(e => e.Kind == ProcessOutputEventKind.Failed);

        Assert.That(exitedCount, Is.EqualTo(3));
        Assert.That(failedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RunAsync_ProcessMode_RestartsWhenExitedProcessLeavesOutputPipeOpen()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("POSIX-specific process group behavior");

        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var attemptsPath = Path.Combine(temp.Path, "attempts.txt");
        var plan = BuildExitPlanWithBackgroundChildHoldingOutputPipe(attemptsPath);
        var process = new ResolvedProcess(
            new ProcessKey("main", "pipe-holder"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);

        var group = new ResolvedProcessGroup(
            "main",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 2,
                RestartDelay = TimeSpan.FromMilliseconds(10),
            },
            [],
            [process]);

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        await supervisor.RunAsync(token);

        Assert.That(File.ReadAllText(attemptsPath).Trim(), Is.EqualTo("3"));
        Assert.That(output.Events.Count(e => e.Process == process.Key && e.Kind == ProcessOutputEventKind.Starting), Is.EqualTo(3));
        Assert.That(output.Events.Count(e => e.Process == process.Key && e.Kind == ProcessOutputEventKind.Exited), Is.EqualTo(3));
        Assert.That(output.Events.Count(e => e.Process == process.Key && e.Kind == ProcessOutputEventKind.Failed), Is.EqualTo(1));
    }

    [Test]
    public async Task RunAsync_ProcessMode_EmitsLiveProcessOutputWhileSiblingRestarts()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var noisyPlan = BuildEchoThenSleepPlan("live-process-output", 30);
        var flappingPlan = BuildStderrExitPlan("restarting-process-output", 1);
        var noisy = new ResolvedProcess(
            new ProcessKey("main", "noisy"),
            noisyPlan.ExecutablePath,
            noisyPlan.ExecutablePath,
            temp.Path,
            noisyPlan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);
        var flapping = new ResolvedProcess(
            new ProcessKey("main", "flapping"),
            flappingPlan.ExecutablePath,
            flappingPlan.ExecutablePath,
            temp.Path,
            flappingPlan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);

        var group = new ResolvedProcessGroup(
            "main",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 2,
                RestartDelay = TimeSpan.FromMilliseconds(10),
            },
            [],
            [noisy, flapping]);

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var token = CancelToken.New();
        var runTask = supervisor.RunAsync(token);
        var noisyVisible = false;
        var flappingRestarted = false;

        try
        {
            noisyVisible = await TestHelpers.WaitUntilOrTimeoutAsync(
                () => output.HasLine(line => line.Process == noisy.Key && line.Line.Contains("live-process-output", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(2));

            flappingRestarted = await TestHelpers.WaitUntilOrTimeoutAsync(
                () => output.CountEvents(e => e.Process == flapping.Key && e.Kind == ProcessOutputEventKind.Exited) >= 2,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            token.Cancel();
            await runTask;
        }

        var eventsDump = string.Join(
            " | ",
            output.Events.Select(e => $"{e.Process.ProcessName}:{e.Kind}:{e.ExitCode?.ToString() ?? "-"}:{e.Message ?? string.Empty}"));
        var linesDump = string.Join(
            " | ",
            output.Lines.Select(line => $"{line.Process.ProcessName}:{line.Stream}:{line.Line}"));

        Assert.That(noisyVisible, Is.True, $"Events: {eventsDump}{Environment.NewLine}Lines: {linesDump}");
        Assert.That(flappingRestarted, Is.True, $"Events: {eventsDump}{Environment.NewLine}Lines: {linesDump}");
        Assert.That(output.HasLine(line => line.Process == noisy.Key && line.Line.Contains("live-process-output", StringComparison.Ordinal)), Is.True, $"Events: {eventsDump}{Environment.NewLine}Lines: {linesDump}");
        Assert.That(output.HasLine(line => line.Process == flapping.Key && line.Line.Contains("restarting-process-output", StringComparison.Ordinal)), Is.True, $"Events: {eventsDump}{Environment.NewLine}Lines: {linesDump}");
    }

    [Test]
    public async Task RunAsync_ProcessMode_EmitsSiblingOutputWhileAnotherProcessKeepsUnterminatedLineOpen()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var blockedPlan = BuildPrintfWithoutNewlineThenSleepPlan("unterminated-output", 30);
        var noisyPlan = BuildEchoThenSleepPlan("sibling-visible-output", 30);
        var blocked = new ResolvedProcess(
            new ProcessKey("main", "blocked"),
            blockedPlan.ExecutablePath,
            blockedPlan.ExecutablePath,
            temp.Path,
            blockedPlan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);
        var noisy = new ResolvedProcess(
            new ProcessKey("main", "noisy"),
            noisyPlan.ExecutablePath,
            noisyPlan.ExecutablePath,
            temp.Path,
            noisyPlan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);

        var group = new ResolvedProcessGroup(
            "main",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 0,
            },
            [],
            [blocked, noisy]);

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        var token = CancelToken.New();
        var runTask = supervisor.RunAsync(token);
        var noisyVisible = false;

        try
        {
            noisyVisible = await TestHelpers.WaitUntilOrTimeoutAsync(
                () => output.HasLine(line => line.Process == noisy.Key && line.Line.Contains("sibling-visible-output", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            token.Cancel();
            await runTask;
        }

        var eventsDump = string.Join(
            " | ",
            output.Events.Select(e => $"{e.Process.ProcessName}:{e.Kind}:{e.ExitCode?.ToString() ?? "-"}:{e.Message ?? string.Empty}"));
        var linesDump = string.Join(
            " | ",
            output.Lines.Select(line => $"{line.Process.ProcessName}:{line.Stream}:{line.Line}"));

        Assert.That(noisyVisible, Is.True, $"Events: {eventsDump}{Environment.NewLine}Lines: {linesDump}");
        Assert.That(output.HasLine(line => line.Process == noisy.Key && line.Line.Contains("sibling-visible-output", StringComparison.Ordinal)), Is.True, $"Events: {eventsDump}{Environment.NewLine}Lines: {linesDump}");
    }

    [Test]
    public async Task RunAsync_CancelledProcess_EmitsStoppedWithoutFailed()
    {
        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var plan = BuildSleepPlan(30);

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "cancel"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            null);

        var token = CancelToken.New();
        var runTask = executor.RunAsync(request, output, token);

        await TestHelpers.WaitUntilAsync(
            () => output.Events.Any(e => e.Process == request.Process && e.Kind == ProcessOutputEventKind.Starting),
            TimeSpan.FromSeconds(2));

        token.Cancel();

        var result = await runTask;

        Assert.That(result.IsCancelled, Is.True);
        Assert.That(output.Events.Any(e => e.Process == request.Process && e.Kind == ProcessOutputEventKind.Stopped), Is.True);
        Assert.That(output.Events.Any(e => e.Process == request.Process && e.Kind == ProcessOutputEventKind.Failed), Is.False);
    }

    [Test]
    public async Task RunAsync_RunAsOnPosix_EmitsRunAsMode()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("POSIX-specific run_as behavior");

        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var plan = BuildDirectEchoPlan("runas-test");
        var user = Environment.UserName;

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "runas"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            user);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        var result = await executor.RunAsync(request, output, token);
        var starting = output.Events.Single(e => e.Kind == ProcessOutputEventKind.Starting);

        Assert.That(starting.Message, Does.Contain("run_as_mode="));
        Assert.That(result.IsCancelled, Is.False);
        Assert.That(output.Events.Any(e => e.Kind is ProcessOutputEventKind.Exited or ProcessOutputEventKind.Failed), Is.True);
    }

    [Test]
    public async Task RunAsync_RunAsOnPosix_EmitsReasonForSilentNonZeroExit()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("POSIX-specific run_as behavior");

        using var temp = new TempDirectory();
        var output = new TestOutputSink();
        var executor = new ProcessRunnerExecutor();
        var plan = BuildExitPlan(1);
        var user = Environment.UserName;

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "runas-silent-fail"),
            plan.ExecutablePath,
            plan.ExecutablePath,
            temp.Path,
            plan.Arguments,
            new Dictionary<string, string?>(),
            null,
            ProcessOutputMode.Inherit,
            null,
            0,
            0,
            user);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        var result = await executor.RunAsync(request, output, token);
        var failed = output.Events.Last(e => e.Kind == ProcessOutputEventKind.Failed);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Exception, Is.Null);
        Assert.That(failed.Message, Does.Contain("without output"));
    }

    private static LaunchPlan BuildShellEchoPlan(string value)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", ["/C", "echo", value]);

        return new LaunchPlan("/bin/sh", ["-c", $"echo {value}"]);
    }

    private static LaunchPlan BuildDirectEchoPlan(string value)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", ["/C", "echo", value]);

        return new LaunchPlan("/bin/echo", [value]);
    }

    private static LaunchPlan BuildExitPlan(int exitCode)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", ["/C", "exit", exitCode.ToString()]);

        return new LaunchPlan("/bin/sh", ["-c", $"exit {exitCode}"]);
    }

    private static LaunchPlan BuildSleepPlan(int seconds)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", ["/C", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"]);

        return new LaunchPlan("/bin/sh", ["-c", $"sleep {seconds}"]);
    }

    private static LaunchPlan BuildEchoThenSleepPlan(string value, int seconds)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", ["/C", $"echo {value} & ping -n {seconds + 1} 127.0.0.1 > nul"]);

        return new LaunchPlan("/bin/sh", ["-c", $"echo {QuotePosixLiteral(value)}; sleep {seconds}"]);
    }

    private static LaunchPlan BuildPrintfWithoutNewlineThenSleepPlan(string value, int seconds)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", ["/C", $"<nul set /p ={value} & ping -n {seconds + 1} 127.0.0.1 > nul"]);

        return new LaunchPlan("/bin/sh", ["-c", $"printf %s {QuotePosixLiteral(value)}; sleep {seconds}"]);
    }

    private static LaunchPlan BuildStderrExitPlan(string value, int exitCode)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", ["/C", $"echo {value} 1>&2 & exit {exitCode}"]);

        return new LaunchPlan("/bin/sh", ["-c", $"echo {QuotePosixLiteral(value)} 1>&2; exit {exitCode}"]);
    }

    private static LaunchPlan BuildExitPlanWithBackgroundChildHoldingOutputPipe(string attemptsPath)
    {
        var quotedAttemptsPath = QuotePosixLiteral(attemptsPath);
        var command =
            $"count=\"$(cat {quotedAttemptsPath} 2>/dev/null || echo 0)\"; " +
            "count=$((count + 1)); " +
            $"printf '%s\\n' \"$count\" > {quotedAttemptsPath}; " +
            "sleep 30 & " +
            "exit 1";

        return new LaunchPlan("/bin/sh", ["-c", command]);
    }

    private static string GetShellExecutable() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static string QuotePosixLiteral(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed record LaunchPlan(string ExecutablePath, IReadOnlyList<string> Arguments);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "procvd-tests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                    Directory.Delete(this.Path, true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
