namespace RailReader.Core.Services;

/// <summary>
/// Factory for creating platform-specific PDF service implementations.
/// Injected into DocumentController to decouple Core from a specific PDF library.
/// </summary>
public interface IPdfServiceFactory
{
    IPdfService CreatePdfService(string filePath);
    IPdfTextService CreatePdfTextService();
}
