namespace RailReader.Core.Services;

/// <summary>
/// Process-wide serialization gate for all PDFium access.
///
/// PDFium holds global state (font cache, image decoder state, etc.) that is not
/// safe against concurrent use from multiple threads — even for separate documents.
/// Concrete symptom: opening multiple tabs of the same PDF triggers background
/// render tasks that race inside PDFium and crash the process below the CLR
/// (no managed exception, no signal, no crash dump from the runtime).
///
/// Every call into PDFium — render, text extract, link enumerate, outline,
/// annotation export — must happen inside <c>lock (PdfiumGate.Lock)</c>.
/// The lock is reentrant so nesting is safe (e.g. SkiaPdfService ctor calls
/// PdfOutlineExtractor.Extract).
/// </summary>
internal static class PdfiumGate
{
    public static readonly object Lock = new();
}
