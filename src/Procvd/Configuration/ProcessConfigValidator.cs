// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

namespace Procvd.Configuration;

public static class ProcessConfigValidator
{
    public static void Validate(ProcessConfig config)
    {
        if (config is null)
            throw new ProcessConfigException("config is null");

        var groups = config.Groups;
        var groupSets = config.GroupSets;

        if (groups is null || groups.Count == 0)
            throw new ProcessConfigException("no groups defined");

        if (config.Defaults.RunAsUser is not null && string.IsNullOrWhiteSpace(config.Defaults.RunAsUser))
            throw new ProcessConfigException("defaults contain empty run_as user");

        foreach (var (groupName, group) in groups)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                throw new ProcessConfigException("group name is empty");

            if (group is null)
                throw new ProcessConfigException($"group '{groupName}' is null");

            if (group.Processes is null || group.Processes.Count == 0)
                throw new ProcessConfigException($"group '{groupName}' has no processes");

            if (group.Settings.RunAsUser is not null && string.IsNullOrWhiteSpace(group.Settings.RunAsUser))
                throw new ProcessConfigException($"group '{groupName}' has empty run_as user");

            foreach (var (processName, process) in group.Processes)
            {
                if (string.IsNullOrWhiteSpace(processName))
                    throw new ProcessConfigException($"group '{groupName}' has process with empty name");

                if (process is null)
                    throw new ProcessConfigException($"process '{processName}' in group '{groupName}' is null");

                var hasPath = !string.IsNullOrWhiteSpace(process.Path);
                var hasCommand = !string.IsNullOrWhiteSpace(process.Command);

                if (hasPath == hasCommand)
                    throw new ProcessConfigException($"process '{processName}' in group '{groupName}' must define either path or command");

                if (hasCommand && ProcessSettings.NormalizeArgs(process.Settings.Args).Count > 0)
                    throw new ProcessConfigException($"process '{processName}' in group '{groupName}' cannot combine command with args");

                if (process.Settings.RunAsUser is not null && string.IsNullOrWhiteSpace(process.Settings.RunAsUser))
                    throw new ProcessConfigException($"process '{processName}' in group '{groupName}' has empty run_as user");
            }
        }

        if (groupSets is null)
            return;

        foreach (var (setName, set) in groupSets)
        {
            if (string.IsNullOrWhiteSpace(setName))
                throw new ProcessConfigException("group set name is empty");

            if (set is null)
                throw new ProcessConfigException($"group set '{setName}' is null");
        }
    }
}
