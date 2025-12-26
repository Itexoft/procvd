// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Configuration;

namespace Procvd.Runtime;

public sealed class ProcessExecutionRequest(
    ProcessKey process,
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
    public ProcessKey Process { get; } = process;
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
