using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Core.Services;

public sealed class ColourEffectShaders : IDisposable
{
    public ColourEffect Effect { get; set; }
    public float Intensity { get; set; } = 1.0f;

    private readonly Dictionary<ColourEffect, SKRuntimeEffect> _effects = [];

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

    private const string BionicFadeSksl = """
        uniform float intensity;
        half4 main(half4 color) {
            half lum = dot(color.rgb, half3(0.299, 0.587, 0.114));
            half factor = smoothstep(0.85, 0.5, lum);
            half3 faded = mix(color.rgb, half3(1.0), intensity * factor);
            return half4(faded, color.a);
        }
        """;

    private SKRuntimeEffect? _bionicEffect;

    public ColourEffectShaders()
    {
        CompileAndStore(ColourEffect.HighContrast, HighContrastSksl);
        CompileAndStore(ColourEffect.HighVisibility, HighVisibilitySksl);
        CompileAndStore(ColourEffect.Amber, AmberSksl);
        CompileAndStore(ColourEffect.Invert, InvertSksl);
        _bionicEffect = CompileColorFilter("BionicFade", BionicFadeSksl);
    }

    private static SKRuntimeEffect? CompileColorFilter(string name, string sksl)
    {
        var compiled = SKRuntimeEffect.CreateColorFilter(sksl, out var error);
        if (compiled is null)
            Console.Error.WriteLine($"Failed to compile {name} shader: {error}");
        return compiled;
    }

    private void CompileAndStore(ColourEffect effect, string sksl)
    {
        var compiled = CompileColorFilter(effect.ToString(), sksl);
        if (compiled is not null)
            _effects[effect] = compiled;
    }

    public bool HasActiveEffect => Effect != ColourEffect.None && _effects.ContainsKey(Effect);

    public SKColorFilter? CreateColorFilter()
    {
        if (!_effects.TryGetValue(Effect, out var rt))
            return null;

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

    public SKColorFilter? CreateBionicColorFilter(float intensity)
    {
        if (_bionicEffect is null) return null;
        var builder = new SKRuntimeColorFilterBuilder(_bionicEffect);
        builder.Uniforms["intensity"] = intensity;
        return builder.Build();
    }

    public void Dispose()
    {
        foreach (var effect in _effects.Values)
            effect.Dispose();
        _effects.Clear();
        _bionicEffect?.Dispose();
        _bionicEffect = null;
    }
}
