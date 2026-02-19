using RailReader2.Models;
using SkiaSharp;

namespace RailReader2.Services;

public sealed class ColourEffectShaders : IDisposable
{
    public ColourEffect Effect { get; set; }
    public float Intensity { get; set; } = 1.0f;

    private SKRuntimeEffect? _highContrast;
    private SKRuntimeEffect? _highVisibility;
    private SKRuntimeEffect? _amber;
    private SKRuntimeEffect? _invert;

    private const string HighContrastSksl = """
        uniform float intensity;
        half4 main(half4 color) {
            half lum = dot(color.rgb, half3(0.299, 0.587, 0.114));
            half inv = 1.0 - lum;
            half t = clamp((inv - 0.15) / 0.7, 0.0, 1.0);
            half c = t * t * (3.0 - 2.0 * t);
            half3 effect = half3(c, c, c);
            half3 result = mix(color.rgb, effect, half(intensity));
            return half4(result, color.a);
        }
        """;

    private const string HighVisibilitySksl = """
        uniform float intensity;
        half4 main(half4 color) {
            half lum = dot(color.rgb, half3(0.299, 0.587, 0.114));
            half inv = 1.0 - lum;
            half3 effect = half3(inv, inv, 0.0);
            half3 result = mix(color.rgb, effect, half(intensity));
            return half4(result, color.a);
        }
        """;

    private const string AmberSksl = """
        uniform float intensity;
        half4 main(half4 color) {
            half3 tinted = clamp(color.rgb * half3(1.09, 1.03, 0.85), half3(0.0), half3(1.0));
            half3 result = mix(color.rgb, tinted, half(intensity));
            return half4(result, color.a);
        }
        """;

    private const string InvertSksl = """
        uniform float intensity;
        half4 main(half4 color) {
            half3 effect = 1.0 - color.rgb;
            half3 result = mix(color.rgb, effect, half(intensity));
            return half4(result, color.a);
        }
        """;

    public ColourEffectShaders()
    {
        _highContrast = Compile("HighContrast", HighContrastSksl);
        _highVisibility = Compile("HighVisibility", HighVisibilitySksl);
        _amber = Compile("Amber", AmberSksl);
        _invert = Compile("Invert", InvertSksl);
    }

    private static SKRuntimeEffect? Compile(string name, string sksl)
    {
        var effect = SKRuntimeEffect.CreateColorFilter(sksl, out var error);
        if (effect is null)
            Console.Error.WriteLine($"Failed to compile {name} shader: {error}");
        return effect;
    }

    private SKRuntimeEffect? RuntimeEffect => Effect switch
    {
        ColourEffect.HighContrast => _highContrast,
        ColourEffect.HighVisibility => _highVisibility,
        ColourEffect.Amber => _amber,
        ColourEffect.Invert => _invert,
        _ => null,
    };

    public bool HasActiveEffect => Effect != ColourEffect.None && RuntimeEffect is not null;

    public SKColorFilter? CreateColorFilter()
    {
        var rt = RuntimeEffect;
        if (rt is null) return null;

        var builder = new SKRuntimeColorFilterBuilder(rt);
        builder.Uniforms["intensity"] = Intensity;
        return builder.Build();
    }

    public SKPaint? CreatePaint()
    {
        var filter = CreateColorFilter();
        if (filter is null) return null;
        return new SKPaint { ColorFilter = filter };
    }

    public void Dispose()
    {
        _highContrast?.Dispose();
        _highVisibility?.Dispose();
        _amber?.Dispose();
        _invert?.Dispose();
    }
}
