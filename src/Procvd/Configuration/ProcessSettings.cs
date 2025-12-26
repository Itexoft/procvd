// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

namespace Procvd.Configuration;

public sealed class ProcessSettings
{
    public static ProcessSettings Empty { get; } = new();

    public IReadOnlyList<string>? Args { get; init; }

    public IReadOnlyDictionary<string, string?>? Env { get; init; }

    public string? WorkingDirectory { get; init; }

    public ProcessOutputMode? OutputMode { get; init; }

    public string? OutputDirectory { get; init; }

    public long? OutputMaxBytes { get; init; }

    public int? OutputMaxFiles { get; init; }

    public ProcessSettings Merge(ProcessSettings? other)
    {
        if (other is null)
            return this;

        var args = new List<string>(NormalizeArgs(this.Args));
        args.AddRange(NormalizeArgs(other.Args));

        var env = new Dictionary<string, string?>(NormalizeEnv(this.Env), StringComparer.Ordinal);

        foreach (var (key, value) in NormalizeEnv(other.Env))
            env[key] = value;

        return new()
        {
            Args = args,
            Env = env,
            WorkingDirectory = other.WorkingDirectory ?? this.WorkingDirectory,
            OutputMode = other.OutputMode ?? this.OutputMode,
            OutputDirectory = other.OutputDirectory ?? this.OutputDirectory,
            OutputMaxBytes = other.OutputMaxBytes ?? this.OutputMaxBytes,
            OutputMaxFiles = other.OutputMaxFiles ?? this.OutputMaxFiles,
        };
    }

    public static IReadOnlyList<string> NormalizeArgs(IReadOnlyList<string>? args) =>
        args is null || args.Count == 0 ? Array.Empty<string>() : args;

    public static IReadOnlyDictionary<string, string?> NormalizeEnv(IReadOnlyDictionary<string, string?>? env) =>
        env is null || env.Count == 0 ? new Dictionary<string, string?>(StringComparer.Ordinal) : env;
}
