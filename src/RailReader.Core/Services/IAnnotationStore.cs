using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Persistence boundary for annotation files. Implementations decide where
/// and how to persist (filesystem JSON on desktop, IndexedDB on web, etc.).
/// All methods are called from the UI thread.
/// </summary>
public interface IAnnotationStore
{
    /// <summary>Load annotations for a PDF, or null if none exist.</summary>
    AnnotationFile? Load(string pdfPath);

    /// <summary>Persist annotations for a PDF. Returns true on success.</summary>
    bool Save(string pdfPath, AnnotationFile annotations);

    /// <summary>Remove any persisted annotations for a PDF. Returns true on success.</summary>
    bool Delete(string pdfPath);
}
