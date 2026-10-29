using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.Services;

namespace RailReader.Cli.Commands;

/// <summary>
/// <c>package</c>: bundle a PDF with the state RailReader2 keeps for it (annotations, bookmarks, portals,
/// view rotation, reading position) into a portable <c>.railreading</c> file. Reads the app's stores
/// read-only: ids come from <see cref="DocumentIdentity.Lookup"/>, which never writes the identity map.
/// </summary>
public static class PackageCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (Program.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var pdfPath = Program.GetRequiredPdf(args);
        var outputPath = Path.GetFullPath(Program.GetOption(args, "output") ?? ReadingPackage.DefaultOutputPath(pdfPath));
        var password = Program.GetOption(args, "password");
        if (string.Equals(outputPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            return Program.Fail("--output must differ from the source PDF.");

        // Encrypted when it won't open without a password.
        bool encrypted;
        try
        {
            using (factory.CreatePdfService(pdfPath, null) as IDisposable) { }
            encrypted = false;
        }
        catch (PdfPasswordRequiredException)
        {
            encrypted = true;
        }

        if (encrypted)
        {
            if (!Program.HasFlag(args, "allow-encrypted"))
                return Program.Fail("This PDF is password-protected. The package keeps it encrypted and doesn't " +
                    "include the password, but your notes are stored in it as plain text. Pass --allow-encrypted " +
                    "(and --password <pwd>) to package it anyway.");
            try
            {
                using (factory.CreatePdfService(pdfPath, password) as IDisposable) { }
            }
            catch (PdfPasswordRequiredException ex)
            {
                return Program.Fail(ex.WrongPassword
                    ? "Incorrect password. Pass the correct --password for this encrypted PDF."
                    : "This PDF is password-protected. Pass --password <pwd> to read its annotations.");
            }
        }

        var view = new PackageViewState { Rotation = ViewRotationStore.Load(pdfPath, DocumentIdentity.Lookup) };
        if (AppConfig.Load().GetReadingPosition(pdfPath) is { } position)
        {
            view.Page = position.Page;
            view.Zoom = position.Zoom;
            view.OffsetX = position.OffsetX;
            view.OffsetY = position.OffsetY;
            view.HasSavedCamera = position.HasSavedCamera;
            view.ColourEffect = position.ColourEffect;
        }

        var manifest = ReadingPackage.Write(pdfPath, outputPath, encrypted ? password : null, encrypted,
            view, DocumentIdentity.Lookup);
        Console.Error.WriteLine($"Wrote {outputPath} ({manifest.Pdf.FileName}, {manifest.Pdf.Size / 1024.0:F0} KiB PDF).");
        return 0;
    }

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli package — Bundle a PDF and its reading session into a .railreading file");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli package <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("The package holds the PDF, its annotations and bookmarks, portals, view rotation and");
        Console.WriteLine("last reading position. Open it in RailReader2 on another computer to carry on reading.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output <path>      Package to write (default: <name>.railreading beside the PDF)");
        Console.WriteLine("  --allow-encrypted    Package a password-protected PDF (it stays encrypted; the");
        Console.WriteLine("                       password is not included, but notes are stored as plain text)");
        Console.WriteLine("  --password <pwd>     Password for an encrypted PDF, to read its annotations");
    }
}
