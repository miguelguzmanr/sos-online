using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SosOnline.UI;

/// <summary>
/// Player names come from Steam and from other PCs, so they can be anything: very long, line breaks, invisible
/// characters, text markup ("&lt;size=200&gt;" is read by the game's text renderer), or scripts the game's pixel font
/// can't draw. Every name is cleaned once, where it enters the mod; everything that shows a name uses the clean one.
/// </summary>
internal static class PlayerNames
{
    /// <summary>Longest name kept, in characters as a player sees them (an emoji or an accented letter is one).</summary>
    public const int MaxLength = 24;

    /// <summary>
    /// A name safe to show: no control or formatting characters, no markup, spaces collapsed, at most
    /// <see cref="MaxLength"/> characters (cut with "..."). <paramref name="fallback"/> when nothing is left, or when
    /// the game's font can't draw most of it.
    /// </summary>
    public static string Clean(string name, string fallback)
    {
        if (string.IsNullOrEmpty(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        bool space = false;
        foreach (char c in name)
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = sb.Length > 0;
                continue;
            }
            if (category == UnicodeCategory.Format) continue; // zero-width and direction marks
            if (space) { sb.Append(' '); space = false; }
            // Markup: "<" and ">" would let a name restyle or hide text in toasts, popups and banners.
            sb.Append(c switch { '<' => '(', '>' => ')', _ => c });
        }
        string clean = Shorten(sb.ToString(), MaxLength);
        if (clean.Length == 0) return fallback;

        // Mostly undrawable (another script, emoji): the game's font would show a few stray letters, or nothing.
        // Before the game's fonts are loaded (a name read at start) judge by script instead, so every PC decides
        // the same way: the game's pixel fonts cover Latin letters only.
        string letters = clean.Replace(" ", "");
        int drawable = GameUi.Ready ? GameUi.Drawable(letters).Length : CountLatin(letters);
        if (drawable * 2 < letters.Length) return fallback;
        return clean;
    }

    /// <summary>Characters in the Latin, Latin-1 and Latin Extended blocks (letters, digits, punctuation).</summary>
    private static int CountLatin(string text)
    {
        int n = 0;
        foreach (char c in text) if (c <= 'ɏ') n++;
        return n;
    }

    /// <summary>Names as a sentence reads them: "Robin", "Robin and Sam", "Robin, Sam, and Kai".</summary>
    public static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[names.Count - 1]}",
    };

    /// <summary>The names and the verb that agrees with them: "Robin is", "Robin and Sam are".</summary>
    public static string With(IReadOnlyList<string> names, string one, string many) =>
        $"{List(names)} {(names.Count == 1 ? one : many)}";

    /// <summary>
    /// Names sent in a message, one per line (cleaned names have no line breaks), for the other PC to build its
    /// own sentence with <see cref="List"/> or <see cref="With"/>.
    /// </summary>
    public static string Pack(IEnumerable<string> names) => string.Join("\n", names);

    public static IReadOnlyList<string> Unpack(string packed) =>
        string.IsNullOrEmpty(packed) ? Array.Empty<string>() : packed.Split('\n');

    /// <summary>At most <paramref name="max"/> visible characters, never splitting one; "..." when cut.</summary>
    public static string Shorten(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        var info = new StringInfo(text);
        if (info.LengthInTextElements <= max) return text;
        return info.SubstringByTextElements(0, max - 1).TrimEnd() + "...";
    }
}
