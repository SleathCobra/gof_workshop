using System.Globalization;

namespace Gof2Workshop.Testbed;

internal sealed class CliLogger
{
    private readonly TextWriter writer;

    public CliLogger(TextWriter? writer = null)
    {
        this.writer = writer ?? Console.Error;
    }

    public void Info(string eventName, string message, params (string Key, object? Value)[] properties)
    {
        Write("INF", eventName, message, properties);
    }

    public void Warning(string eventName, string message, params (string Key, object? Value)[] properties)
    {
        Write("WRN", eventName, message, properties);
    }

    public void Error(string eventName, string message, params (string Key, object? Value)[] properties)
    {
        Write("ERR", eventName, message, properties);
    }

    private void Write(
        string level,
        string eventName,
        string message,
        (string Key, object? Value)[] properties)
    {
        string suffix = properties.Length == 0
            ? string.Empty
            : " " + string.Join(
                " ",
                properties.Select(property =>
                    $"{property.Key}={FormatValue(property.Value)}"));
        writer.WriteLine($"[{level}] {eventName}: {message}{suffix}");
    }

    private static string FormatValue(object? value)
    {
        string text = value switch
        {
            null => "null",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        return text.Any(char.IsWhiteSpace) ? $"\"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : text;
    }
}
