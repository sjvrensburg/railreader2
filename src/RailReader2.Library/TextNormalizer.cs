using System.Text;

namespace RailReader2.Library;

/// <summary>Index-time text normalisation (design §4.1): NFKC, line-end de-hyphenation, whitespace
/// collapse. Applied to what is indexed, never to the PDF.</summary>
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
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
