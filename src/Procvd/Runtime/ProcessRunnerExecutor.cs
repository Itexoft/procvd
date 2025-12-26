// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Itexoft.IO.Streams.Chars;
using Itexoft.Processes;
using Itexoft.Threading;
using Itexoft.Threading.Tasks;
using Procvd.Configuration;
using Procvd.Output;

namespace Procvd.Runtime;

public sealed class ProcessRunnerExecutor : IProcessExecutor
{
    private readonly List<SysProcess> running = [];
    private readonly Lock sync = new();

    public async Promise<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        IProcessOutputSink output,
        CancelToken cancelToken = default)
    {
        OutputFileState? outputFile = null;
        ProcessFileOutputTailer? tailer = null;
        Promise? tailTask = null;
        Promise? stdOutTask = null;
        Promise? stdErrTask = null;
        LaunchPlan? plan = null;
        var runAsUser = request.RunAsUser;
        var environment = request.Environment;
        var workingDirectory = request.WorkingDirectory;
        var runAsMode = "native";
        var outputLineCount = 0;

        try
        {
            cancelToken.ThrowIf();

            if (request.OutputMode == ProcessOutputMode.File)
            {
                outputFile = PrepareOutputFile(request);
                tailer = new ProcessFileOutputTailer(outputFile.Path, request.Process, request.DisplayPath, output);
                plan = CreateFileLaunchPlan(request, outputFile.Path);
            }

            plan ??= new LaunchPlan(request.ExecutablePath, request.Arguments);

            if (!string.IsNullOrWhiteSpace(request.RunAsUser) && !OperatingSystem.IsWindows())
            {
                if (string.Equals(Environment.UserName, request.RunAsUser, StringComparison.Ordinal))
                {
                    runAsUser = null;
                    runAsMode = "direct";
                }
                else
                {
                    if (!IsPosixRoot())
                        throw new ProcessConfigException(
                            $"run_as '{request.RunAsUser}' requires root on POSIX when target differs from current user '{Environment.UserName}'");

                    runAsMode = "native";
                }
            }

            SysProcess? process = null;

            try
            {
                var startParts = new List<string>(3)
                {
                    $"output_mode={request.OutputMode.ToString().ToLowerInvariant()}",
                    "capture_output=true",
                };

                if (!string.IsNullOrWhiteSpace(request.RunAsUser))
                {
                    startParts.Add($"run_as={request.RunAsUser}");
                    startParts.Add($"run_as_mode={runAsMode}");
                }

                var startMessage = string.Join(' ', startParts);

                output.WriteEvent(
                    new ProcessOutputEvent(
                        request.Process,
                        request.DisplayPath,
                        ProcessOutputEventKind.Starting,
                        DateTimeOffset.UtcNow,
                        null,
                        startMessage));

                var options = new SysProcessOptions(plan.ExecutablePath)
                {
                    Environment = environment,
                    WorkingDirectory = workingDirectory,
                    Arguments = plan.Arguments.ToArray(),
                    User = runAsUser,
                    RedirectStdError = true,
                    RedirectStdOut = true,
                };

                cancelToken.ThrowIf();
                process = SysProcess.Start(options);
                this.RegisterRunner(process);

                stdOutTask = Promise.Run(() => PumpOutput(
                    process.StdOut!,
                    request.Process,
                    request.DisplayPath,
                    ProcessOutputStream.StdOut,
                    output,
                    () => Interlocked.Increment(ref outputLineCount)));

                stdErrTask = Promise.Run(() => PumpOutput(
                    process.StdErr!,
                    request.Process,
                    request.DisplayPath,
                    ProcessOutputStream.StdErr,
                    output,
                    () => Interlocked.Increment(ref outputLineCount)));

                using var cancelBridge = cancelToken.Bridge(out var cancellationToken);
                await using var killRegistration = cancellationToken.UnsafeRegister(static state => TryKill((SysProcess)state!), process);

                if (cancelToken.IsRequested)
                    TryKill(process);

                var runTaskAsTask = process.WaitAsync();

                if (tailer is not null && outputFile is not null)
                    tailTask = tailer.RunAsync(runTaskAsTask, outputFile.StartPosition);

                var exitCode = await runTaskAsTask;

                TryKill(process);

                if (cancelToken.IsRequested)
                {
                    output.WriteEvent(
                        new ProcessOutputEvent(request.Process, request.DisplayPath, ProcessOutputEventKind.Stopped, DateTimeOffset.UtcNow));

                    return new ProcessExecutionResult(null, true, null);
                }

                await AwaitTaskAsync(stdOutTask);
                await AwaitTaskAsync(stdErrTask);

                if (tailTask is not null)
                    await tailTask;

                output.WriteEvent(
                    new ProcessOutputEvent(request.Process, request.DisplayPath, ProcessOutputEventKind.Exited, DateTimeOffset.UtcNow, exitCode));

                if (!string.IsNullOrWhiteSpace(request.RunAsUser) && exitCode != 0 && Volatile.Read(ref outputLineCount) == 0)
                {
                    var reason = runAsMode switch
                    {
                        "native" => $"run_as '{request.RunAsUser}' native launch exited with code {exitCode} without output",
                        "direct" => $"run_as '{request.RunAsUser}' resolved to direct mode and exited with code {exitCode} without output",
                        _ => $"run_as '{request.RunAsUser}' exited with code {exitCode} without output",
                    };

                    output.WriteEvent(
                        new ProcessOutputEvent(
                            request.Process,
                            request.DisplayPath,
                            ProcessOutputEventKind.Failed,
                            DateTimeOffset.UtcNow,
                            null,
                            reason));
                }

                return new ProcessExecutionResult(exitCode, false, null);
            }
            finally
            {
                if (!cancelToken.IsRequested)
                {
                    await AwaitTaskAsync(stdOutTask);
                    await AwaitTaskAsync(stdErrTask);
                }

                this.UnregisterRunner(process);
            }
        }
        catch (OperationCanceledException)
        {
            output.WriteEvent(new ProcessOutputEvent(request.Process, request.DisplayPath, ProcessOutputEventKind.Stopped, DateTimeOffset.UtcNow));

            return new ProcessExecutionResult(null, true, null);
        }
        catch (Exception) when (cancelToken.IsRequested)
        {
            output.WriteEvent(new ProcessOutputEvent(request.Process, request.DisplayPath, ProcessOutputEventKind.Stopped, DateTimeOffset.UtcNow));

            return new ProcessExecutionResult(null, true, null);
        }
        catch (Exception ex)
        {
            var message = ex.Message;

            if (string.IsNullOrWhiteSpace(message))
                message = ex.GetType().Name;

            if (ex is Win32Exception win32 && win32.NativeErrorCode != 0)
                message = $"{message} (errno {win32.NativeErrorCode})";

            if (!string.IsNullOrWhiteSpace(request.RunAsUser))
                message = $"run_as '{request.RunAsUser}' failed: {message}";

            output.WriteEvent(
                new ProcessOutputEvent(request.Process, request.DisplayPath, ProcessOutputEventKind.Failed, DateTimeOffset.UtcNow, null, message));

            return new ProcessExecutionResult(null, false, ex);
        }
    }

    public void KillAll()
    {
        List<SysProcess> snapshot;

        lock (this.sync)
            snapshot = [..this.running];

        foreach (var runner in snapshot)
            TryKill(runner);
    }

    private static OutputFileState PrepareOutputFile(ProcessExecutionRequest request)
    {
        var outputPath = request.OutputPath;

        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ProcessConfigException($"process '{request.Process.ProcessName}' output path is missing");

        var directory = Path.GetDirectoryName(outputPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        RotateIfNeeded(outputPath, request.OutputMaxBytes, request.OutputMaxFiles);

        using (File.Open(outputPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }

        var startPosition = new FileInfo(outputPath).Length;

        return new OutputFileState(outputPath, startPosition);
    }

    private static void RotateIfNeeded(string path, long maxBytes, int maxFiles)
    {
        if (maxBytes <= 0)
            return;

        var info = new FileInfo(path);

        if (!info.Exists || info.Length <= maxBytes)
            return;

        if (maxFiles <= 1)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

            return;
        }

        var maxArchive = maxFiles - 1;

        for (var i = maxArchive - 1; i >= 1; i--)
        {
            var source = path + "." + i;
            var target = path + "." + (i + 1);

            if (File.Exists(target))
                File.Delete(target);

            if (File.Exists(source))
                File.Move(source, target);
        }

        var first = path + ".1";

        if (File.Exists(first))
            File.Delete(first);

        File.Move(path, first);
    }

    private static LaunchPlan CreateFileLaunchPlan(ProcessExecutionRequest request, string outputPath)
    {
        var scriptPath = CreateOutputScript(request, outputPath);

        if (OperatingSystem.IsWindows())
        {
            var shell = ResolveShellPath();
            var args = new List<string> { "/C", scriptPath };

            if (request.ShellCommand is null)
                args.AddRange(request.Arguments);

            return new LaunchPlan(shell, args);
        }

        var scriptArgs = request.ShellCommand is null ? request.Arguments : [];

        return new LaunchPlan(scriptPath, scriptArgs);
    }

    private static bool IsPosixRoot()
    {
        if (OperatingSystem.IsWindows())
            return false;

        return geteuid() == 0;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static string CreateOutputScript(ProcessExecutionRequest request, string outputPath)
    {
        var scriptDirectory = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", ".procvd");
        Directory.CreateDirectory(scriptDirectory);

        var groupName = SanitizeFileName(request.Process.GroupName);
        var processName = SanitizeFileName(request.Process.ProcessName);
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var scriptPath = Path.Combine(scriptDirectory, $"{groupName}.{processName}{extension}");

        var content = OperatingSystem.IsWindows() ? BuildWindowsScript(request, outputPath) : BuildPosixScript(request, outputPath);

        File.WriteAllText(scriptPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!OperatingSystem.IsWindows())
            TryMakeExecutable(scriptPath);

        return scriptPath;
    }

    private static string BuildWindowsScript(ProcessExecutionRequest request, string outputPath)
    {
        var escapedOutput = EscapeCmdLiteral(outputPath);

        if (!string.IsNullOrWhiteSpace(request.ShellCommand))
        {
            var command = request.ShellCommand;

            return $"@echo off{Environment.NewLine}{command} 1>>\"{escapedOutput}\" 2>>&1{Environment.NewLine}";
        }

        var executablePath = EscapeCmdLiteral(request.ExecutablePath);

        return $"@echo off{Environment.NewLine}\"{executablePath}\" %* 1>>\"{escapedOutput}\" 2>>&1{Environment.NewLine}";
    }

    private static string BuildPosixScript(ProcessExecutionRequest request, string outputPath)
    {
        var escapedOutput = QuotePosixLiteral(outputPath);

        if (!string.IsNullOrWhiteSpace(request.ShellCommand))
        {
            var command = QuotePosixLiteral(request.ShellCommand);

            return $"#!/bin/sh{Environment.NewLine}exec /bin/sh -c {command} >>{escapedOutput} 2>&1{Environment.NewLine}";
        }

        var executablePath = QuotePosixLiteral(request.ExecutablePath);

        return $"#!/bin/sh{Environment.NewLine}exec {executablePath} \"$@\" >>{escapedOutput} 2>&1{Environment.NewLine}";
    }

    [UnsupportedOSPlatform("windows")]
    private static void TryMakeExecutable(string path)
    {
        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolveShellPath()
    {
        var comspec = Environment.GetEnvironmentVariable("COMSPEC");

        return string.IsNullOrWhiteSpace(comspec) ? "cmd.exe" : comspec;
    }

    private static string QuotePosixLiteral(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string EscapeCmdLiteral(string value) =>
        value.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "process";

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            buffer[i] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
        }

        return new string(buffer);
    }

    private static void PumpOutput(
        CharStreamBr stream,
        ProcessKey process,
        string displayPath,
        ProcessOutputStream outputStream,
        IProcessOutputSink output,
        Action onLine)
    {
        while (true)
        {
            var read = stream.ReadLine(out var line);

            if (read == 0)
                break;

            onLine();
            output.Write(new ProcessOutputLine(process, displayPath, outputStream, line, DateTimeOffset.UtcNow));
        }
    }

    private static async Promise AwaitTaskAsync(Promise? task)
    {
        if (task is null)
            return;

        try
        {
            await task;
        }
        catch (OperationCanceledException) { }
    }

    private static void TryKill(SysProcess process)
    {
        try
        {
            process.Kill(tree: true);
        }
        catch { }
    }

    private void RegisterRunner(SysProcess runner)
    {
        lock (this.sync)
            this.running.Add(runner);
    }

    private void UnregisterRunner(SysProcess? runner)
    {
        if (runner == null)
            return;

        lock (this.sync)
            this.running.Remove(runner);
    }

    private sealed record LaunchPlan(string ExecutablePath, IReadOnlyList<string> Arguments);

    private sealed record OutputFileState(string Path, long StartPosition);
}
