using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.Services;
using RailReader2.Views;

namespace RailReader2.ViewModels;

// Reading packages (.railreading, Cauchy roadmap M6): save the focused document with its annotations,
// bookmarks, portals, rotation and position; open one into a local copy or a new file.
public sealed partial class MainWindowViewModel
{
    private readonly HashSet<TabViewModel> _packagingTabs = [];

    private const string EncryptedPackageWarning =
        "This PDF is password-protected. The package keeps it encrypted and doesn't include the password, " +
        "but your notes are stored in it as plain text.";

    /// <summary>Opens a PDF, or a reading package by its extension. Every user-facing open goes through here.</summary>
    public Task OpenPath(string path)
        => ReadingPackage.IsPackagePath(path) ? ImportPackage(path) : OpenDocument(path);

    [RelayCommand]
    public async Task SaveReadingPackage()
    {
        if (_window is null || ActiveTab is not { } tab || DialogOwner is not { } owner) return;
        if (_packagingTabs.Contains(tab)) return;

        bool encrypted = IsActiveDocumentEncrypted;
        if (encrypted && !await new ConfirmDialog("Save Reading Package", EncryptedPackageWarning, "Save Anyway")
                .ShowDialog<bool>(owner))
            return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Reading Package",
            DefaultExtension = "railreading",
            FileTypeChoices = [new FilePickerFileType("Reading Packages") { Patterns = ["*" + ReadingPackage.Extension] }],
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + ReadingPackage.Extension,
        });
        if (file is null) return;
        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        // The package is read from disk: flush pending annotation saves and the reading position first.
        SaveAllReadingPositions();
        var vp = _controller.FocusedViewport is { } focused && focused.Owner == tab.State ? focused : tab.Viewport;
        var view = new PackageViewState
        {
            Rotation = tab.State.ViewRotation,
            Page = vp.CurrentPage,
            Zoom = vp.Camera.Zoom,
            OffsetX = vp.Camera.OffsetX,
            OffsetY = vp.Camera.OffsetY,
            HasSavedCamera = true,
            ColourEffect = tab.State.ColourEffect,
        };
        string pdfPath = tab.FilePath;
        string? password = tab.Pdf.Password;

        _packagingTabs.Add(tab);
        try
        {
            await Task.Run(() => ReadingPackage.Write(pdfPath, outputPath, password, encrypted, view, DocumentIdentity.Peek));
            ShowStatusToast($"Reading package saved: {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            _logger.Error("[ReadingPackage] Save failed", ex);
            ShowStatusToast($"Couldn't save the reading package: {ex.Message}");
        }
        finally
        {
            _packagingTabs.Remove(tab);
        }
    }

    /// <summary>
    /// Opens a reading package. When the document is already on this computer the reader confirms adding the
    /// package to that copy; otherwise the PDF is saved where the reader picks. Either way the package's position and rotation win, and its annotations, bookmarks and
    /// portals are merged in (an id the copy already has is skipped).
    /// </summary>
    public async Task ImportPackage(string packagePath)
    {
        if (_window is null || DialogOwner is not { } owner || IsScanAllActive) return;
        string scratch = Path.Combine(Path.GetTempPath(), $"railreader2-import-{Guid.NewGuid():N}");
        try
        {
            OpenedPackage package;
            try
            {
                package = await Task.Run(() => ReadingPackage.Read(packagePath, scratch));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                ShowStatusToast(ex.Message);
                return;
            }

            // A copy of a document this computer knows gets the same content id (that is how notes follow a
            // copied PDF), so it would share its notes, portals and rotation: a known document is always
            // merged into, never saved separately. An open tab of it wins over the paths on record (which are
            // canonical, while a tab keeps the spelling it was opened by).
            var openTabs = Tabs.Select(t => t.FilePath).ToList();
            string? local = await Task.Run(() => FindLocalCopy(package, openTabs));
            bool merge = local is not null;
            if (merge && !await new ImportPackageDialog(local!).ShowDialog<bool>(owner)) return;

            string? target = merge ? local : await SavePackagePdfAs(package, packagePath);
            if (target is null) return;

            // Not open yet: store the package's position and rotation so the open restores them (camera
            // offsets included). Already open: they are applied to the live view after the merge.
            bool wasOpen = FindTabIndex(target) >= 0;
            if (!wasOpen)
            {
                await Task.Run(() => DocumentIdentity.Resolve(target));
                StoreViewState(target, package.View);
            }

            if (FindTabIndex(target) is var index and >= 0) SelectTab(index);
            else await OpenDocument(target);
            if (ActiveTab is not { } tab || !SamePath(tab.FilePath, target)) return;   // open failed or was cancelled

            // A separate copy is the package's PDF itself: its in-PDF annotations are already open.
            int annotations = MergePackageAnnotations(tab, package, includeInPdf: merge);
            int portals = MergePackagePortals(tab, package.Portals);
            if (wasOpen) ApplyViewState(tab, package.View);

            ShowStatusToast(annotations + portals == 0
                ? $"Opened {Path.GetFileName(packagePath)}"
                : $"Opened {Path.GetFileName(packagePath)}: {Count(annotations, "annotation")}, {Count(portals, "portal")} added");
        }
        catch (Exception ex)
        {
            _logger.Error("[ReadingPackage] Import failed", ex);
            ShowStatusToast($"Couldn't open the reading package: {ex.Message}");
        }
        finally
        {
            ReadingPackage.TryDeleteDirectory(scratch);
        }

        static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    /// <summary>
    /// A PDF on this computer that is the package's document: one with these exact bytes, else one opened
    /// under the package's content id. An open tab of that document comes first, as its tab spelling, so the
    /// caller finds the tab. Each path on record is re-resolved; that drops one now holding bytes known as
    /// another document, but not one replaced by bytes never seen (the identity map treats new bytes at a
    /// known path as that document rewritten), which is why the reader confirms the path.
    /// </summary>
    private static string? FindLocalCopy(OpenedPackage package, IReadOnlyList<string> openTabs)
    {
        foreach (var id in new[] { DocumentIdentity.Lookup(package.PdfPath), package.Manifest.Pdf.ContentId }.Distinct())
        {
            if (string.IsNullOrEmpty(id)) continue;
            foreach (var path in openTabs)
                if (DocumentIdentity.Peek(path) == id)
                    return path;
            foreach (var path in DocumentIdentity.KnownPaths(id))
                if (File.Exists(path) && DocumentIdentity.Resolve(path) == id)
                    return path;
        }
        return null;
    }

    /// <summary>Asks where to save the package's PDF (suggesting its original name beside the package, or
    /// <c>name (2).pdf</c> when taken) and copies it there. Null when cancelled or refused.
    /// <para>An existing file is refused: the identity map keys by path and treats new bytes at a known path as
    /// the same document rewritten, so overwriting another PDF would hand its notes and portals to this one.</para></summary>
    private async Task<string?> SavePackagePdfAs(OpenedPackage package, string packagePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? "";
        var name = Path.GetFileName(package.Manifest.Pdf.FileName);
        if (string.IsNullOrWhiteSpace(name)) name = ReadingPackage.PdfEntry;
        var stem = Path.Combine(folder, Path.GetFileNameWithoutExtension(name));
        var suggested = Path.Combine(folder, name);
        for (int n = 2; File.Exists(suggested); n++) suggested = $"{stem} ({n}).pdf";

        var file = await _window!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the Package's PDF",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            SuggestedFileName = Path.GetFileName(suggested),
            SuggestedStartLocation = await _window.StorageProvider.TryGetFolderFromPathAsync(folder),
        });
        if (file is null) return null;
        var dest = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (dest is null) return null;
        dest = Path.GetFullPath(dest);
        if (File.Exists(dest))
        {
            ShowStatusToast("A file with that name already exists — choose a new name");
            return null;
        }
        await Task.Run(() => File.Copy(package.PdfPath, dest));
        return dest;
    }

    /// <summary>Writes a view state where <see cref="OpenDocument"/> restores it from. The document must
    /// already be resolved (rotation is keyed by its content id).</summary>
    private void StoreViewState(string pdfPath, PackageViewState view)
    {
        ViewRotationStore.Save(pdfPath, view.Rotation);
        if (view.HasSavedCamera)
        {
            AppConfig.SaveReadingPosition(pdfPath, view.Page, view.Zoom, view.OffsetX, view.OffsetY, view.ColourEffect);
            return;
        }
        AppConfig.AddRecentFile(pdfPath);
        if (AppConfig.GetReadingPosition(pdfPath) is { } entry)
        {
            entry.Page = view.Page;
            entry.ColourEffect = view.ColourEffect;
            AppConfig.Save();
        }
    }

    /// <summary>Applies a view state to a document that was already open: rotation, page, zoom, colour effect.</summary>
    private void ApplyViewState(TabViewModel tab, PackageViewState view)
    {
        if (view.Rotation != tab.State.ViewRotation) SetViewRotation(view.Rotation);
        Dispatch(() => _controller.GoToPage(Math.Clamp(view.Page, 0, Math.Max(0, tab.PageCount - 1)), recordHistory: false),
            InvalidateAfterNavigation);
        if (view.HasSavedCamera && view.Zoom > 0 && double.IsFinite(view.Zoom)
            && double.IsFinite(view.OffsetX) && double.IsFinite(view.OffsetY)
            && _controller.FocusedViewport is { } vp && vp.Owner == tab.State)
        {
            // The same restore Core's AddDocument does from the recent-files entry, so the result
            // doesn't depend on whether the copy happened to be open.
            vp.Camera.Zoom = Math.Clamp(view.Zoom, Camera.ZoomMin, Camera.ZoomMax);
            vp.Camera.OffsetX = view.OffsetX;
            vp.Camera.OffsetY = view.OffsetY;
            vp.ClampCamera(vp.Width, vp.Height);
            vp.UpdateRenderDpiIfNeeded();
            InvalidateAll();
        }
        if (view.ColourEffect is { } effect && effect != tab.State.ColourEffect) SetColourEffect(effect);
    }

    /// <summary>
    /// Merges the package's annotations into the open document: those stored in the package's PDF (when
    /// <paramref name="includeInPdf"/>: a separate copy already has them), and its
    /// sidecar part. Core's merge (0.76.0, RailReaderCore#168) skips one the document already has (same
    /// <c>/NM</c>, or the same content), keeps replies attached, and adds the rest as RailReader-authored so a
    /// save writes them (into the PDF, or the sidecar when the PDF is read-only). An annotation edited on both
    /// copies still comes back twice, and one deleted here but still in the package is added back.
    /// </summary>
    private int MergePackageAnnotations(TabViewModel tab, OpenedPackage package, bool includeInPdf)
    {
        var incoming = new AnnotationFile();
        if (includeInPdf)
        {
            try
            {
                if (new PdfAnnotationStore().Load(package.PdfPath, tab.Pdf.Password) is { } native)
                    AnnotationService.MergeInto(incoming, native);
            }
            catch (PdfPasswordRequiredException)
            {
                // The package's PDF has another password than the copy: its in-PDF annotations can't be read.
                _logger.Warn("[ReadingPackage] Couldn't read the package PDF's annotations (different password)");
            }
        }
        AnnotationService.MergeInto(incoming, package.Annotations, skipEquivalent: true);

        int added = AnnotationService.MergeInto(tab.State.Annotations, incoming, skipEquivalent: true);
        if (added > 0) ApplyMergedAnnotations(tab);
        return added;
    }

    /// <summary>Adds the package's portals the document doesn't have (by id) and saves the set.</summary>
    private int MergePackagePortals(TabViewModel tab, PortalSet incoming)
    {
        var portals = tab.Portals;
        var ids = portals.Portals.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var added = incoming.Portals.Where(p => ids.Add(p.Id)).ToList();
        if (added.Count == 0) return 0;
        portals.Portals.AddRange(added);
        portals.Save(tab.FilePath);
        NotifyPortalsChanged();
        InvalidatePortalMarkers();
        EvaluatePortals(forceRender: true);
        return added.Count;
    }

    private int FindTabIndex(string path)
    {
        for (int i = 0; i < Tabs.Count; i++)
            if (SamePath(Tabs[i].FilePath, path)) return i;
        return -1;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);
}
