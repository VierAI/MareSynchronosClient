using System.Runtime.CompilerServices;
using System.Text;

namespace MareSynchronos.Utils;

[InterpolatedStringHandler]
public readonly ref struct MareInterpolatedStringHandler
{
    // PSync: skip building counter names when performance logging is off (set by PerformanceCollectorService).
    internal static volatile bool Enabled;
    readonly StringBuilder? _logMessageStringbuilder;

    public MareInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _logMessageStringbuilder = Enabled ? new StringBuilder(literalLength) : null;
    }

    public void AppendLiteral(string s)
    {
        _logMessageStringbuilder?.Append(s);
    }

    public void AppendFormatted<T>(T t)
    {
        _logMessageStringbuilder?.Append(t?.ToString());
    }

    public string BuildMessage() => _logMessageStringbuilder?.ToString() ?? string.Empty;
}
