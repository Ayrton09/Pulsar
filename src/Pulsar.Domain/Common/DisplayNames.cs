using System.Globalization;
using System.Text;

namespace Pulsar.Domain.Common;

/// <summary>
/// Names users give what others see listed (servers, backups, schedules, API keys), shown as they are: nothing that would
/// hide in one or reorder it (zero-width spaces, direction overrides and isolates, tag characters) or break its line, so
/// that one name cannot be made to read like another. The zero-width non-joiner and joiner (Persian words, emoji
/// sequences) and the left-to-right and right-to-left marks (Hebrew and Arabic names) are allowed. Read by code point:
/// tag characters are format characters past the BMP. Node and role names follow the stricter rule of their own.
/// </summary>
public static class DisplayNames
{
    public static bool IsDisplayable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EnumerateRunes().All(IsAllowed);
    }

    private static bool IsAllowed(Rune rune) =>
        rune.Value is 0x200C or 0x200D or 0x200E or 0x200F
        || !(Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
}
