using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Which GPU draws the window. Shell-managed sidecar (<c>ConfigDir/display_gpu_prefs.json</c>) like
/// <see cref="OcrPreferences"/>. Read once in <see cref="Program.Main"/>, before Avalonia creates its
/// OpenGL context — the choice is made when the GL driver loads, so it takes effect on restart.
/// </summary>
public sealed class DisplayGpuPreferences
{
    /// <summary>Linux only: render on the discrete GPU of a hybrid (PRIME) laptop. Off by default —
    /// the integrated GPU is the battery-friendly choice and is enough for small windows, but on a
    /// large or high-resolution viewport it can take 35-45 ms per moving frame where a discrete GPU
    /// takes ~16 ms (measured: Iris Xe vs RTX A2000 at 2560x1440), which reads as stutter.</summary>
    public bool PreferDiscreteGpu { get; set; }

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "display_gpu_prefs.json");

    public static DisplayGpuPreferences Load()
        => JsonSidecar.Load(Path, DisplayGpuPreferencesJsonContext.Default.DisplayGpuPreferences,
            static () => new DisplayGpuPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, DisplayGpuPreferencesJsonContext.Default.DisplayGpuPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(DisplayGpuPreferences))]
internal partial class DisplayGpuPreferencesJsonContext : JsonSerializerContext;

/// <summary>
/// Linux hybrid-GPU support: detects the machine's GPUs, applies <see cref="DisplayGpuPreferences"/>
/// by setting the PRIME render-offload environment variables before the GL driver loads, and records
/// the GL renderer actually in use.
/// </summary>
internal static class DisplayGpu
{
    private const string NvOffload = "__NV_PRIME_RENDER_OFFLOAD";
    private const string GlxVendor = "__GLX_VENDOR_LIBRARY_NAME";
    private const string DriPrime = "DRI_PRIME";

    // .NET's Environment.SetEnvironmentVariable only updates the runtime's own copy of the
    // environment on Unix — native code (libglvnd, Mesa) reads the C environ, so set it there too.
    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libGL.so.1")]
    private static extern IntPtr glGetString(uint name);
    private const uint GL_RENDERER = 0x1F01;

    private static int s_rendererProbed;

    /// <summary>What <see cref="Apply"/> did at startup, for Settings to show.</summary>
    public static string? AppliedNote { get; private set; }

    /// <summary>The GL_RENDERER string of the context the page layer draws with, once known.</summary>
    public static string? Renderer { get; private set; }

    /// <summary>GPU vendors found under /sys/class/drm (one entry per card), e.g. "Intel", "NVIDIA".</summary>
    public static IReadOnlyList<string> DetectGpus()
    {
        var gpus = new List<string>();
        if (!OperatingSystem.IsLinux()) return gpus;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                var name = System.IO.Path.GetFileName(dir);
                if (!name[4..].All(char.IsAsciiDigit) || name.Length == 4) continue; // skip card0-DP-1 etc.
                var vendorFile = System.IO.Path.Combine(dir, "device", "vendor");
                if (!File.Exists(vendorFile)) continue;
                gpus.Add(File.ReadAllText(vendorFile).Trim() switch
                {
                    "0x8086" => "Intel",
                    "0x10de" => "NVIDIA",
                    "0x1002" => "AMD",
                    var other => other,
                });
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Debug($"[GPU] DRM enumeration failed: {ex.Message}");
        }
        return gpus;
    }

    /// <summary>
    /// Call from <see cref="Program.Main"/> before Avalonia starts. When the preference is on, points
    /// GL at the discrete GPU: NVIDIA's PRIME render offload if the proprietary driver is loaded,
    /// otherwise Mesa's <c>DRI_PRIME</c>. Leaves the environment alone if the user already set any of
    /// these variables themselves (a launcher, <c>prime-run</c>, a desktop entry's "use discrete GPU").
    /// </summary>
    public static void Apply(ILogger logger)
    {
        if (!OperatingSystem.IsLinux() || !DisplayGpuPreferences.Load().PreferDiscreteGpu) return;

        if (Environment.GetEnvironmentVariable(NvOffload) is not null
            || Environment.GetEnvironmentVariable(GlxVendor) is not null
            || Environment.GetEnvironmentVariable(DriPrime) is not null)
        {
            AppliedNote = "GPU selection already set in the environment; left as is.";
            logger.Info($"[GPU] Prefer discrete GPU: {AppliedNote}");
            return;
        }

        if (File.Exists("/proc/driver/nvidia/version"))
        {
            Set(NvOffload, "1");
            Set(GlxVendor, "nvidia");
            AppliedNote = "Requested NVIDIA PRIME render offload.";
        }
        else if (DetectGpus().Count > 1)
        {
            Set(DriPrime, "1");
            AppliedNote = "Requested the discrete GPU via DRI_PRIME.";
        }
        else
        {
            AppliedNote = "Only one GPU found; nothing to switch.";
        }
        logger.Info($"[GPU] Prefer discrete GPU: {AppliedNote}");
    }

    /// <summary>Records the GL renderer once. Must be called on the render thread while the
    /// compositor's GL context is current (i.e. from a composition handler's OnRender).</summary>
    public static void ProbeRenderer()
    {
        if (!OperatingSystem.IsLinux() || Interlocked.Exchange(ref s_rendererProbed, 1) != 0) return;
        try
        {
            var ptr = glGetString(GL_RENDERER);
            Renderer = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
            RailReaderLogging.Logger.Info($"[GPU] GL renderer: {Renderer ?? "(unknown)"}");
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Debug($"[GPU] GL renderer probe failed: {ex.Message}");
        }
    }

    private static void Set(string name, string value)
    {
        setenv(name, value, 1);
        Environment.SetEnvironmentVariable(name, value);
    }
}
