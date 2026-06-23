namespace RailReader2.Services;

/// <summary>
/// Shares one reference-counted <see cref="PortalSet"/> per PDF across all tabs/panes that view it,
/// mirroring Core's annotation file manager. Without this, duplicate tabs of the same PDF each held
/// their own <see cref="PortalSet"/> over the same SHA-keyed sidecar, so a save from one tab silently
/// clobbered the other's edits (last-writer-wins). With one shared instance, every view mutates and
/// saves the same in-memory list — no lost portals.
///
/// UI-thread only (all portal authoring/eval runs on the UI thread), so no locking is needed.
/// </summary>
internal sealed class PortalSetManager
{
    public static PortalSetManager Default { get; } = new();

    private readonly Dictionary<string, Entry> _byPath = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required PortalSet Set;
        public int RefCount;
    }

    private static string Key(string pdfPath) => System.IO.Path.GetFullPath(pdfPath);

    /// <summary>Check out the shared <see cref="PortalSet"/> for <paramref name="pdfPath"/>, loading it
    /// from disk on first use. Each call must be paired with a <see cref="Release"/>.</summary>
    public PortalSet Checkout(string pdfPath)
    {
        string key = Key(pdfPath);
        if (!_byPath.TryGetValue(key, out var entry))
        {
            entry = new Entry { Set = PortalSet.Load(pdfPath) };
            _byPath[key] = entry;
        }
        entry.RefCount++;
        return entry.Set;
    }

    /// <summary>Release a checkout. The shared instance is dropped once its last holder releases.</summary>
    public void Release(string pdfPath)
    {
        string key = Key(pdfPath);
        if (!_byPath.TryGetValue(key, out var entry)) return;
        if (--entry.RefCount <= 0)
            _byPath.Remove(key);
    }
}
