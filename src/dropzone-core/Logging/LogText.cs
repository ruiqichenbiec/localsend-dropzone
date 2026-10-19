using System.Globalization;
using System.Text;

namespace Dropzone.Core.Logging;

/// <summary>Makes text from peers (device names, sender aliases, file names) safe to put on one log line.</summary>
public static class LogText
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>
    /// Escapes CR, LF, tab, every other control character and the Unicode line and paragraph separators
    /// (<c>\r</c>, <c>\n</c>, <c>\t</c>, otherwise <c>\uXXXX</c>), so a crafted name cannot start a forged log line.
    /// Null gives an empty string.
    /// </summary>
    public static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string? escaped = c switch
            {
                '\r' => @"\r",
                '\n' => @"\n",
                '\t' => @"\t",
                _ when char.IsControl(c) || c is LineSeparator or ParagraphSeparator =>
                    @"\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (escaped is null)
            {
                builder?.Append(c);
                continue;
            }

            builder ??= new StringBuilder(text, 0, i, text.Length + 16);
            builder.Append(escaped);
        }

        return builder?.ToString() ?? text;
    }
}
