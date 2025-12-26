// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Text;
using Itexoft.IO.FileSystem;
using Itexoft.IO.Streams.Chars;
using Itexoft.Threading;
using Itexoft.Threading.Tasks;
using Procvd.Runtime;

namespace Procvd.Output;

public sealed class ProcessFileOutputTailer(
    string path,
    ProcessKey process,
    string displayPath,
    IProcessOutputSink output,
    TimeSpan? pollInterval = null)
{
    private readonly TimeSpan pollInterval = pollInterval is null || pollInterval.Value <= TimeSpan.Zero
        ? TimeSpan.FromMilliseconds(100)
        : pollInterval.Value;

    public async Promise RunAsync(Promise<int> processExit, long startPosition, CancelToken cancelToken = default)
    {
        using var stream = IFileSystem.Sys.Open(path, SysFileMode.Read);

        stream.Position = startPosition;

        using var reader = new CharStreamBr(stream, Encoding.UTF8);

        while (true)
        {
            cancelToken.ThrowIf();

            var positionBeforeRead = stream.Position;
            var line = reader.ReadLine(cancelToken);
            var positionAfterRead = stream.Position;

            if (positionAfterRead != positionBeforeRead || line.Length != 0)
            {
                output.Write(new ProcessOutputLine(process, displayPath, ProcessOutputStream.StdOut, line, DateTimeOffset.UtcNow));

                continue;
            }

            if (processExit.IsCompleted && stream.Length <= positionAfterRead)
                break;

            await Promise.Delay(this.pollInterval, cancelToken);
        }
    }
}
