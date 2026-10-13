namespace Pulsar.Domain.Common;

/// <summary>Limits on text from outside (an agent's message, a game's output) before it is stored.</summary>
public static class TextLimits
{
    /// <summary>
    /// The first <paramref name="maxLength"/> UTF-16 units of <paramref name="value"/>, backing off one when the cut would
    /// split a surrogate pair (half an emoji is not valid text: it could not be stored in PostgreSQL's UTF-8).
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength)];
    }
}
