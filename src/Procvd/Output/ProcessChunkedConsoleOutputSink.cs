// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Collections.Concurrent;
using Itexoft.Threading;
using Procvd.Runtime;

namespace Procvd.Output;

public sealed class ProcessChunkedConsoleOutputSink : IProcessOutputSink, IAsyncDisposable
{
    private readonly Lock sync = new();
    private readonly ProcessOutputFormatter formatterInternal;
    private readonly ConcurrentDictionary<ProcessKey, ProcessBuffer> buffers = new();
    private readonly ConcurrentDictionary<ProcessKey, byte> active = new();
    private readonly ConcurrentQueue<ProcessKey> activeQueue = new();
    private readonly TimeSpan flushInterval;
    private readonly object stopSource = new();
    private readonly CancelToken stopToken;
    private readonly Task flushTask;

    public ProcessChunkedConsoleOutputSink(TimeSpan? flushInterval = null, ProcessOutputFormatter? formatter = null)
    {
        this.flushInterval = flushInterval is null || flushInterval.Value <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : flushInterval.Value;

        this.formatterInternal = formatter ?? new ProcessOutputFormatter();
        this.stopToken = new CancelToken(this.stopSource);
        this.flushTask = this.RunAsync();
    }

    public void Write(ProcessOutputLine line)
    {
        var buffer = this.buffers.GetOrAdd(line.Process, static _ => new ProcessBuffer());
        buffer.Enqueue(line);

        if (this.active.TryAdd(line.Process, 0))
            this.activeQueue.Enqueue(line.Process);
    }

    public void WriteEvent(ProcessOutputEvent message)
    {
        this.active.TryRemove(message.Process, out _);
        this.FlushProcessCore(message.Process);

        var text = this.formatterInternal.Format(message);
        this.WriteWithColor(text, ConsoleColor.DarkGray);
    }

    public async ValueTask DisposeAsync()
    {
        this.stopToken.Cancel();
        await this.flushTask.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        using var bridge = this.stopToken.Bridge(out var cancellationToken);
        using var timer = new PeriodicTimer(this.flushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                this.Flush();
        }
        catch (OperationCanceledException)
        {
            // ignore
        }

        this.Flush();
    }

    private void Flush()
    {
        while (this.activeQueue.TryDequeue(out var process))
        {
            this.active.TryRemove(process, out _);
            this.FlushProcessCore(process);
        }
    }

    private void FlushProcessCore(ProcessKey process)
    {
        if (!this.buffers.TryGetValue(process, out var buffer))
            return;

        var lines = buffer.Drain();

        if (lines.Count == 0)
            return;

        var color = ProcessColorPalette.GetColor(process);

        foreach (var line in lines)
        {
            var text = this.formatterInternal.Format(line);
            this.WriteWithColor(text, color);
        }

        if (buffer.HasPending && this.active.TryAdd(process, 0))
            this.activeQueue.Enqueue(process);
    }

    private void WriteWithColor(string text, ConsoleColor color)
    {
        lock (this.sync)
        {
            var previous = Console.ForegroundColor;

            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
    }

    private sealed class ProcessBuffer
    {
        private readonly Lock sync = new();
        private readonly Queue<ProcessOutputLine> lines = new();

        public void Enqueue(ProcessOutputLine line)
        {
            lock (this.sync)
                this.lines.Enqueue(line);
        }

        public List<ProcessOutputLine> Drain()
        {
            lock (this.sync)
            {
                if (this.lines.Count == 0)
                    return new List<ProcessOutputLine>();

                var drained = new List<ProcessOutputLine>(this.lines.Count);

                while (this.lines.Count > 0)
                    drained.Add(this.lines.Dequeue());

                return drained;
            }
        }

        public bool HasPending
        {
            get
            {
                lock (this.sync)
                    return this.lines.Count > 0;
            }
        }
    }

    private static class ProcessColorPalette
    {
        private static readonly ConsoleColor[] colors =
        {
            ConsoleColor.Cyan,
            ConsoleColor.Green,
            ConsoleColor.Yellow,
            ConsoleColor.Magenta,
            ConsoleColor.Blue,
            ConsoleColor.DarkCyan,
            ConsoleColor.DarkGreen,
            ConsoleColor.DarkYellow,
            ConsoleColor.DarkMagenta,
            ConsoleColor.DarkBlue,
        };

        public static ConsoleColor GetColor(ProcessKey key)
        {
            var hash = HashCode.Combine(key.GroupName, key.ProcessName);
            var index = (hash & int.MaxValue) % colors.Length;

            return colors[index];
        }
    }
}
