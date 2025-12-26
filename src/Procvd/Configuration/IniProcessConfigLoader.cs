// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Text;
using Itexoft.Formats.Configuration.Ini;
using Itexoft.Threading;

namespace Procvd.Configuration;

public sealed class IniProcessConfigLoader(IniReaderOptions? options = null, string? defaultGroupName = null) : IProcessConfigLoader
{
    private const string defaultGroupName = "main";
    private static readonly StringComparer keyComparer = StringComparer.OrdinalIgnoreCase;
    private readonly IniReaderOptions optionsInternal = options ?? IniReaderOptions.Default;
    private readonly string defGroupName = string.IsNullOrWhiteSpace(defaultGroupName) ? IniProcessConfigLoader.defaultGroupName : defaultGroupName;

    public async ValueTask<ProcessConfig> LoadAsync(Stream stream, CancelToken token = default)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        using (token.Bridge(out var cancellationToken))
        using (var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true))
        {
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var document = IniDocument.Parse(text, this.optionsInternal);

            return Parse(document, this.defGroupName);
        }
    }

    private static ProcessConfig Parse(IniDocument document, string defaultGroupName)
    {
        var defaults = new SettingsBuilder();
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.Ordinal);
        var groupSets = new Dictionary<string, GroupSetBuilder>(StringComparer.Ordinal);

        var globalGroup = new GroupBuilder(defaultGroupName);
        ParseGlobalSection(document.Global, defaults, globalGroup);

        if (globalGroup.HasContent)
            groups[defaultGroupName] = globalGroup;

        foreach (var section in document.Sections)
        {
            if (IsDefaultsSection(section.Name))
            {
                ParseDefaultsSection(section, defaults);
                continue;
            }

            if (TryParseGroupSetName(section.Name, out var setName))
            {
                var builder = GetOrCreateGroupSet(groupSets, setName);
                ParseGroupSetSection(section, builder);
                continue;
            }

            if (TryParseProcessSectionName(section.Name, out var groupName, out var processName))
            {
                var group = GetOrCreateGroup(groups, groupName);
                var process = group.GetOrCreateProcess(processName);
                ParseProcessSection(section, process);
                continue;
            }

            var groupSectionName = TryParseGroupName(section.Name, out var parsedGroupName)
                ? parsedGroupName
                : NormalizeName(section.Name);
            var groupBuilder = GetOrCreateGroup(groups, groupSectionName);
            ParseGroupSection(section, groupBuilder);
        }

        return new ProcessConfig
        {
            Defaults = defaults.ToSettings(),
            Groups = BuildGroups(groups),
            GroupSets = groupSets.Count == 0 ? null : BuildGroupSets(groupSets),
        };
    }

    private static IReadOnlyDictionary<string, ProcessGroupConfig> BuildGroups(Dictionary<string, GroupBuilder> groups)
    {
        var result = new Dictionary<string, ProcessGroupConfig>(StringComparer.Ordinal);

        foreach (var (name, group) in groups)
            result[name] = group.ToConfig();

        return result;
    }

    private static IReadOnlyDictionary<string, ProcessGroupSetConfig> BuildGroupSets(Dictionary<string, GroupSetBuilder> groupSets)
    {
        var result = new Dictionary<string, ProcessGroupSetConfig>(StringComparer.Ordinal);

        foreach (var (name, set) in groupSets)
            result[name] = set.ToConfig();

        return result;
    }

    private static GroupBuilder GetOrCreateGroup(Dictionary<string, GroupBuilder> groups, string name)
    {
        if (!groups.TryGetValue(name, out var group))
        {
            group = new GroupBuilder(name);
            groups[name] = group;
        }

        return group;
    }

    private static GroupSetBuilder GetOrCreateGroupSet(Dictionary<string, GroupSetBuilder> groupSets, string name)
    {
        if (!groupSets.TryGetValue(name, out var groupSet))
        {
            groupSet = new GroupSetBuilder(name);
            groupSets[name] = groupSet;
        }

        return groupSet;
    }

    private static void ParseGlobalSection(IniSection section, SettingsBuilder defaults, GroupBuilder group)
    {
        foreach (var entry in section.Entries)
        {
            switch (entry)
            {
                case IniValueEntry valueEntry:
                    AddProcessCommand(group, valueEntry.Value);
                    break;
                case IniKeyValueEntry keyValue:
                    if (TryApplyProcessKey(group, keyValue.Key.Text, keyValue.Values))
                        break;

                    if (TryApplySettings(defaults, keyValue.Key.Text, keyValue.Values))
                        break;

                    AddNamedProcess(group, keyValue.Key.Text, keyValue.Values);
                    break;
            }
        }
    }

    private static void ParseDefaultsSection(IniSection section, SettingsBuilder defaults)
    {
        foreach (var entry in section.Entries)
        {
            if (entry is IniValueEntry valueEntry && !valueEntry.Value.IsEmpty)
                throw new ProcessConfigException("defaults section cannot contain process entries");

            if (entry is not IniKeyValueEntry keyValue)
                continue;

            if (!TryApplySettings(defaults, keyValue.Key.Text, keyValue.Values))
                throw new ProcessConfigException($"defaults section contains unknown key '{keyValue.Key.Text}'");
        }
    }

    private static void ParseGroupSection(IniSection section, GroupBuilder group)
    {
        foreach (var entry in section.Entries)
        {
            switch (entry)
            {
                case IniValueEntry valueEntry:
                    AddProcessCommand(group, valueEntry.Value);
                    break;
                case IniKeyValueEntry keyValue:
                    if (TryApplyProcessKey(group, keyValue.Key.Text, keyValue.Values))
                        break;

                    if (TryApplyGroupSettings(group, keyValue.Key.Text, keyValue.Values))
                        break;

                    AddNamedProcess(group, keyValue.Key.Text, keyValue.Values);
                    break;
            }
        }
    }

    private static void ParseProcessSection(IniSection section, ProcessBuilder process)
    {
        foreach (var entry in section.Entries)
        {
            switch (entry)
            {
                case IniValueEntry valueEntry:
                    ApplyProcessField(process, "path", valueEntry.Value);
                    break;
                case IniKeyValueEntry keyValue:
                    ApplyProcessField(process, keyValue.Key.Text, keyValue.Values);
                    break;
            }
        }
    }

    private static void ParseGroupSetSection(IniSection section, GroupSetBuilder groupSet)
    {
        foreach (var entry in section.Entries)
        {
            switch (entry)
            {
                case IniValueEntry valueEntry:
                    AddListValues(groupSet.Groups, valueEntry.Value);
                    break;
                case IniKeyValueEntry keyValue:
                    if (TryApplyGroupSetSettings(groupSet, keyValue.Key.Text, keyValue.Values))
                        break;

                    throw new ProcessConfigException($"group set '{groupSet.Name}' contains unknown key '{keyValue.Key.Text}'");
            }
        }
    }

    private static void AddProcessCommand(GroupBuilder group, IniValue value)
    {
        var command = NormalizeValue(value);

        if (string.IsNullOrWhiteSpace(command))
            return;

        var name = CreateProcessNameFromCommand(command);
        name = group.GetUniqueProcessName(name);

        var process = group.GetOrCreateProcess(name);
        process.SetCommand(command);
    }

    private static void AddNamedProcess(GroupBuilder group, string name, IniValueCollection values)
    {
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ProcessConfigException("process name is empty");

        var process = group.GetOrCreateProcess(normalized);
        var command = NormalizeValue(values.FirstOrDefault);

        if (string.IsNullOrWhiteSpace(command))
            throw new ProcessConfigException($"process '{normalized}' has empty command");

        process.SetCommand(command);
    }

    private static bool TryApplyGroupSettings(GroupBuilder group, string key, IniValueCollection values)
    {
        if (TryApplySettings(group.Settings, key, values))
            return true;

        if (IsKey(key, "depends") || IsKey(key, "depends_on"))
        {
            AddListValues(group.DependsOn, values);
            return true;
        }

        if (IsKey(key, "restart") || IsKey(key, "restart_mode") || IsKey(key, "mode"))
        {
            var mode = NormalizeValue(values.FirstOrDefault);
            group.RestartMode = ParseRestartMode(mode);
            return true;
        }

        if (IsKey(key, "restart_delay_seconds"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            group.RestartDelay = ParseDelay(raw, key, TimeSpan.FromSeconds);
            return true;
        }

        if (IsKey(key, "restart_delay_ms"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            group.RestartDelay = ParseDelay(raw, key, TimeSpan.FromMilliseconds);
            return true;
        }

        if (IsKey(key, "restart_delay"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            group.RestartDelay = ParseDelay(raw, key, null);
            return true;
        }

        if (IsKey(key, "restart_limit") || IsKey(key, "restart_max") || IsKey(key, "restart_max_restarts"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            group.MaxRestarts = ParseMaxRestarts(raw, key);
            return true;
        }

        return false;
    }

    private static bool TryApplyGroupSetSettings(GroupSetBuilder groupSet, string key, IniValueCollection values)
    {
        if (TryApplySettings(groupSet.Settings, key, values))
            return true;

        if (IsKey(key, "groups") || IsKey(key, "group"))
        {
            AddListValues(groupSet.Groups, values);
            return true;
        }

        if (IsKey(key, "sets") || IsKey(key, "set") || IsKey(key, "groupsets"))
        {
            AddListValues(groupSet.GroupSets, values);
            return true;
        }

        if (IsKey(key, "depends") || IsKey(key, "depends_on"))
        {
            AddListValues(groupSet.DependsOn, values);
            return true;
        }

        return false;
    }

    private static bool TryApplyProcessKey(GroupBuilder group, string key, IniValueCollection values)
    {
        if (!TryParseProcessKey(key, out var processName, out var field))
            return false;

        var process = group.GetOrCreateProcess(processName);
        ApplyProcessField(process, field, values);

        return true;
    }

    private static void ApplyProcessField(ProcessBuilder process, string field, IniValueCollection values)
    {
        if (IsKey(field, "path") || string.IsNullOrWhiteSpace(field))
        {
            process.SetPath(NormalizeValue(values.FirstOrDefault));
            return;
        }

        if (IsKey(field, "arg") || IsKey(field, "argv"))
        {
            if (process.HasCommand)
                throw new ProcessConfigException($"process '{process.Name}' cannot combine command with args");

            foreach (var value in values)
                process.Settings.AddArg(NormalizeValue(value));
            return;
        }

        if (IsKey(field, "args"))
        {
            if (process.HasCommand)
                throw new ProcessConfigException($"process '{process.Name}' cannot combine command with args");

            foreach (var value in values)
                process.Settings.AddArgs(SplitTokens(value.Span));
            return;
        }

        if (TryGetEnvKey(field, out var envKey))
        {
            foreach (var value in values)
                process.Settings.SetEnv(envKey, NormalizeValue(value));
            return;
        }

        if (IsKey(field, "workdir") || IsKey(field, "working_dir") || IsKey(field, "workingdir"))
        {
            process.Settings.WorkingDirectory = NormalizeValue(values.FirstOrDefault);
            return;
        }

        if (IsKey(field, "output") || IsKey(field, "output_mode") || IsKey(field, "outputmode"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            process.Settings.OutputMode = ParseOutputMode(raw, field);
            return;
        }

        if (IsKey(field, "output_dir") || IsKey(field, "output_directory") || IsKey(field, "log_dir"))
        {
            process.Settings.OutputDirectory = NormalizeValue(values.FirstOrDefault);
            return;
        }

        if (IsKey(field, "output_max_bytes") || IsKey(field, "output_max_size") || IsKey(field, "output_max"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            process.Settings.OutputMaxBytes = ParseSize(raw, field);
            return;
        }

        if (IsKey(field, "output_max_files") || IsKey(field, "output_files"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            process.Settings.OutputMaxFiles = ParseInt(raw, field);
            return;
        }

        if (IsKey(field, "enabled"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            process.Enabled = ParseBoolean(raw, field);
            return;
        }

        throw new ProcessConfigException($"process '{process.Name}' has unknown field '{field}'");
    }

    private static void ApplyProcessField(ProcessBuilder process, string field, IniValue value)
    {
        if (IsKey(field, "path") || string.IsNullOrWhiteSpace(field))
        {
            process.SetPath(NormalizeValue(value));
            return;
        }

        if (IsKey(field, "arg") || IsKey(field, "argv"))
        {
            if (process.HasCommand)
                throw new ProcessConfigException($"process '{process.Name}' cannot combine command with args");

            process.Settings.AddArg(NormalizeValue(value));
            return;
        }

        if (IsKey(field, "args"))
        {
            if (process.HasCommand)
                throw new ProcessConfigException($"process '{process.Name}' cannot combine command with args");

            process.Settings.AddArgs(SplitTokens(value.Span));
            return;
        }

        if (TryGetEnvKey(field, out var envKey))
        {
            process.Settings.SetEnv(envKey, NormalizeValue(value));
            return;
        }

        if (IsKey(field, "workdir") || IsKey(field, "working_dir") || IsKey(field, "workingdir"))
        {
            process.Settings.WorkingDirectory = NormalizeValue(value);
            return;
        }

        if (IsKey(field, "output") || IsKey(field, "output_mode") || IsKey(field, "outputmode"))
        {
            var raw = NormalizeValue(value);
            process.Settings.OutputMode = ParseOutputMode(raw, field);
            return;
        }

        if (IsKey(field, "output_dir") || IsKey(field, "output_directory") || IsKey(field, "log_dir"))
        {
            process.Settings.OutputDirectory = NormalizeValue(value);
            return;
        }

        if (IsKey(field, "output_max_bytes") || IsKey(field, "output_max_size") || IsKey(field, "output_max"))
        {
            var raw = NormalizeValue(value);
            process.Settings.OutputMaxBytes = ParseSize(raw, field);
            return;
        }

        if (IsKey(field, "output_max_files") || IsKey(field, "output_files"))
        {
            var raw = NormalizeValue(value);
            process.Settings.OutputMaxFiles = ParseInt(raw, field);
            return;
        }

        if (IsKey(field, "enabled"))
        {
            var raw = NormalizeValue(value);
            process.Enabled = ParseBoolean(raw, field);
            return;
        }

        throw new ProcessConfigException($"process '{process.Name}' has unknown field '{field}'");
    }

    private static bool TryApplySettings(SettingsBuilder settings, string key, IniValueCollection values)
    {
        if (IsKey(key, "arg") || IsKey(key, "argv"))
        {
            foreach (var value in values)
                settings.AddArg(NormalizeValue(value));
            return true;
        }

        if (IsKey(key, "args"))
        {
            foreach (var value in values)
                settings.AddArgs(SplitTokens(value.Span));
            return true;
        }

        if (TryGetEnvKey(key, out var envKey))
        {
            foreach (var value in values)
                settings.SetEnv(envKey, NormalizeValue(value));
            return true;
        }

        if (IsKey(key, "workdir") || IsKey(key, "working_dir") || IsKey(key, "workingdir"))
        {
            settings.WorkingDirectory = NormalizeValue(values.FirstOrDefault);
            return true;
        }

        if (IsKey(key, "output") || IsKey(key, "output_mode") || IsKey(key, "outputmode"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            settings.OutputMode = ParseOutputMode(raw, key);
            return true;
        }

        if (IsKey(key, "output_dir") || IsKey(key, "output_directory") || IsKey(key, "log_dir"))
        {
            settings.OutputDirectory = NormalizeValue(values.FirstOrDefault);
            return true;
        }

        if (IsKey(key, "output_max_bytes") || IsKey(key, "output_max_size") || IsKey(key, "output_max"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            settings.OutputMaxBytes = ParseSize(raw, key);
            return true;
        }

        if (IsKey(key, "output_max_files") || IsKey(key, "output_files"))
        {
            var raw = NormalizeValue(values.FirstOrDefault);
            settings.OutputMaxFiles = ParseInt(raw, key);
            return true;
        }

        return false;
    }

    private static bool TryParseProcessKey(string key, out string processName, out string field)
    {
        if (TryParsePrefixed(key, "process", out var remainder) || TryParsePrefixed(key, "proc", out remainder))
        {
            var index = remainder.IndexOf('.');
            if (index < 0)
            {
                processName = NormalizeName(remainder);
                field = "path";
                return !string.IsNullOrWhiteSpace(processName);
            }

            processName = NormalizeName(remainder[..index]);
            field = NormalizeName(remainder[(index + 1)..]);
            return !string.IsNullOrWhiteSpace(processName);
        }

        processName = string.Empty;
        field = string.Empty;
        return false;
    }

    private static bool TryGetEnvKey(string key, out string envKey)
    {
        if (!TryParsePrefixed(key, "env", out var remainder))
        {
            envKey = string.Empty;
            return false;
        }

        envKey = NormalizeName(remainder);
        return !string.IsNullOrWhiteSpace(envKey);
    }

    private static bool TryParseGroupSetName(string name, out string setName)
    {
        if (TryParsePrefixed(name, "set", out var remainder) || TryParsePrefixed(name, "groupset", out remainder))
        {
            setName = NormalizeName(remainder);
            return !string.IsNullOrWhiteSpace(setName);
        }

        setName = string.Empty;
        return false;
    }

    private static bool TryParseGroupName(string name, out string groupName)
    {
        if (TryParsePrefixed(name, "group", out var remainder))
        {
            groupName = NormalizeName(remainder);
            return !string.IsNullOrWhiteSpace(groupName);
        }

        groupName = string.Empty;
        return false;
    }

    private static bool TryParseProcessSectionName(string name, out string groupName, out string processName)
    {
        if (!TryParsePrefixed(name, "process", out var remainder) && !TryParsePrefixed(name, "proc", out remainder))
        {
            groupName = string.Empty;
            processName = string.Empty;
            return false;
        }

        var separatorIndex = remainder.IndexOfAny(['/', ':']);
        if (separatorIndex <= 0 || separatorIndex >= remainder.Length - 1)
            throw new ProcessConfigException($"process section '{name}' must be in format process:group/name");

        groupName = NormalizeName(remainder[..separatorIndex]);
        processName = NormalizeName(remainder[(separatorIndex + 1)..]);

        if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(processName))
            throw new ProcessConfigException($"process section '{name}' must specify group and process names");

        return true;
    }

    private static bool IsDefaultsSection(string name) =>
        IsKey(name, "defaults") || IsKey(name, "default");

    private static bool TryParsePrefixed(string value, string prefix, out string remainder)
    {
        if (value.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
        {
            remainder = value[(prefix.Length + 1)..];
            return true;
        }

        if (value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
        {
            remainder = value[(prefix.Length + 1)..];
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    private static bool IsKey(string key, string expected) => keyComparer.Equals(key, expected);

    private static string NormalizeName(string value) => value.Trim();

    private static string NormalizeValue(IniValue value) => TrimQuotes(value.ToString());

    private static string TrimQuotes(string value)
    {
        var span = value.AsSpan().Trim();

        if (span.Length >= 2)
        {
            if ((span[0] == '"' && span[^1] == '"') || (span[0] == '\'' && span[^1] == '\''))
                span = span[1..^1];
        }

        return span.ToString();
    }

    private static string CreateProcessNameFromCommand(string command)
    {
        var executable = GetFirstCommandToken(command);
        if (string.IsNullOrWhiteSpace(executable))
            return "process";

        var name = Path.GetFileNameWithoutExtension(executable);
        return string.IsNullOrWhiteSpace(name) ? "process" : name;
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

    private static void AddListValues(List<string> target, IniValue value)
    {
        foreach (var item in SplitTokens(value.Span))
            target.Add(item);
    }

    private static void AddListValues(List<string> target, IniValueCollection values)
    {
        foreach (var value in values)
            AddListValues(target, value);
    }

    private static List<string> SplitTokens(ReadOnlySpan<char> span)
    {
        var result = new List<string>();
        var index = 0;

        while (index < span.Length)
        {
            while (index < span.Length && IsSeparator(span[index]))
                index++;

            if (index >= span.Length)
                break;

            var quote = span[index] is '"' or '\'' ? span[index++] : '\0';
            var start = index;

            while (index < span.Length)
            {
                var ch = span[index];
                if (quote != '\0')
                {
                    if (ch == quote)
                        break;
                }
                else if (IsSeparator(ch))
                {
                    break;
                }

                index++;
            }

            var token = span[start..index].Trim();
            if (!token.IsEmpty)
                result.Add(token.ToString());

            if (quote != '\0' && index < span.Length && span[index] == quote)
                index++;
        }

        return result;
    }

    private static bool IsSeparator(char ch) => char.IsWhiteSpace(ch) || ch == ',';

    private static GroupRestartMode ParseRestartMode(string value)
    {
        if (IsKey(value, "group"))
            return GroupRestartMode.Group;

        if (IsKey(value, "process") || IsKey(value, "proc"))
            return GroupRestartMode.Process;

        throw new ProcessConfigException($"invalid restart mode '{value}'");
    }

    private static int? ParseMaxRestarts(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ProcessConfigException($"invalid {key} value '{value}'");

        return parsed;
    }

    private static TimeSpan ParseDelay(string value, string key, Func<double, TimeSpan>? defaultFactory)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TimeSpan.Zero;

        if (defaultFactory is not null && double.TryParse(value, out var parsed))
            return defaultFactory(parsed);

        if (TryParseDuration(value, out var delay))
            return delay;

        throw new ProcessConfigException($"invalid {key} value '{value}'");
    }

    private static bool ParseBoolean(string value, string key)
    {
        if (bool.TryParse(value, out var parsed))
            return parsed;

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            return false;

        throw new ProcessConfigException($"invalid {key} value '{value}'");
    }

    private static ProcessOutputMode ParseOutputMode(string value, string key)
    {
        if (string.Equals(value, "inherit", StringComparison.OrdinalIgnoreCase))
            return ProcessOutputMode.Inherit;

        if (string.Equals(value, "file", StringComparison.OrdinalIgnoreCase))
            return ProcessOutputMode.File;

        throw new ProcessConfigException($"invalid {key} value '{value}'");
    }

    private static long ParseSize(string value, string key)
    {
        if (!TryParseSize(value, out var size))
            throw new ProcessConfigException($"invalid {key} value '{value}'");

        return size;
    }

    private static bool TryParseSize(string value, out long size)
    {
        size = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var span = value.AsSpan().Trim();
        var suffixLength = 0;
        long multiplier = 1;

        if (span.Length >= 2)
        {
            var last = span[^1];
            var prev = span[^2];

            if ((prev == 'k' || prev == 'K') && (last == 'b' || last == 'B'))
            {
                multiplier = 1024;
                suffixLength = 2;
            }
            else if ((prev == 'm' || prev == 'M') && (last == 'b' || last == 'B'))
            {
                multiplier = 1024 * 1024;
                suffixLength = 2;
            }
            else if ((prev == 'g' || prev == 'G') && (last == 'b' || last == 'B'))
            {
                multiplier = 1024L * 1024 * 1024;
                suffixLength = 2;
            }
        }

        if (suffixLength == 0 && span.Length >= 1)
        {
            var last = span[^1];
            if (last == 'k' || last == 'K')
            {
                multiplier = 1024;
                suffixLength = 1;
            }
            else if (last == 'm' || last == 'M')
            {
                multiplier = 1024 * 1024;
                suffixLength = 1;
            }
            else if (last == 'g' || last == 'G')
            {
                multiplier = 1024L * 1024 * 1024;
                suffixLength = 1;
            }
        }

        var numberSpan = suffixLength == 0 ? span : span[..^suffixLength].Trim();

        if (numberSpan.Length == 0)
            return false;

        if (!double.TryParse(numberSpan, out var parsed))
            return false;

        if (parsed < 0)
            return false;

        size = (long)Math.Round(parsed * multiplier, MidpointRounding.AwayFromZero);
        return true;
    }

    private static int ParseInt(string value, string key)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ProcessConfigException($"invalid {key} value '{value}'");

        return parsed;
    }

    private static bool TryParseDuration(string value, out TimeSpan result)
    {
        if (TimeSpan.TryParse(value, out result))
            return true;

        var span = value.AsSpan().Trim();

        if (span.Length < 2)
            return TryParseMilliseconds(span, out result);

        var unit = span[^1];
        var numberSpan = span[..^1].Trim();

        if (unit == 's' || unit == 'S')
            return TryParse(numberSpan, TimeSpan.FromSeconds, out result);

        if (unit == 'm' || unit == 'M')
            return TryParse(numberSpan, TimeSpan.FromMinutes, out result);

        if (unit == 'h' || unit == 'H')
            return TryParse(numberSpan, TimeSpan.FromHours, out result);

        if (unit == 'd' || unit == 'D')
            return TryParse(numberSpan, TimeSpan.FromDays, out result);

        if (span.Length > 2 && (span[^2] == 'm' || span[^2] == 'M') && (unit == 's' || unit == 'S'))
            return TryParse(span[..^2].Trim(), TimeSpan.FromMilliseconds, out result);

        return TryParseMilliseconds(span, out result);
    }

    private static bool TryParseMilliseconds(ReadOnlySpan<char> value, out TimeSpan result)
    {
        if (TryParse(value, TimeSpan.FromMilliseconds, out result))
            return true;

        result = TimeSpan.Zero;
        return false;
    }

    private static bool TryParse(ReadOnlySpan<char> value, Func<double, TimeSpan> factory, out TimeSpan result)
    {
        if (double.TryParse(value, out var parsed))
        {
            result = factory(parsed);
            return true;
        }

        result = TimeSpan.Zero;
        return false;
    }

    private sealed class SettingsBuilder
    {
        private readonly List<string> args = [];
        private readonly Dictionary<string, string?> env = new(StringComparer.Ordinal);

        public string? WorkingDirectory { get; set; }

        public ProcessOutputMode? OutputMode { get; set; }

        public string? OutputDirectory { get; set; }

        public long? OutputMaxBytes { get; set; }

        public int? OutputMaxFiles { get; set; }

        public void AddArg(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                this.args.Add(value);
        }

        public void AddArgs(IEnumerable<string> values)
        {
            foreach (var value in values)
                this.AddArg(value);
        }

        public void SetEnv(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                value = null;

            this.env[key] = value;
        }

        public bool HasArgs => this.args.Count > 0;

        public ProcessSettings ToSettings()
        {
            if (this.args.Count == 0 && this.env.Count == 0 && string.IsNullOrWhiteSpace(this.WorkingDirectory)
                && this.OutputMode is null && string.IsNullOrWhiteSpace(this.OutputDirectory)
                && this.OutputMaxBytes is null && this.OutputMaxFiles is null)
                return ProcessSettings.Empty;

            return new ProcessSettings
            {
                Args = this.args.Count == 0 ? null : this.args.ToArray(),
                Env = this.env.Count == 0 ? null : new Dictionary<string, string?>(this.env, StringComparer.Ordinal),
                WorkingDirectory = string.IsNullOrWhiteSpace(this.WorkingDirectory) ? null : this.WorkingDirectory,
                OutputMode = this.OutputMode,
                OutputDirectory = string.IsNullOrWhiteSpace(this.OutputDirectory) ? null : this.OutputDirectory,
                OutputMaxBytes = this.OutputMaxBytes,
                OutputMaxFiles = this.OutputMaxFiles,
            };
        }
    }

    private sealed class ProcessBuilder(string name)
    {
        public string Name { get; } = name;

        public string? Path { get; private set; }

        public string? Command { get; private set; }

        public SettingsBuilder Settings { get; } = new();

        public bool Enabled { get; set; } = true;

        public bool HasCommand => !string.IsNullOrWhiteSpace(this.Command);

        public void SetCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ProcessConfigException($"process '{this.Name}' has empty command");

            if (!string.IsNullOrWhiteSpace(this.Path))
                throw new ProcessConfigException($"process '{this.Name}' cannot combine command with path");

            if (this.Settings.HasArgs)
                throw new ProcessConfigException($"process '{this.Name}' cannot combine command with args");

            if (!string.IsNullOrWhiteSpace(this.Command))
                throw new ProcessConfigException($"process '{this.Name}' has duplicate command");

            this.Command = command;
        }

        public void SetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ProcessConfigException($"process '{this.Name}' has empty path");

            if (!string.IsNullOrWhiteSpace(this.Command))
                throw new ProcessConfigException($"process '{this.Name}' cannot combine path with command");

            this.Path = path;
        }

        public ProcessConfigItem ToConfig()
        {
            if (!string.IsNullOrWhiteSpace(this.Command))
                return new ProcessConfigItem
                {
                    Command = this.Command,
                    Settings = this.Settings.ToSettings(),
                    Enabled = this.Enabled,
                };

            if (string.IsNullOrWhiteSpace(this.Path))
                throw new ProcessConfigException($"process '{this.Name}' has empty path");

            return new ProcessConfigItem
            {
                Path = this.Path,
                Settings = this.Settings.ToSettings(),
                Enabled = this.Enabled,
            };
        }
    }

    private sealed class GroupBuilder
    {
        private readonly Dictionary<string, ProcessBuilder> processes = new(StringComparer.Ordinal);

        public GroupBuilder(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ProcessConfigException("group name is empty");

            this.Name = name;
        }

        public string Name { get; }

        public SettingsBuilder Settings { get; } = new();

        public List<string> DependsOn { get; } = [];

        public GroupRestartMode RestartMode { get; set; } = GroupRestartMode.Process;

        public TimeSpan RestartDelay { get; set; } = TimeSpan.Zero;

        public int? MaxRestarts { get; set; }

        public bool HasContent => this.processes.Count > 0;

        public ProcessBuilder GetOrCreateProcess(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ProcessConfigException($"group '{this.Name}' has process with empty name");

            if (!this.processes.TryGetValue(name, out var process))
            {
                process = new ProcessBuilder(name);
                this.processes[name] = process;
            }

            return process;
        }

        public string GetUniqueProcessName(string baseName)
        {
            var normalized = NormalizeName(baseName);
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = "process";

            if (!this.processes.ContainsKey(normalized))
                return normalized;

            var suffix = 2;
            while (true)
            {
                var candidate = $"{normalized}-{suffix}";
                if (!this.processes.ContainsKey(candidate))
                    return candidate;

                suffix++;
            }
        }

        public ProcessGroupConfig ToConfig()
        {
            var processConfigs = new Dictionary<string, ProcessConfigItem>(StringComparer.Ordinal);

            foreach (var (name, process) in this.processes)
                processConfigs[name] = process.ToConfig();

            return new ProcessGroupConfig
            {
                DependsOn = this.DependsOn.Count == 0 ? null : this.DependsOn.ToArray(),
                Settings = this.Settings.ToSettings(),
                RestartMode = this.RestartMode,
                RestartPolicy = new ProcessRestartPolicy
                {
                    MaxRestarts = this.MaxRestarts,
                    RestartDelay = this.RestartDelay,
                },
                Processes = processConfigs,
            };
        }
    }

    private sealed class GroupSetBuilder
    {
        public GroupSetBuilder(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ProcessConfigException("group set name is empty");

            this.Name = name;
        }

        public string Name { get; }

        public SettingsBuilder Settings { get; } = new();

        public List<string> Groups { get; } = [];

        public List<string> GroupSets { get; } = [];

        public List<string> DependsOn { get; } = [];

        public ProcessGroupSetConfig ToConfig() => new()
        {
            Groups = this.Groups.Count == 0 ? null : this.Groups.ToArray(),
            GroupSets = this.GroupSets.Count == 0 ? null : this.GroupSets.ToArray(),
            DependsOn = this.DependsOn.Count == 0 ? null : this.DependsOn.ToArray(),
            Settings = this.Settings.ToSettings(),
        };
    }
}
