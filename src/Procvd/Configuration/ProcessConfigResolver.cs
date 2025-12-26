// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Runtime;

namespace Procvd.Configuration;

public sealed class ProcessConfigResolver
{
    private const long defaultOutputMaxBytes = 32L * 1024 * 1024;
    private const int defaultOutputMaxFiles = 3;
    private const string defaultOutputDirectoryName = "procvd-logs";

    public ResolvedProcessConfig Resolve(ProcessConfig config, string baseDirectory)
    {
        ProcessConfigValidator.Validate(config);

        var groups = config.Groups ?? throw new ProcessConfigException("groups are missing");
        var groupSets = config.GroupSets ?? new Dictionary<string, ProcessGroupSetConfig>(StringComparer.Ordinal);
        var resolvedBaseDirectory = ResolveBaseDirectory(baseDirectory);

        var groupSetMembers = ResolveGroupSetMembers(groupSets, groups);
        var groupSetDependencies = ResolveGroupSetDependencies(groupSets, groups, groupSetMembers);
        var groupMembership = BuildGroupMembership(groupSetMembers);

        var resolvedGroups = new Dictionary<string, ResolvedProcessGroup>(StringComparer.Ordinal);

        foreach (var groupName in groups.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var groupConfig = groups[groupName];
            var settings = config.Defaults ?? ProcessSettings.Empty;
            var memberships = groupMembership.TryGetValue(groupName, out var setNames)
                ? setNames.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

            foreach (var setName in memberships)
                settings = settings.Merge(groupSets[setName].Settings);

            settings = settings.Merge(groupConfig.Settings);

            var dependencies = new SortedSet<string>(StringComparer.Ordinal);
            AddDependencies(dependencies, groupConfig.DependsOn, groups, groupSets, groupSetMembers);

            foreach (var setName in memberships)
                AddResolvedDependencies(dependencies, groupSetDependencies[setName]);

            if (dependencies.Contains(groupName))
                throw new ProcessConfigException($"group '{groupName}' depends on itself");

            var processes = ResolveProcesses(groupName, groupConfig, settings, resolvedBaseDirectory);

            resolvedGroups[groupName] = new ResolvedProcessGroup(
                groupName,
                groupConfig.RestartMode,
                groupConfig.RestartPolicy,
                dependencies.ToArray(),
                processes);
        }

        return new ResolvedProcessConfig(resolvedBaseDirectory, resolvedGroups);
    }

    private static IReadOnlyList<ResolvedProcess> ResolveProcesses(
        string groupName,
        ProcessGroupConfig group,
        ProcessSettings baseSettings,
        string baseDirectory)
    {
        var processes = group.Processes ?? new Dictionary<string, ProcessConfigItem>(StringComparer.Ordinal);
        var resolved = new List<ResolvedProcess>();

        foreach (var processName in processes.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var process = processes[processName];

            if (!process.Enabled)
                continue;

            var settings = baseSettings.Merge(process.Settings);
            var workingDirectory = ResolveWorkingDirectory(baseDirectory, settings.WorkingDirectory);
            var environment = new Dictionary<string, string?>(ProcessSettings.NormalizeEnv(settings.Env), StringComparer.Ordinal);
            var outputMode = settings.OutputMode ?? ProcessOutputMode.Inherit;
            var outputDirectory = ResolveOutputDirectory(baseDirectory, settings.OutputDirectory);
            var outputMaxBytes = outputMode == ProcessOutputMode.File
                ? settings.OutputMaxBytes ?? defaultOutputMaxBytes
                : 0;
            var outputMaxFiles = outputMode == ProcessOutputMode.File
                ? settings.OutputMaxFiles ?? defaultOutputMaxFiles
                : 0;

            if (!string.IsNullOrWhiteSpace(process.Command))
            {
                var args = ProcessSettings.NormalizeArgs(settings.Args);
                if (args.Count > 0)
                    throw new ProcessConfigException($"process '{processName}' in group '{groupName}' cannot combine command with args");

                var shellPath = ResolveShellPath();
                var shellArgs = BuildShellArguments(process.Command);
                var displayPath = ResolveCommandDisplayPath(baseDirectory, process.Command);
                var outputPath = outputMode == ProcessOutputMode.File
                    ? ResolveOutputPath(outputDirectory, groupName, processName)
                    : null;

                resolved.Add(new ResolvedProcess(
                    new ProcessKey(groupName, processName),
                    shellPath,
                    displayPath,
                    workingDirectory,
                    shellArgs,
                    environment,
                    process.Command,
                    outputMode,
                    outputPath,
                    outputMaxBytes,
                    outputMaxFiles));

                continue;
            }

            if (string.IsNullOrWhiteSpace(process.Path))
                throw new ProcessConfigException($"process '{processName}' in group '{groupName}' has empty path");

            var executablePath = ResolvePath(baseDirectory, process.Path);
            var display = Path.GetRelativePath(baseDirectory, executablePath);
            var outputPathDirect = outputMode == ProcessOutputMode.File
                ? ResolveOutputPath(outputDirectory, groupName, processName)
                : null;

            resolved.Add(new ResolvedProcess(
                new ProcessKey(groupName, processName),
                executablePath,
                display,
                workingDirectory,
                ProcessSettings.NormalizeArgs(settings.Args).ToArray(),
                environment,
                null,
                outputMode,
                outputPathDirect,
                outputMaxBytes,
                outputMaxFiles));
        }

        if (resolved.Count == 0)
            throw new ProcessConfigException($"group '{groupName}' has no enabled processes");

        return resolved;
    }

    private static void AddDependencies(
        ISet<string> target,
        IReadOnlyList<string>? dependencies,
        IReadOnlyDictionary<string, ProcessGroupConfig> groups,
        IReadOnlyDictionary<string, ProcessGroupSetConfig> groupSets,
        IReadOnlyDictionary<string, HashSet<string>> groupSetMembers)
    {
        if (dependencies is null || dependencies.Count == 0)
            return;

        foreach (var entry in dependencies)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            if (groups.ContainsKey(entry))
            {
                target.Add(entry);
                continue;
            }

            if (groupSets.ContainsKey(entry))
            {
                AddResolvedDependencies(target, groupSetMembers[entry]);
                continue;
            }

            throw new ProcessConfigException($"dependency '{entry}' not found");
        }
    }

    private static void AddResolvedDependencies(ISet<string> target, IEnumerable<string> dependencies)
    {
        foreach (var entry in dependencies)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            target.Add(entry);
        }
    }

    private static Dictionary<string, HashSet<string>> ResolveGroupSetMembers(
        IReadOnlyDictionary<string, ProcessGroupSetConfig> groupSets,
        IReadOnlyDictionary<string, ProcessGroupConfig> groups)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var stack = new HashSet<string>(StringComparer.Ordinal);

        foreach (var setName in groupSets.Keys)
            ResolveGroupSetMembers(setName, groupSets, groups, result, stack);

        return result;
    }

    private static HashSet<string> ResolveGroupSetMembers(
        string setName,
        IReadOnlyDictionary<string, ProcessGroupSetConfig> groupSets,
        IReadOnlyDictionary<string, ProcessGroupConfig> groups,
        Dictionary<string, HashSet<string>> cache,
        HashSet<string> stack)
    {
        if (cache.TryGetValue(setName, out var cached))
            return cached;

        if (!groupSets.TryGetValue(setName, out var set))
            throw new ProcessConfigException($"group set '{setName}' not found");

        if (!stack.Add(setName))
            throw new ProcessConfigException($"cycle detected in group sets near '{setName}'");

        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var groupName in set.Groups ?? Array.Empty<string>())
        {
            if (!groups.ContainsKey(groupName))
                throw new ProcessConfigException($"group '{groupName}' referenced by set '{setName}' not found");

            members.Add(groupName);
        }

        foreach (var childSet in set.GroupSets ?? Array.Empty<string>())
        {
            var childMembers = ResolveGroupSetMembers(childSet, groupSets, groups, cache, stack);

            foreach (var childMember in childMembers)
                members.Add(childMember);
        }

        stack.Remove(setName);
        cache[setName] = members;

        return members;
    }

    private static Dictionary<string, HashSet<string>> ResolveGroupSetDependencies(
        IReadOnlyDictionary<string, ProcessGroupSetConfig> groupSets,
        IReadOnlyDictionary<string, ProcessGroupConfig> groups,
        IReadOnlyDictionary<string, HashSet<string>> groupSetMembers)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (setName, set) in groupSets)
        {
            var dependencies = new HashSet<string>(StringComparer.Ordinal);

            AddDependencies(dependencies, set.DependsOn, groups, groupSets, groupSetMembers);
            result[setName] = dependencies;
        }

        return result;
    }

    private static Dictionary<string, HashSet<string>> BuildGroupMembership(
        IReadOnlyDictionary<string, HashSet<string>> groupSetMembers)
    {
        var membership = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (setName, members) in groupSetMembers)
        {
            foreach (var groupName in members)
            {
                if (!membership.TryGetValue(groupName, out var list))
                {
                    list = new HashSet<string>(StringComparer.Ordinal);
                    membership[groupName] = list;
                }

                list.Add(setName);
            }
        }

        return membership;
    }

    private static string ResolveBaseDirectory(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ProcessConfigException("base directory is empty");

        return Path.GetFullPath(baseDirectory);
    }

    private static string ResolvePath(string baseDirectory, string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    private static string ResolveWorkingDirectory(string baseDirectory, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return baseDirectory;

        if (Path.IsPathRooted(workingDirectory))
            return Path.GetFullPath(workingDirectory);

        return Path.GetFullPath(Path.Combine(baseDirectory, workingDirectory));
    }

    private static string ResolveOutputDirectory(string baseDirectory, string? outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return Path.GetFullPath(Path.Combine(baseDirectory, defaultOutputDirectoryName));

        if (Path.IsPathRooted(outputDirectory))
            return Path.GetFullPath(outputDirectory);

        return Path.GetFullPath(Path.Combine(baseDirectory, outputDirectory));
    }

    private static string ResolveOutputPath(string outputDirectory, string groupName, string processName)
    {
        var groupSegment = SanitizeFileName(groupName);
        var processSegment = SanitizeFileName(processName);

        return Path.Combine(outputDirectory, groupSegment, processSegment + ".log");
    }

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

    private static string ResolveShellPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var comspec = Environment.GetEnvironmentVariable("COMSPEC");
            return string.IsNullOrWhiteSpace(comspec) ? "cmd.exe" : comspec;
        }

        return "/bin/sh";
    }

    private static IReadOnlyList<string> BuildShellArguments(string command)
    {
        if (OperatingSystem.IsWindows())
            return new[] { "/c", command };

        return new[] { "-c", command };
    }

    private static string ResolveCommandDisplayPath(string baseDirectory, string command)
    {
        var token = GetFirstCommandToken(command);

        if (string.IsNullOrWhiteSpace(token))
            return command;

        if (!LooksLikePath(token))
            return token;

        var resolved = ResolvePath(baseDirectory, token);
        return Path.GetRelativePath(baseDirectory, resolved);
    }

    private static string? GetFirstCommandToken(string command)
    {
        var span = command.AsSpan().Trim();

        if (span.IsEmpty)
            return null;

        var quote = span[0] is '"' or '\'' ? span[0] : '\0';
        var start = quote == '\0' ? 0 : 1;

        for (var i = start; i < span.Length; i++)
        {
            var ch = span[i];

            if (quote != '\0')
            {
                if (ch == quote)
                    return span[start..i].ToString();

                continue;
            }

            if (char.IsWhiteSpace(ch))
                return span[start..i].ToString();
        }

        return span[start..].ToString();
    }

    private static bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token[0] is '~' or '$')
            return false;

        if (OperatingSystem.IsWindows() && token[0] == '%')
            return false;

        if (Path.IsPathRooted(token))
            return true;

        return token.Contains('/') || token.Contains('\\') || token.StartsWith(".", StringComparison.Ordinal);
    }
}
