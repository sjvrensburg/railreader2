using System.Collections;
using System.Reflection;

namespace RailReader.Cli.Output;

public sealed class HumanFormatter : IOutputFormatter
{
    public void WriteResult(object result)
    {
        switch (result)
        {
            case string s:
                Console.WriteLine(s);
                break;

            case IEnumerable<object[]> table:
                WriteTable(table);
                break;

            default:
                WriteRecord(result);
                break;
        }
    }

    public void WriteError(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
    }

    public void WriteMessage(string message)
    {
        Console.WriteLine(message);
    }

    private static void WriteRecord(object obj)
    {
        var props = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        int maxLabel = props.Max(p => FormatLabel(p.Name).Length);

        foreach (var prop in props)
        {
            var value = prop.GetValue(obj);
            if (value is IDictionary dict)
            {
                Console.WriteLine($"  {FormatLabel(prop.Name).PadRight(maxLabel)}  :");
                foreach (DictionaryEntry entry in dict)
                    Console.WriteLine($"    {entry.Key}: {entry.Value}");
            }
            else
            {
                Console.WriteLine($"  {FormatLabel(prop.Name).PadRight(maxLabel)}  : {value}");
            }
        }
    }

    public static void WriteTable(IEnumerable<object[]> rows, params string[] headers)
    {
        var allRows = rows.ToList();
        if (allRows.Count == 0 && headers.Length == 0) return;

        int cols = headers.Length > 0 ? headers.Length : (allRows.FirstOrDefault()?.Length ?? 0);
        var widths = new int[cols];

        for (int c = 0; c < cols; c++)
            widths[c] = c < headers.Length ? headers[c].Length : 0;

        foreach (var row in allRows)
            for (int c = 0; c < Math.Min(cols, row.Length); c++)
                widths[c] = Math.Max(widths[c], (row[c]?.ToString() ?? "").Length);

        if (headers.Length > 0)
        {
            Console.Write("  ");
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) Console.Write(" | ");
                Console.Write(headers[c].PadRight(widths[c]));
            }
            Console.WriteLine();
            Console.Write("  ");
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) Console.Write("-+-");
                Console.Write(new string('-', widths[c]));
            }
            Console.WriteLine();
        }

        foreach (var row in allRows)
        {
            Console.Write("  ");
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) Console.Write(" | ");
                var val = c < row.Length ? (row[c]?.ToString() ?? "") : "";
                Console.Write(val.PadRight(widths[c]));
            }
            Console.WriteLine();
        }
    }

    private static string FormatLabel(string name) =>
        PascalCaseHelper.SplitPascalCase(name, ' ');
}
