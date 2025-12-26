// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Runtime;

namespace Procvd.Configuration;

public sealed class ResolvedProcessConfig(string baseDirectory, IReadOnlyDictionary<string, ResolvedProcessGroup> groups)
{
    public string BaseDirectory { get; } = baseDirectory;

    public IReadOnlyDictionary<string, ResolvedProcessGroup> Groups { get; } = groups;
}

public sealed class ResolvedProcessGroup(
    string name,
    GroupRestartMode restartMode,
    ProcessRestartPolicy restartPolicy,
    IReadOnlyList<string> dependencies,
    IReadOnlyList<ResolvedProcess> processes)
{
    public string Name { get; } = name;

    public GroupRestartMode RestartMode { get; } = restartMode;

    public ProcessRestartPolicy RestartPolicy { get; } = restartPolicy;

    public IReadOnlyList<string> Dependencies { get; } = dependencies;

    public IReadOnlyList<ResolvedProcess> Processes { get; } = processes;
}

public sealed class ResolvedProcess(
    ProcessKey key,
    string executablePath,
    string displayPath,
    string workingDirectory,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string?> environment,
    string? shellCommand,
    ProcessOutputMode outputMode,
    string? outputPath,
    long outputMaxBytes,
    int outputMaxFiles)
{
    public ProcessKey Key { get; } = key;

    public string ExecutablePath { get; } = executablePath;

    public string DisplayPath { get; } = displayPath;

    public string WorkingDirectory { get; } = workingDirectory;

    public IReadOnlyList<string> Arguments { get; } = arguments;

    public IReadOnlyDictionary<string, string?> Environment { get; } = environment;

    public string? ShellCommand { get; } = shellCommand;

    public ProcessOutputMode OutputMode { get; } = outputMode;

    public string? OutputPath { get; } = outputPath;

    public long OutputMaxBytes { get; } = outputMaxBytes;

    public int OutputMaxFiles { get; } = outputMaxFiles;
}
