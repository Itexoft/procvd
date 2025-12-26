// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Text;
using Itexoft.IO;
using Itexoft.IO.Streams.Chars;
using Procvd.Configuration;

namespace Procvd.Tests;

public class IniProcessConfigLoaderTests
{
    [Test]
    public async Task LoadAsync_MinimalPathCreatesDefaultGroup()
    {
        var config = Load("./bin/api");

        var group = config.Groups?.GetValueOrDefault("main");

        Assert.That(group, Is.Not.Null);
        Assert.That(group!.Processes, Is.Not.Null);
        Assert.That(group.Processes!.Count, Is.EqualTo(1));

        var process = group.Processes["api"];
        Assert.That(process.Command, Is.EqualTo("./bin/api"));
        Assert.That(process.Path, Is.Null);
    }

    [Test]
    public async Task LoadAsync_GroupSettingsAndProcessOverrides()
    {
        var ini = """
                  [defaults]
                  arg = --global
                  env.GLOBAL = yes

                  [core]
                  depends = db
                  restart = group
                  restart_delay_seconds = 2
                  arg = --group
                  env.GROUP = 1
                  process.api.path = ./api
                  process.api.arg = --port=5000
                  process.api.env.PORT = 5000
                  """;

        var config = Load(ini);

        var defaultsArgs = ProcessSettings.NormalizeArgs(config.Defaults.Args);
        var defaultsEnv = ProcessSettings.NormalizeEnv(config.Defaults.Env);

        Assert.That(defaultsArgs, Is.EqualTo(new[] { "--global" }));
        Assert.That(defaultsEnv["GLOBAL"], Is.EqualTo("yes"));

        var group = config.Groups!["core"];
        Assert.That(group.DependsOn, Is.EquivalentTo(new[] { "db" }));
        Assert.That(group.RestartMode, Is.EqualTo(GroupRestartMode.Group));
        Assert.That(group.RestartPolicy.RestartDelay, Is.EqualTo(TimeSpan.FromSeconds(2)));

        var groupArgs = ProcessSettings.NormalizeArgs(group.Settings.Args);
        var groupEnv = ProcessSettings.NormalizeEnv(group.Settings.Env);
        Assert.That(groupArgs, Is.EqualTo(new[] { "--group" }));
        Assert.That(groupEnv["GROUP"], Is.EqualTo("1"));

        var process = group.Processes!["api"];
        Assert.That(process.Path, Is.EqualTo("./api"));
        Assert.That(process.Command, Is.Null);

        var processArgs = ProcessSettings.NormalizeArgs(process.Settings.Args);
        var processEnv = ProcessSettings.NormalizeEnv(process.Settings.Env);
        Assert.That(processArgs, Is.EqualTo(new[] { "--port=5000" }));
        Assert.That(processEnv["PORT"], Is.EqualTo("5000"));
    }

    [Test]
    public async Task LoadAsync_GroupSetMapsGroups()
    {
        var ini = """
                  [core]
                  ./core

                  [api]
                  ./api

                  [set:backend]
                  groups = core, api
                  depends = base
                  arg = --backend
                  """;

        var config = Load(ini);

        var set = config.GroupSets!["backend"];
        Assert.That(set.Groups, Is.EquivalentTo(new[] { "core", "api" }));
        Assert.That(set.DependsOn, Is.EquivalentTo(new[] { "base" }));

        var setArgs = ProcessSettings.NormalizeArgs(set.Settings.Args);
        Assert.That(setArgs, Is.EqualTo(new[] { "--backend" }));
    }

    [Test]
    public async Task LoadAsync_OutputModeSettings()
    {
        var ini = """
                  [defaults]
                  output = file
                  output_max_bytes = 10mb
                  output_max_files = 2
                  output_dir = logs

                  [core]
                  api = ./api
                  process.api.output = inherit
                  """;

        var config = Load(ini);

        Assert.That(config.Defaults.OutputMode, Is.EqualTo(ProcessOutputMode.File));
        Assert.That(config.Defaults.OutputMaxBytes, Is.EqualTo(10 * 1024 * 1024));
        Assert.That(config.Defaults.OutputMaxFiles, Is.EqualTo(2));
        Assert.That(config.Defaults.OutputDirectory, Is.EqualTo("logs"));

        var process = config.Groups!["core"].Processes!["api"];
        Assert.That(process.Settings.OutputMode, Is.EqualTo(ProcessOutputMode.Inherit));
    }

    [Test]
    public async Task LoadAsync_RunAsUserSettings()
    {
        var ini = """
                  [defaults]
                  run_as = root

                  [core]
                  api = ./api
                  process.api.run_as = router
                  """;

        var config = Load(ini);

        Assert.That(config.Defaults.RunAsUser, Is.EqualTo("root"));

        var process = config.Groups!["core"].Processes!["api"];
        Assert.That(process.Settings.RunAsUser, Is.EqualTo("router"));
    }

    [Test]
    public void LoadAsync_CommandAndArgsConflict()
    {
        var ini = """
                  [main]
                  api = ./api --port 5000
                  process.api.arg = --port=5001
                  """;

        Assert.Throws<ProcessConfigException>(() => Load(ini));
    }

    private static ProcessConfig Load(string ini)
    {
        var loader = new IniProcessConfigLoader();

        return loader.Load(new CharStreamBrws(new StreamTrws<byte>(Encoding.UTF8.GetBytes(ini)), Encoding.UTF8));
    }
}
