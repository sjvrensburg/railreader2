using System.Reflection;
using System.Runtime.InteropServices;

namespace RailReader.Core.Services;

/// <summary>
/// Registers a DllImport resolver for the "pdfium" native library.
/// On Linux/macOS the NuGet package ships as libpdfium.so/.dylib in
/// runtimes/{rid}/native/. .NET's default probing normally finds it
/// (tries lib prefix + platform extension), but this resolver provides
/// an explicit fallback to guarantee resolution on all platforms.
///
/// Call <see cref="Initialize"/> once at startup, before any P/Invoke
/// into PDFium (PdfOutlineExtractor, PdfTextService).
/// PDFtoImage must be referenced so the native asset is deployed.
/// </summary>
public static class PdfiumResolver
{
    private static bool s_initialized;

    public static void Initialize()
    {
        if (s_initialized) return;
        s_initialized = true;

        NativeLibrary.SetDllImportResolver(
            typeof(PdfiumResolver).Assembly,
            ResolvePdfium);
    }

    private static IntPtr ResolvePdfium(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "pdfium")
            return IntPtr.Zero; // fall back to default resolution

        // Try default probing first (handles most cases)
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        // Explicit platform-specific fallback
        string ext, prefix;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ext = ".dll";
            prefix = "";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ext = ".dylib";
            prefix = "lib";
        }
        else
        {
            ext = ".so";
            prefix = "lib";
        }

        string libFile = $"{prefix}pdfium{ext}";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", RuntimeInformation.RuntimeIdentifier, "native", libFile),
            Path.Combine(AppContext.BaseDirectory, libFile),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
