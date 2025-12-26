// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Text;

namespace Procvd.Output;

public sealed class ProcessOutputFormatter(ProcessOutputFormatterOptions? options = null)
{
    private readonly ProcessOutputFormatterOptions optionsInternal = options ?? new();

    public string Format(ProcessOutputLine line)
    {
        var builder = new StringBuilder();

        AppendPrefix(builder, line.Timestamp, line.Process, line.DisplayPath);
        builder.Append('[');
        builder.Append(line.Stream == ProcessOutputStream.StdOut ? "out" : "err");
        builder.Append("] ");
        builder.Append(line.Line);

        return builder.ToString();
    }

    public string Format(ProcessOutputEvent message)
    {
        var builder = new StringBuilder();

        AppendPrefix(builder, message.Timestamp, message.Process, message.DisplayPath);
        builder.Append("[event:");
        builder.Append(message.Kind.ToString().ToLowerInvariant());
        builder.Append(']');

        if (message.ExitCode.HasValue)
        {
            builder.Append(" [code:");
            builder.Append(message.ExitCode.Value);
            builder.Append(']');
        }

        if (!string.IsNullOrWhiteSpace(message.Message))
        {
            builder.Append(' ');
            builder.Append(message.Message);
        }

        return builder.ToString();
    }

    private void AppendPrefix(StringBuilder builder, DateTimeOffset timestamp, Runtime.ProcessKey process, string displayPath)
    {
        builder.Append('[');
        builder.Append(this.FormatTimestamp(timestamp));
        builder.Append("] [group:");
        builder.Append(process.GroupName);
        builder.Append("] [proc:");
        builder.Append(process.ProcessName);
        builder.Append("] [path:");
        builder.Append(displayPath);
        builder.Append("] ");
    }

    private string FormatTimestamp(DateTimeOffset timestamp) =>
        (this.optionsInternal.UseUtc ? timestamp.ToUniversalTime() : timestamp).ToString(this.optionsInternal.TimestampFormat);
}

public sealed class ProcessOutputFormatterOptions
{
    public string TimestampFormat { get; init; } = "O";

    public bool UseUtc { get; init; } = true;
}
