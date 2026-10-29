using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.Services;

namespace RailReader.Cli.Commands;

/// <summary>
/// <c>unpack</c>: check a <c>.railreading</c> package and extract its PDF and JSON as plain files. Installs
/// nothing into RailReader2's config directory; opening the package in the app does that.
/// </summary>
public static class UnpackCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (Program.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var packagePath = Program.GetRequiredPdf(args, "Package");
        var outDir = Path.GetFullPath(Program.GetOption(args, "output-dir")
            ?? Path.Combine(Path.GetDirectoryName(packagePath) ?? "", Path.GetFileNameWithoutExtension(packagePath)));

        try
        {
            var (manifest, pdfPath) = ReadingPackage.Unpack(packagePath, outDir);
            Console.Error.WriteLine($"Package from RailReader2 {manifest.AppVersion}, created {manifest.CreatedUtc}.");
            Console.Error.WriteLine($"Wrote {pdfPath}{(manifest.Pdf.Encrypted ? " (password-protected)" : "")}");
            Console.Error.WriteLine($"and its annotations, portals and view state to {outDir}.");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            return Program.Fail(ex.Message);
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli unpack — Extract a .railreading package");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli unpack <package> [options]");
        Console.WriteLine();
        Console.WriteLine("Checks the package and writes its PDF (under its original name) plus manifest.json,");
        Console.WriteLine("annotations.json, portals.json and view.json. Nothing is added to RailReader2's own");
        Console.WriteLine("storage; open the package in RailReader2 to restore the session there.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output-dir <dir>   Where to extract (default: a folder named after the package)");
    }
}
