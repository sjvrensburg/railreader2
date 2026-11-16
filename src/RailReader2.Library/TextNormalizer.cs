using System.Text;

namespace RailReader2.Library;

/// <summary>Index-time text normalisation (design §4.1): NFKC, line-end de-hyphenation, whitespace
/// collapse. Applied to what is indexed, never to the PDF.
/// <para>Known gap: a glyph with no Unicode mapping comes out of PDFium as a control character (a broken
/// "fi" ligature: "uni\u001Ced"); it is treated as a space, so that word won't match.</para></summary>
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
                    if (j < s.Length && char.IsLower(s[j]))
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
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
