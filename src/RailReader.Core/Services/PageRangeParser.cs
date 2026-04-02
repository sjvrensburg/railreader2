namespace RailReader.Core.Services;

/// <summary>
/// Parses page range strings like "1,3,5-10" into 0-based page indices.
/// Input is 1-based (user-facing); output is 0-based.
/// </summary>
public static class PageRangeParser
{
    public static (List<int>? Pages, string? Error) Parse(string? range, int totalPages)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            var all = new List<int>(totalPages);
            for (int i = 0; i < totalPages; i++) all.Add(i);
            return (all, null);
        }

        var pages = new SortedSet<int>();
        foreach (var segment in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Contains('-'))
            {
                var parts = segment.Split('-', 2);
                if (!int.TryParse(parts[0], out int start) || !int.TryParse(parts[1], out int end))
                    return (null, $"Invalid range: '{segment}'");

                if (start > end)
                    return (null, $"Invalid range: '{segment}' (start > end)");

                if (start < 1 || end < 1 || start > totalPages || end > totalPages)
                    return (null, $"Page out of range: '{segment}' (document has {totalPages} pages)");

                for (int i = start; i <= end; i++)
                    pages.Add(i - 1);
            }
            else
            {
                if (!int.TryParse(segment, out int page))
                    return (null, $"Invalid page number: '{segment}'");

                if (page < 1 || page > totalPages)
                    return (null, $"Page {page} out of range (document has {totalPages} pages)");

                pages.Add(page - 1);
            }
        }

        return (pages.ToList(), null);
    }
}
