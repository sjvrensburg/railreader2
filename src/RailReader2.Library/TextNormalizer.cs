using System.Text;

namespace RailReader2.Library;

/// <summary>Index-time text normalisation (design §4.1): NFKC, line-end de-hyphenation, whitespace
/// collapse, broken-ligature repair. Applied to what is indexed, never to the PDF.
/// <para>A TeX font without a Unicode map gives PDFium its raw codes, so a ligature comes out as a control
/// character ("uni\u001Ced"). The codes of TeX's T1 (Cork) encoding (and OT1's ffi/ffl) are mapped back when they
/// touch a letter (<see cref="Ligature"/>); any other control character is a word break.</para></summary>
public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // PDFium reports a hyphen it found at the end of a line as U+0002, usually with no line
            // break after it: "Trans\u0002formers". Treat it as a hyphen followed by a line break.
            if (c == '\u0002')
            {
                if (pendingSpace || sb.Length == 0) { pendingSpace = true; continue; }
                sb.Append('-');
                c = '\n';
            }
            if (c is '\r' or '\n')
            {
                // "estima-\ntion" -> "estimation": a hyphen ending a line, lower-case letter after it.
                if (sb.Length > 1 && sb[^1] is '-' or '­' && char.IsLetter(sb[^2]))
                {
                    int j = i + 1;
                    while (j < s.Length && s[j] is '\r' or '\n' or ' ' or '\t') j++;
                    // A broken ligature code can open the next line's half: "coef-\n\u001Ccient".
                    if (j < s.Length && (char.IsLower(s[j]) || Ligature(s, j) is not null))
                    {
                        sb.Length--;
                        i = j - 1;
                        pendingSpace = false;
                        continue;
                    }
                }
                pendingSpace = true;
                continue;
            }
            // A soft hyphen inside a line is an invisible hyphenation hint: dropped, or the tokenizer would
            // split the word on it ("esti­mation" → "esti", "mation"). One ending a line is kept for
            // the line-break branch above to join the word.
            if (c == '­')
            {
                int k = i + 1;
                while (k < s.Length && s[k] is ' ' or '\t') k++;
                if (k < s.Length && s[k] is not ('\r' or '\n' or '\u0002')) continue;
            }
            if (Ligature(s, i) is { } lig)
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(lig);
                continue;
            }
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The ligature a TeX font's raw code at <paramref name="i"/> stands for, or null. T1 (Cork):
    /// 0x1B ff, 0x1C fi, 0x1D fl, 0x1E ffi, 0x1F ffl. OT1: 0x0E ffi, 0x0F ffl; its ff, fi and fl
    /// (0x0B, 0x0C, 0x0D) are vertical tab, form feed and carriage return, so they stay word breaks. Only next to a letter: "de\u001Cne", "\u001Dat",
    /// "o\u001B". Seen in pdfTeX output with Type 3 or unmapped Type 1 fonts.</summary>
    static string? Ligature(string s, int i)
    {
        string? lig = s[i] switch
        {
            '\u001B' => "ff",
            '\u001C' => "fi",
            '\u001D' => "fl",
            '\u001E' or '\u000E' => "ffi",
            '\u001F' or '\u000F' => "ffl",
            _ => null,
        };
        if (lig is null) return null;
        bool before = i > 0 && char.IsLetter(s[i - 1]);
        bool after = i + 1 < s.Length && char.IsLetter(s[i + 1]);
        return before || after ? lig : null;
    }
}
