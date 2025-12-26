// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Runtime;

namespace Procvd.Output;

public sealed class ProcessConsoleOutputSink(ProcessOutputFormatter? formatter = null) : IProcessOutputSink
{
    private readonly Lock sync = new();
    private readonly ProcessOutputFormatter formatterInternal = formatter ?? new();

    public void Write(ProcessOutputLine line)
    {
        var text = this.formatterInternal.Format(line);
        var color = ProcessColorPalette.GetColor(line.Process);

        this.WriteWithColor(text, color);
    }

    public void WriteEvent(ProcessOutputEvent message)
    {
        var text = this.formatterInternal.Format(message);

        this.WriteWithColor(text, ConsoleColor.DarkGray);
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
