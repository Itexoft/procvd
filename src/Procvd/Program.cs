// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Itexoft.IO.FileSystem;
using Itexoft.Threading;
using Procvd.Configuration;
using Procvd.Output;
using Procvd.Runtime;

namespace Procvd;

internal static class Program
{
    private static readonly string sampleConfig = string.Join(
        Environment.NewLine,
        "; Procvd configuration file.",
        "; Lines starting with ';' are comments.",
        ";",
        "; Minimal config: create a group and list process paths.",
        "; [main]",
        "; ./bin/api",
        ";",
        "; Global defaults applied to all groups and processes:",
        "; [defaults]",
        "; args = --flag value",
        "; env.LOG_LEVEL = info",
        "; workdir = .",
        "; output = inherit",
        "; output_dir = ./logs",
        "; output_max_bytes = 10mb",
        "; output_max_files = 3",
        ";",
        "; Group settings and processes (each line is a process and runs in parallel):",
        "[main]",
        "; Replace the paths below with your executables:",
        "./bin/api",
        "./bin/worker",
        ";",
        "; Named process example:",
        "; web = ./bin/web --port 8080",
        ";",
        "; Per-process overrides:",
        "; process.worker.env.LOG_LEVEL = debug",
        "; process.worker.workdir = ./services/worker",
        ";",
        "; Restart policy (process or group):",
        "; restart = group",
        "; restart_delay = 2s",
        "; depends = database, cache",
        ";",
        "; Direct process example (no shell):",
        "; [process:main/api]",
        "; path = ./bin/api",
        "; args = --port 8080",
        "; env.LOG_LEVEL = debug",
        ";",
        "; Group sets combine groups and dependencies:",
        "; [set:backend]",
        "; groups = main, jobs",
        "; depends = infra");

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 1)
        {
            PrintUsage();

            return 1;
        }

        var configPath = args.Length == 1 ? ResolveCustomPath(args[0]) : ResolveDefaultPath();

        if (!File.Exists(configPath))
        {
            CreateSampleConfig(configPath);
            Console.WriteLine($"Config file not found. A sample config has been created at '{configPath}'.");

            return 0;
        }

        try
        {
            if (!TryAcquireInstanceLock(out var instanceLock, out var instanceLockError))
            {
                Console.Error.WriteLine(instanceLockError);

                return 1;
            }

            using var processLock = instanceLock;
            var config = LoadConfig(configPath);
            var baseDirectory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
            var resolved = new ProcessConfigResolver().Resolve(config, baseDirectory);

            var stopToken = CancelToken.New();

            await using var output = new ProcessChunkedConsoleOutputSink(TimeSpan.FromSeconds(1));

            var supervisor = new ProcessSupervisor(
                resolved,
                new ProcessSupervisorOptions
                {
                    Output = output,
                });
            var stopRequested = 0;

            void stopNow()
            {
                if (Interlocked.Exchange(ref stopRequested, 1) != 0)
                {
                    Environment.Exit(130);

                    return;
                }

                stopToken.Cancel();
                supervisor.KillAllRunningProcesses();
            }

            using var sigint = !OperatingSystem.IsWindows()
                ? PosixSignalRegistration.Create(
                    PosixSignal.SIGINT,
                    context =>
                    {
                        context.Cancel = true;
                        stopNow();
                    })
                : null;

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopNow();
            };

            await supervisor.RunAsync(stopToken);

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");

            return 130;
        }
        catch (ProcessConfigException ex)
        {
            Console.Error.WriteLine($"Config error: {ex.Message}");

            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled error: {ex}");

            return 3;
        }
    }

    private static ProcessConfig LoadConfig(string path)
    {
        IProcessConfigLoader loader = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? new JsonProcessConfigLoader()
            : new IniProcessConfigLoader();

        return LoadConfig(path, loader);
    }

    private static ProcessConfig LoadConfig(string path, IProcessConfigLoader loader)
    {
        using var stream = IFileSystem.Sys.OpenString(path, SysFileMode.Read);

        return loader.Load(stream);
    }

    private static void CreateSampleConfig(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, sampleConfig);
    }

    private static string ResolveCustomPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Config path is empty.", nameof(path));

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
    }

    private static string ResolveDefaultPath()
    {
        var processPath = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(processPath))
            return BuildDefaultPath(processPath);

        return Path.Combine(AppContext.BaseDirectory, "procvd.ini");
    }

    private static string BuildDefaultPath(string location)
    {
        var directory = Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        var name = Path.GetFileNameWithoutExtension(location);

        if (string.IsNullOrWhiteSpace(name))
            name = "procvd";

        return Path.Combine(directory, name + ".ini");
    }

    private static bool TryAcquireInstanceLock(out FileStream? stream, out string? error)
    {
        var executablePath = Path.GetFullPath(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "procvd"));
        var lockPath = BuildInstanceLockPath(executablePath);
        var lockDirectory = Path.GetDirectoryName(lockPath);

        if (!string.IsNullOrWhiteSpace(lockDirectory))
            Directory.CreateDirectory(lockDirectory);

        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);

            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine(executablePath);
            writer.Write(Environment.ProcessId);
            writer.Flush();

            stream.Position = 0;
            error = null;

            return true;
        }
        catch (IOException)
        {
            stream = null;
            error = $"Another instance is already running from '{executablePath}'.";

            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            stream = null;
            error = $"Failed to acquire instance lock for '{executablePath}': {ex.Message}";

            return false;
        }
    }

    private static string BuildInstanceLockPath(string executablePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executablePath)));

        return Path.Combine(Path.GetTempPath(), $"procvd.{hash}.lock");
    }

    private static void PrintUsage()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "procvd");
        Console.WriteLine($"Usage: {name} [configPath]");
    }
}
