using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>Where the reader was in a document: rotation plus the fields of Core's
/// <see cref="RecentFileEntry"/> that restore a position.</summary>
public sealed class PackageViewState
{
    public int Rotation { get; set; }
    public int Page { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool HasSavedCamera { get; set; }

    /// <summary>The colour effect's name as <c>config.json</c> writes it (<c>"highContrast"</c>), or null.
    /// A string because Core's enum carries a converter the source generator can't see.</summary>
    [JsonPropertyName("colour_effect")]
    public string? ColourEffectName { get; set; }

    [JsonIgnore]
    public ColourEffect? ColourEffect
    {
        get => Enum.TryParse<ColourEffect>(ColourEffectName, ignoreCase: true, out var effect) && Enum.IsDefined(effect)
            ? effect : null;   // TryParse also takes a number, so an out-of-range "7" would come through
        set => ColourEffectName = value is { } effect ? JsonNamingPolicy.CamelCase.ConvertName(effect.ToString()) : null;
    }
}

public sealed class PackagePdf
{
    public string Entry { get; set; } = ReadingPackage.PdfEntry;
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    /// <summary>The document's content id on the machine that wrote the package. Used only to find a
    /// local copy on import; the importing app resolves its own id.</summary>
    public string ContentId { get; set; } = "";
    public bool Encrypted { get; set; }
}

public sealed class PackageManifest
{
    public string Format { get; set; } = ReadingPackage.FormatName;
    public int FormatVersion { get; set; } = ReadingPackage.FormatVersion;
    public string AppVersion { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public PackagePdf Pdf { get; set; } = new();
}

/// <summary>A package read by <see cref="ReadingPackage.Read"/>: the PDF extracted to a scratch
/// directory, and the state that travels with it.</summary>
public sealed record OpenedPackage(
    PackageManifest Manifest, string PdfPath, AnnotationFile Annotations, PortalSet Portals, PackageViewState View);

/// <summary>
/// A portable reading session (<c>.railreading</c>, Cauchy roadmap M6): a zip holding the PDF and the
/// state RailReader keeps for it outside the file — the sidecar annotations and bookmarks, portals, view
/// rotation and the reading position — so it can move to another machine or another reader.
/// <para>Entries have fixed names (<see cref="ManifestEntry"/> and friends); a reader takes only those and
/// never extracts by an entry's own name. The PDF is the file's current bytes, so on a writable PDF the
/// annotations travel inside it and <c>annotations.json</c> holds only what the sidecar adds (bookmarks,
/// and annotations a read-only or signed PDF couldn't take).</para>
/// <para>The manifest's content id is only a hint for finding a local copy: once annotations are written
/// into a PDF its bytes no longer hash to its id, and only the writing machine's identity map knows the
/// alias. An importer resolves the PDF under its own identity map and saves the state under that id.</para>
/// </summary>
public static class ReadingPackage
{
    public const string Extension = ".railreading";
    public const string FormatName = "railreading";
    public const int FormatVersion = 1;

    public const string ManifestEntry = "manifest.json";
    public const string PdfEntry = "document.pdf";
    public const string AnnotationsEntry = "annotations.json";
    public const string PortalsEntry = "portals.json";
    public const string ViewEntry = "view.json";

    internal const long MaxPdfBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaxJsonBytes = 64L * 1024 * 1024;

    /// <summary>True for a path with the package extension.</summary>
    public static bool IsPackagePath(string path)
        => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The package's default name: <c>&lt;name&gt;.railreading</c> beside the PDF.</summary>
    public static string DefaultOutputPath(string pdfPath)
        => Path.Combine(Path.GetDirectoryName(pdfPath) ?? "",
            Path.GetFileNameWithoutExtension(pdfPath) + Extension);

    /// <summary>
    /// Writes a package for <paramref name="pdfPath"/> to <paramref name="outPath"/> (via a temp file, then a
    /// rename). Reads the PDF and its stored state from disk, so the caller flushes pending annotation saves
    /// first. <paramref name="keyOf"/> maps the PDF to its content id: <see cref="DocumentIdentity.Peek"/> in
    /// the app, <see cref="DocumentIdentity.Lookup"/> in the CLI. Throws <see cref="PdfPasswordRequiredException"/>
    /// for an encrypted PDF without its password. Returns the manifest written.
    /// </summary>
    public static PackageManifest Write(string pdfPath, string outPath, string? password, bool encrypted,
        PackageViewState view, Func<string, string?> keyOf)
    {
        pdfPath = Path.GetFullPath(pdfPath);
        outPath = Path.GetFullPath(outPath);
        if (string.Equals(pdfPath, outPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The package can't replace the PDF it is made from.");

        var annotations = SidecarPart(CompositeAnnotationStore.Default.Load(pdfPath, password));
        var portals = PortalSet.Load(pdfPath, keyOf);
        return Write(pdfPath, outPath, encrypted, keyOf(pdfPath), annotations, portals, view);
    }

    /// <summary>Writes a package from state the caller supplies (the store-reading overload's second half;
    /// tests use it directly so they never touch the real config directory). A null
    /// <paramref name="contentId"/> becomes the PDF's hash, the id its first open would give it.</summary>
    internal static PackageManifest Write(string pdfPath, string outPath, bool encrypted, string? contentId,
        AnnotationFile annotations, PortalSet portals, PackageViewState view)
    {
        pdfPath = Path.GetFullPath(pdfPath);
        outPath = Path.GetFullPath(outPath);
        var manifest = new PackageManifest
        {
            AppVersion = AppVersion(),
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            Pdf = new PackagePdf { FileName = Path.GetFileName(pdfPath), Encrypted = encrypted },
        };

        string tmp = $"{outPath}.{Guid.NewGuid():N}.tmp";
        string scratch = Path.Combine(Path.GetTempPath(), $"railreader2-package-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(scratch);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                // The PDF first, hashed as it is copied: its hash and size go in the manifest.
                using (var src = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = zip.CreateEntry(PdfEntry, CompressionLevel.Optimal).Open())
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int n;
                    long size = 0;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                        dst.Write(buffer, 0, n);
                        size += n;
                    }
                    manifest.Pdf.Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                    manifest.Pdf.Size = size;
                }
                // A document never opened in the app has no id yet; the first open would give it this hash.
                manifest.Pdf.ContentId = contentId ?? manifest.Pdf.Sha256;

                // AnnotationService only exports to a path.
                string annotationsJson = Path.Combine(scratch, AnnotationsEntry);
                AnnotationService.ExportJson(annotations, annotationsJson);
                zip.CreateEntryFromFile(annotationsJson, AnnotationsEntry, CompressionLevel.Optimal);

                WriteJson(zip, PortalsEntry, portals, PortalJsonContext.Default.PortalSet);
                WriteJson(zip, ViewEntry, view, PackageJsonContext.Default.PackageViewState);
                WriteJson(zip, ManifestEntry, manifest, PackageJsonContext.Default.PackageManifest);
            }
            File.Move(tmp, outPath, overwrite: true);
            return manifest;
        }
        finally
        {
            TryDelete(tmp);
            TryDeleteDirectory(scratch);
        }
    }

    /// <summary>
    /// Reads and checks a package, extracting the PDF (as <see cref="PdfEntry"/>) and its JSON into
    /// <paramref name="scratchDir"/>, which the caller owns and deletes. Only the known entries are read, each
    /// under a size cap, and the PDF must match the manifest's hash. Throws <see cref="InvalidDataException"/>
    /// with a reader-facing message when the package is damaged, too new, or not a package.
    /// </summary>
    public static OpenedPackage Read(string packagePath, string scratchDir)
    {
        Directory.CreateDirectory(scratchDir);
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(packagePath); }
        catch (InvalidDataException) { throw new InvalidDataException("This file isn't a reading package (it isn't a zip file)."); }

        using (zip)
        {
            var manifest = ReadJson(zip, ManifestEntry, PackageJsonContext.Default.PackageManifest)
                ?? throw new InvalidDataException("This file isn't a reading package (it has no manifest).");
            if (manifest.Format != FormatName)
                throw new InvalidDataException("This file isn't a reading package.");
            if (manifest.FormatVersion > FormatVersion)
                throw new InvalidDataException(
                    $"This reading package was made by a newer RailReader2 ({manifest.AppVersion}). Update RailReader2 to open it.");
            if (manifest.FormatVersion < 1 || manifest.Pdf is null || manifest.Pdf.Entry != PdfEntry)
                throw new InvalidDataException("This reading package is damaged (its manifest is invalid).");

            string pdfPath = Path.Combine(scratchDir, PdfEntry);
            var pdfEntry = zip.GetEntry(PdfEntry)
                ?? throw new InvalidDataException("This reading package is damaged (the PDF is missing).");
            string sha;
            long size;
            using (var src = pdfEntry.Open())
            using (var dst = File.Create(pdfPath))
                (sha, size) = CopyCapped(src, dst, MaxPdfBytes, hash: true);
            if (size != manifest.Pdf.Size || !string.Equals(sha, manifest.Pdf.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This reading package is damaged (the PDF doesn't match its manifest).");

            var annotations = new AnnotationFile();
            if (zip.GetEntry(AnnotationsEntry) is { } annotationsEntry)
            {
                // AnnotationService only imports from a path.
                string annotationsJson = Path.Combine(scratchDir, AnnotationsEntry);
                using (var src = annotationsEntry.Open())
                using (var dst = File.Create(annotationsJson))
                    CopyCapped(src, dst, MaxJsonBytes, hash: false);
                annotations = AnnotationService.ImportJson(annotationsJson)
                    ?? throw new InvalidDataException("This reading package is damaged (its annotations can't be read).");
            }

            var portals = ReadJson(zip, PortalsEntry, PortalJsonContext.Default.PortalSet) ?? new PortalSet();
            var view = ReadJson(zip, ViewEntry, PackageJsonContext.Default.PackageViewState) ?? new PackageViewState();
            return new OpenedPackage(manifest, pdfPath, annotations, portals, view);
        }
    }

    /// <summary>
    /// Checks a package and extracts it as plain files into <paramref name="outDir"/>: the PDF under its
    /// original file name, and the JSON entries as stored. Installs nothing into the config directory (that
    /// needs <see cref="DocumentIdentity.Resolve"/>, which only the app calls). Refuses to overwrite a file.
    /// Returns the manifest and the extracted PDF's path.
    /// </summary>
    public static (PackageManifest Manifest, string PdfPath) Unpack(string packagePath, string outDir)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"railreader2-unpack-{Guid.NewGuid():N}");
        try
        {
            var opened = Read(packagePath, scratch);
            // The manifest's file name is the writer's word: keep only its last segment.
            string name = Path.GetFileName(opened.Manifest.Pdf.FileName);
            string[] jsonEntries = [ManifestEntry, AnnotationsEntry, PortalsEntry, ViewEntry];
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
                || jsonEntries.Contains(name, StringComparer.OrdinalIgnoreCase))   // would collide with an entry below
                name = PdfEntry;

            Directory.CreateDirectory(outDir);
            string pdfOut = Path.Combine(outDir, name);
            foreach (var target in jsonEntries.Select(e => Path.Combine(outDir, e)).Append(pdfOut))
                if (File.Exists(target))
                    throw new IOException($"{target} already exists.");

            File.Move(opened.PdfPath, pdfOut);
            using var zip = ZipFile.OpenRead(packagePath);
            foreach (var entryName in jsonEntries)
            {
                if (zip.GetEntry(entryName) is not { } entry) continue;
                using var src = entry.Open();
                using var dst = new FileStream(Path.Combine(outDir, entryName), FileMode.CreateNew);
                CopyCapped(src, dst, MaxJsonBytes, hash: false);
            }
            return (opened.Manifest, pdfOut);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    /// <summary>What the sidecar adds to the PDF: bookmarks, and annotations not stored in the PDF itself
    /// (the same split as Core's <c>CompositeAnnotationStore</c> uses when it falls back to the sidecar).</summary>
    internal static AnnotationFile SidecarPart(AnnotationFile? loaded)
    {
        var part = new AnnotationFile();
        if (loaded is null) return part;
        foreach (var (page, list) in loaded.Pages)
        {
            var authored = list.Where(a => a.Source != AnnotationSource.InPdf).ToList();
            if (authored.Count > 0) part.Pages[page] = authored;
        }
        part.Bookmarks = [.. loaded.Bookmarks];
        return part;
    }

    private static string AppVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = version.IndexOf('+');   // drop the SourceLink commit suffix
        return plus >= 0 ? version[..plus] : version;
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(stream, value, info);
    }

    private static T? ReadJson<T>(ZipArchive zip, string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        using var buffer = new MemoryStream();
        using (var src = entry.Open())
            CopyCapped(src, buffer, MaxJsonBytes, hash: false);
        buffer.Position = 0;
        try { return JsonSerializer.Deserialize(buffer, info); }
        catch (JsonException) { throw new InvalidDataException($"This reading package is damaged ({name} can't be read)."); }
    }

    /// <summary>Copies at most <paramref name="max"/> bytes, counting what is actually decompressed rather
    /// than trusting the entry's declared length. Returns the SHA-256 (when asked) and the byte count.</summary>
    internal static (string Sha256, long Size) CopyCapped(Stream src, Stream dst, long max, bool hash)
    {
        using var sha = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > max)
                throw new InvalidDataException("This reading package is damaged (an entry is larger than allowed).");
            sha?.AppendData(buffer, 0, n);
            dst.Write(buffer, 0, n);
        }
        return (sha is null ? "" : Convert.ToHexStringLower(sha.GetHashAndReset()), total);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(PackageViewState))]
internal partial class PackageJsonContext : JsonSerializerContext;
