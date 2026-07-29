namespace Gof2Workshop.Testbed;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> options;

    private CliArguments(
        string command,
        IReadOnlyList<string> positionals,
        Dictionary<string, string?> options)
    {
        Command = command;
        Positionals = positionals;
        this.options = options;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positionals { get; }

    public static CliArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return new CliArguments("help", [], new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        }

        string command = args[0];
        List<string> positionals = [];
        Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < args.Length; index++)
        {
            string value = args[index];
            if (!value.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(value);
                continue;
            }

            int equals = value.IndexOf('=');
            if (equals > 2)
            {
                options[value[2..equals]] = value[(equals + 1)..];
            }
            else if (index + 1 < args.Length
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[value[2..]] = args[++index];
            }
            else
            {
                options[value[2..]] = null;
            }
        }

        return new CliArguments(command, positionals, options);
    }

    public bool HasFlag(string name) => options.ContainsKey(name);

    public string? GetOption(string name)
    {
        return options.TryGetValue(name, out string? value) ? value : null;
    }

    public string GetOption(string name, string defaultValue)
    {
        return GetOption(name) ?? defaultValue;
    }

    public int? GetIntOption(string name)
    {
        string? value = GetOption(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int result)
            ? result
            : throw new ArgumentException($"Option --{name} requires an integer, got '{value}'.");
    }

    public float? GetFloatOption(string name)
    {
        string? value = GetOption(name);
        if (value is null)
        {
            return null;
        }

        return float.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float result)
            ? result
            : throw new ArgumentException(
                $"Option --{name} requires a number, got '{value}'.");
    }

    public string RequirePositional(int index, string description)
    {
        return index < Positionals.Count
            ? Positionals[index]
            : throw new ArgumentException($"Missing required argument: {description}.");
    }
}
