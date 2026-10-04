using System.Text.RegularExpressions;

namespace BmwMs43Logger.Core;

public sealed record ParameterId
{
    private static readonly Regex ValidPattern = new(
        "^[a-z][a-z0-9]*(\\.[a-z0-9]+)+$",
        RegexOptions.CultureInvariant);

    public ParameterId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!ValidPattern.IsMatch(value))
        {
            throw new ArgumentException(
                "Parameter IDs must be lower-case dotted identifiers.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
