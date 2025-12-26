// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Text;
using Itexoft.Threading;
using Procvd.Runtime;

namespace Procvd.Output;

public sealed class ProcessFileOutputTailer
{
    private readonly string path;
    private readonly ProcessKey process;
    private readonly string displayPath;
    private readonly IProcessOutputSink output;
    private readonly TimeSpan pollInterval;

    public ProcessFileOutputTailer(
        string path,
        ProcessKey process,
        string displayPath,
        IProcessOutputSink output,
        TimeSpan? pollInterval = null)
    {
        this.path = path;
        this.process = process;
        this.displayPath = displayPath;
        this.output = output;
        this.pollInterval = pollInterval is null || pollInterval.Value <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(100)
            : pollInterval.Value;
    }

    public async Task RunAsync(Task processExit, long startPosition, CancelToken token = default)
    {
        using var bridge = token.Bridge(out var cancellationToken);
        await using var stream = new FileStream(
            this.path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        if (startPosition > 0)
            stream.Seek(startPosition, SeekOrigin.Begin);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        while (true)
        {
            token.ThrowIf();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is not null)
            {
                this.output.Write(new ProcessOutputLine(
                    this.process,
                    this.displayPath,
                    ProcessOutputStream.StdOut,
                    line,
                    DateTimeOffset.UtcNow));
                continue;
            }

            if (processExit.IsCompleted && stream.Length <= stream.Position)
                break;

            await Task.Delay(this.pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
