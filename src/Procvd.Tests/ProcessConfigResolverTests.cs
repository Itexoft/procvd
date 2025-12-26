// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Configuration;

namespace Procvd.Tests;

public class ProcessConfigResolverTests
{
    [Test]
    public void Resolve_MergesGroupSetSettings()
    {
        var config = new ProcessConfig
        {
            Defaults = new ProcessSettings
            {
                Args = ["--global"],
                Env = new Dictionary<string, string?>
                {
                    ["A"] = "1",
                },
                OutputMode = ProcessOutputMode.Inherit,
            },
            Groups = new Dictionary<string, ProcessGroupConfig>
            {
                ["api"] = new()
                {
                    Processes = new Dictionary<string, ProcessConfigItem>
                    {
                        ["server"] = new()
                        {
                            Path = "bin/server",
                            Settings = new ProcessSettings
                            {
                                Args = ["--proc"],
                                Env = new Dictionary<string, string?>
                                {
                                    ["C"] = "3",
                                },
                            },
                        },
                    },
                },
            },
            GroupSets = new Dictionary<string, ProcessGroupSetConfig>
            {
                ["backend"] = new()
                {
                    Groups = ["api"],
                    Settings = new ProcessSettings
                    {
                        Args = ["--group"],
                        Env = new Dictionary<string, string?>
                        {
                            ["B"] = "2",
                            ["A"] = "override",
                        },
                        OutputMode = ProcessOutputMode.Inherit,
                    },
                },
            },
        };

        var resolver = new ProcessConfigResolver();
        var baseDirectory = Path.Combine(Path.GetTempPath(), "procvd-tests");
        var resolved = resolver.Resolve(config, baseDirectory);
        var process = resolved.Groups["api"].Processes[0];

        Assert.That(process.Arguments, Is.EqualTo(new[] { "--global", "--group", "--proc" }));
        Assert.That(process.Environment["A"], Is.EqualTo("override"));
        Assert.That(process.Environment["B"], Is.EqualTo("2"));
        Assert.That(process.Environment["C"], Is.EqualTo("3"));
        Assert.That(process.WorkingDirectory, Is.EqualTo(Path.GetFullPath(baseDirectory)));
        Assert.That(process.OutputMode, Is.EqualTo(ProcessOutputMode.Inherit));

        var expectedDisplay = Path.GetRelativePath(baseDirectory, process.ExecutablePath);
        Assert.That(process.DisplayPath, Is.EqualTo(expectedDisplay));
    }

    [Test]
    public void Resolve_OutputFileDefaults()
    {
        var config = new ProcessConfig
        {
            Defaults = new ProcessSettings
            {
                OutputMode = ProcessOutputMode.File,
            },
            Groups = new Dictionary<string, ProcessGroupConfig>
            {
                ["core"] = new()
                {
                    Processes = new Dictionary<string, ProcessConfigItem>
                    {
                        ["app"] = new()
                        {
                            Path = "bin/app",
                        },
                    },
                },
            },
        };

        var resolver = new ProcessConfigResolver();
        var baseDirectory = Path.Combine(Path.GetTempPath(), "procvd-tests", Guid.NewGuid().ToString("N"));
        var resolved = resolver.Resolve(config, baseDirectory);
        var process = resolved.Groups["core"].Processes[0];

        var expectedLog = Path.Combine(
            Path.GetFullPath(baseDirectory),
            "procvd-logs",
            "core",
            "app.log");

        Assert.That(process.OutputMode, Is.EqualTo(ProcessOutputMode.File));
        Assert.That(process.OutputPath, Is.EqualTo(expectedLog));
        Assert.That(process.OutputMaxBytes, Is.EqualTo(32L * 1024 * 1024));
        Assert.That(process.OutputMaxFiles, Is.EqualTo(3));
    }
}
