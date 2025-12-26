// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

namespace Procvd.Configuration;

public sealed class ProcessConfig
{
    public ProcessSettings Defaults { get; init; } = ProcessSettings.Empty;

    public IReadOnlyDictionary<string, ProcessGroupConfig>? Groups { get; init; }

    public IReadOnlyDictionary<string, ProcessGroupSetConfig>? GroupSets { get; init; }
}
