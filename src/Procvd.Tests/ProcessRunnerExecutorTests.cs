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
    public async Task RunAsync_Inherit_DoesNotEmitLines()
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
            0);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        var result = await executor.RunAsync(request, output, token);

        Assert.That(result.IsCancelled, Is.False);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(output.Lines, Is.Empty);
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
            Array.Empty<string>(),
            new Dictionary<string, string?>(),
            "echo file-test",
            ProcessOutputMode.File,
            logPath,
            1024 * 1024,
            3);

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

        File.WriteAllText(logPath, new string('x', 256));

        var request = new ProcessExecutionRequest(
            new ProcessKey("main", "rotate"),
            GetShellExecutable(),
            GetShellExecutable(),
            temp.Path,
            Array.Empty<string>(),
            new Dictionary<string, string?>(),
            "echo rotate-test",
            ProcessOutputMode.File,
            logPath,
            64,
            2);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        await executor.RunAsync(request, output, token);

        var rotated = logPath + ".1";
        Assert.That(File.Exists(rotated), Is.True);
        Assert.That(File.ReadAllText(logPath).Contains("rotate-test", StringComparison.Ordinal), Is.True);
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
            3);

        var token = CancelToken.None.Branch(TimeSpan.FromSeconds(5));
        var result = await executor.RunAsync(request, output, token);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(output.Lines.Any(line => line.Line.Contains("direct-test", StringComparison.Ordinal)), Is.True);
        Assert.That(File.Exists(logPath), Is.True);
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
            0);

        var group = new ResolvedProcessGroup(
            "main",
            GroupRestartMode.Process,
            new ProcessRestartPolicy
            {
                MaxRestarts = 2,
                RestartDelay = TimeSpan.FromMilliseconds(10),
            },
            Array.Empty<string>(),
            new[] { process });

        var supervisor = new ProcessGroupSupervisor(group, executor, output);
        await supervisor.RunAsync();

        var exitedCount = output.Events.Count(e => e.Kind == ProcessOutputEventKind.Exited);
        var failedCount = output.Events.Count(e => e.Kind == ProcessOutputEventKind.Failed);

        Assert.That(exitedCount, Is.EqualTo(3));
        Assert.That(failedCount, Is.EqualTo(1));
    }

    private static LaunchPlan BuildShellEchoPlan(string value)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", new[] { "/C", "echo", value });

        return new LaunchPlan("/bin/sh", new[] { "-c", $"echo {value}" });
    }

    private static LaunchPlan BuildDirectEchoPlan(string value)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", new[] { "/C", "echo", value });

        return new LaunchPlan("/bin/echo", new[] { value });
    }

    private static LaunchPlan BuildExitPlan(int exitCode)
    {
        if (OperatingSystem.IsWindows())
            return new LaunchPlan("cmd.exe", new[] { "/C", "exit", exitCode.ToString() });

        return new LaunchPlan("/bin/sh", new[] { "-c", $"exit {exitCode}" });
    }

    private static string GetShellExecutable() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private sealed record LaunchPlan(string ExecutablePath, IReadOnlyList<string> Arguments);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            this.Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "procvd-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                    Directory.Delete(this.Path, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }
}
