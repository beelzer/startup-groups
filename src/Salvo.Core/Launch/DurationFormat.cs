using System.Globalization;

namespace Salvo.Core.Launch;

/// <summary>
/// One place for human-readable launch-duration formatting, previously
/// copy-pasted across five view-models — with an F1-vs-F2 drift on the seconds
/// branch. Sub-second durations render as whole milliseconds ("450ms"); longer
/// ones as seconds with one decimal ("1.5s").
/// </summary>
public static class DurationFormat
{
    public static string Human(TimeSpan duration) =>
        duration.TotalMilliseconds < 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMilliseconds:F0}ms")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:F1}s");
}
