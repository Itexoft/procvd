// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Runtime.Versioning;
using System.Text;
using Itexoft.Processes;
using Itexoft.Threading;
using Procvd.Configuration;
using Procvd.Output;

namespace Procvd.Runtime;

public sealed class ProcessRunnerExecutor : IProcessExecutor
{
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        IProcessOutputSink output,
        CancelToken token = default)
    {
        OutputFileState? outputFile = null;
        ProcessFileOutputTailer? tailer = null;
        Task? tailTask = null;
        LaunchPlan? plan = null;

        try
        {
            if (request.OutputMode == ProcessOutputMode.File)
            {
                outputFile = PrepareOutputFile(request);
                tailer = new ProcessFileOutputTailer(outputFile.Path, request.Process, request.DisplayPath, output);
                plan = CreateFileLaunchPlan(request, outputFile.Path);
            }

            plan ??= new LaunchPlan(request.ExecutablePath, request.Arguments);

            await using var runner = new ProcessRunner(plan.ExecutablePath, request.WorkingDirectory);
            output.WriteEvent(new ProcessOutputEvent(
                request.Process,
                request.DisplayPath,
                ProcessOutputEventKind.Starting,
                DateTimeOffset.UtcNow));

            var options = new ProcessRunner.ProcessRunOptions
            {
                Environment = request.Environment,
                CaptureOutput = false,
            };

            var runTask = runner.RunAsync(plan.Arguments, options, token);

            if (tailer is not null && outputFile is not null)
                tailTask = tailer.RunAsync(runTask.AsTask(), outputFile.StartPosition, token);

            var exitCode = await runTask;

            if (tailTask is not null)
                await tailTask.ConfigureAwait(false);

            output.WriteEvent(new ProcessOutputEvent(
                request.Process,
                request.DisplayPath,
                ProcessOutputEventKind.Exited,
                DateTimeOffset.UtcNow,
                exitCode));

            return new ProcessExecutionResult(exitCode, false, null);
        }
        catch (OperationCanceledException)
        {
            output.WriteEvent(new ProcessOutputEvent(
                request.Process,
                request.DisplayPath,
                ProcessOutputEventKind.Stopped,
                DateTimeOffset.UtcNow));

            return new ProcessExecutionResult(null, true, null);
        }
        catch (Exception ex)
        {
            output.WriteEvent(new ProcessOutputEvent(
                request.Process,
                request.DisplayPath,
                ProcessOutputEventKind.Failed,
                DateTimeOffset.UtcNow,
                null,
                ex.Message));

            return new ProcessExecutionResult(null, false, ex);
        }
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

        var scriptArgs = request.ShellCommand is null ? request.Arguments : Array.Empty<string>();
        return new LaunchPlan(scriptPath, scriptArgs);
    }

    private static string CreateOutputScript(ProcessExecutionRequest request, string outputPath)
    {
        var scriptDirectory = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", ".procvd");
        Directory.CreateDirectory(scriptDirectory);

        var groupName = SanitizeFileName(request.Process.GroupName);
        var processName = SanitizeFileName(request.Process.ProcessName);
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var scriptPath = Path.Combine(scriptDirectory, $"{groupName}.{processName}{extension}");

        var content = OperatingSystem.IsWindows()
            ? BuildWindowsScript(request, outputPath)
            : BuildPosixScript(request, outputPath);

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
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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

    private sealed record LaunchPlan(string ExecutablePath, IReadOnlyList<string> Arguments);

    private sealed record OutputFileState(string Path, long StartPosition);
}
