// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Output;

namespace Procvd.Tests;

public sealed class TestOutputSink : IProcessOutputSink
{
    private readonly object sync = new();

    public List<ProcessOutputLine> Lines { get; } = new();

    public List<ProcessOutputEvent> Events { get; } = new();

    public void Write(ProcessOutputLine line)
    {
        lock (this.sync)
            this.Lines.Add(line);
    }

    public void WriteEvent(ProcessOutputEvent message)
    {
        lock (this.sync)
            this.Events.Add(message);
    }
}
