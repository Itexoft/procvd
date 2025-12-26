// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Output;

namespace Procvd.Tests;

public sealed class TestOutputSink : IProcessOutputSink
{
    private readonly object sync = new();
    private readonly List<ProcessOutputLine> lines = [];
    private readonly List<ProcessOutputEvent> events = [];

    public IReadOnlyList<ProcessOutputLine> Lines
    {
        get
        {
            lock (this.sync)
                return this.lines.ToArray();
        }
    }

    public IReadOnlyList<ProcessOutputEvent> Events
    {
        get
        {
            lock (this.sync)
                return this.events.ToArray();
        }
    }

    public void Write(ProcessOutputLine line)
    {
        lock (this.sync)
            this.lines.Add(line);
    }

    public void WriteEvent(ProcessOutputEvent message)
    {
        lock (this.sync)
            this.events.Add(message);
    }

    public bool HasLine(Func<ProcessOutputLine, bool> predicate)
    {
        lock (this.sync)
            return this.lines.Any(predicate);
    }

    public bool HasEvent(Func<ProcessOutputEvent, bool> predicate)
    {
        lock (this.sync)
            return this.events.Any(predicate);
    }

    public int CountEvents(Func<ProcessOutputEvent, bool> predicate)
    {
        lock (this.sync)
            return this.events.Count(predicate);
    }
}
