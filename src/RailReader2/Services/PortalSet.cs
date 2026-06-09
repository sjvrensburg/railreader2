using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Anchors a portal endpoint to a detected layout block. <see cref="Page"/>/<see cref="Block"/>
/// is the fast path; <see cref="Role"/> plus the normalized bounding box
/// (<see cref="Nx"/>/<see cref="Ny"/>/<see cref="Nw"/>/<see cref="Nh"/>, each a fraction of the
/// page's width/height) make the anchor resolution-independent and let
/// <c>MainWindowViewModel.ResolveAnchorBlock</c> recover the block by nearest-centre same-role
/// match if a page is re-analysed into a different block order. See <c>docs/portals-design.md</c> §4.
/// </summary>
public sealed class PortalAnchor
{
    public int Page { get; set; }
    public int Block { get; set; }
    public string Role { get; set; } = "";
    public float Nx { get; set; }
    public float Ny { get; set; }
    public float Nw { get; set; }
    public float Nh { get; set; }
}

/// <summary>
/// A single linked-context portal: when the reading position enters the <see cref="Source"/>
/// block, the <see cref="Target"/> block is rendered into the Portals panel / pop-out window.
/// </summary>
public sealed class Portal
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public PortalAnchor Source { get; set; } = new();
    public PortalAnchor Target { get; set; } = new();
    public string CreatedUtc { get; set; } = "";
}

/// <summary>
/// Per-document set of portals. Shell-managed sidecar, mirroring
/// <see cref="CustomLayoutModelConfig"/> and the annotations sidecar: keyed by the SHA-256 of the
/// PDF's absolute path, stored at <c>ConfigDir/portals/&lt;sha256&gt;.json</c>. No RailReaderCore change.
/// </summary>
public sealed class PortalSet
{
    public int Version { get; set; } = 1;
    public List<Portal> Portals { get; set; } = [];

    private static string Dir => System.IO.Path.Combine(AppConfig.ConfigDir, "portals");

    /// <summary>Sidecar path for a PDF, keyed by the SHA-256 of its absolute path (same keying
    /// convention as the annotations sidecar). The shell computes the hash itself.</summary>
    public static string PathFor(string pdfPath)
    {
        string full = System.IO.Path.GetFullPath(pdfPath);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return System.IO.Path.Combine(Dir, Convert.ToHexStringLower(hash) + ".json");
    }

    public static PortalSet Load(string pdfPath)
    {
        try
        {
            var path = PathFor(pdfPath);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, PortalJsonContext.Default.PortalSet)
                    ?? new PortalSet();
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"Failed to load portals sidecar for {pdfPath}", ex);
        }
        return new PortalSet();
    }

    public void Save(string pdfPath)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, PortalJsonContext.Default.PortalSet);
            File.WriteAllText(PathFor(pdfPath), json);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"Failed to save portals sidecar for {pdfPath}", ex);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(PortalSet))]
internal partial class PortalJsonContext : JsonSerializerContext;
