using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Persistence boundary for the most-recently-used file list and per-file
/// reading position. The Core controller calls this to remember where the
/// user was; the platform-specific implementation decides how to persist
/// (filesystem JSON on desktop, IndexedDB on web, etc.).
/// </summary>
public interface IRecentFilesStore
{
    RecentFileEntry? GetReadingPosition(string filePath);
    void AddRecentFile(string filePath);
    void SaveReadingPosition(string filePath, int page, double zoom,
        double offsetX, double offsetY, ColourEffect? colourEffect = null);
}
