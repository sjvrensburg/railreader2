namespace RailReader.Cli.Output;

internal static class PascalCaseHelper
{
    /// <summary>
    /// Splits PascalCase into words separated by the given character.
    /// "PageCount" with '_' → "Page_Count", with ' ' → "Page Count".
    /// </summary>
    internal static string SplitPascalCase(string name, char separator)
    {
        var chars = new List<char>();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                chars.Add(separator);
            chars.Add(name[i]);
        }
        return new string(chars.ToArray());
    }
}
