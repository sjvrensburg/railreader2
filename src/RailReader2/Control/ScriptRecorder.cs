using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Input;

namespace RailReader2.ControlBus;

/// <summary>
/// Records a live RailReader2 session into an editable demo DSL script (AutoIt-style:
/// record → tweak → replay). Captures the document opened plus every *handled* keyboard shortcut
/// with timing, pairing key down/up so a held key (e.g. Right for rail scrolling) becomes
/// <c>key_down</c>/<c>key_up</c> and a quick press becomes <c>key</c>. Inter-action gaps become
/// <c>hold:</c> steps. Replay goes through the control bus, not synthetic OS input.
/// </summary>
public sealed class ScriptRecorder
{
    private const double TapThresholdMs = 300;  // shorter press → a tap (key:), longer → a hold
    private const double MinHoldMs = 120;       // gaps below this aren't worth a hold step
    private const double RoundMs = 100;         // round holds/durations for clean scripts

    /// <summary>Where <see cref="Save()"/> writes — set at launch (--record-script) or by the
    /// in-app toggle (a timestamped default).</summary>
    public string? SavePath { get; init; }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<Key, (double down, KeyModifiers mods)> _pressed = [];
    private string? _source;

    private enum Kind { Open, Tap, Hold, Frame }
    private readonly record struct Entry(double Start, double End, Kind Kind, string Text);

    /// <summary>Record the document opened (the first becomes the script's <c>source:</c>).</summary>
    public void RecordOpen(string path)
    {
        lock (_gate)
        {
            _source ??= path;
            double t = _clock.Elapsed.TotalMilliseconds;
            _entries.Add(new Entry(t, t, Kind.Open, path));
        }
    }

    /// <summary>Record a handled key-down. Repeats (auto-repeat) and pure modifier keys are ignored.</summary>
    public void RecordKeyDown(Key key, KeyModifiers mods)
    {
        if (IsModifier(key)) return;
        lock (_gate)
            _pressed.TryAdd(key, (_clock.Elapsed.TotalMilliseconds, mods));
    }

    /// <summary>Record a key-up, pairing it with its down to decide tap vs hold.</summary>
    public void RecordKeyUp(Key key, KeyModifiers mods)
    {
        if (IsModifier(key)) return;
        lock (_gate)
        {
            if (!_pressed.Remove(key, out var d)) return;
            double up = _clock.Elapsed.TotalMilliseconds;
            string chord = KeyChord.Format(key, d.mods);
            var kind = (up - d.down) >= TapThresholdMs ? Kind.Hold : Kind.Tap;
            _entries.Add(new Entry(d.down, up, kind, chord));
        }
    }

    /// <summary>Record a double-click "frame this block" gesture (smooth zoom into rail mode at the
    /// block's start) as a <c>frame_block</c> step — the mouse-driven way to zoom onto a specific
    /// block, which the keyboard can't do. <paramref name="blockIndex"/> is the current-page block
    /// index (Core's <c>analysis.Blocks</c> / <c>SmoothlyFrameBlock</c> index space).</summary>
    public void RecordFrameBlock(int blockIndex)
    {
        lock (_gate)
        {
            double t = _clock.Elapsed.TotalMilliseconds;
            _entries.Add(new Entry(t, t, Kind.Frame,
                blockIndex.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public void Save(string path)
    {
        using var w = new StreamWriter(path);
        WriteTo(w);
    }

    /// <summary>Save to <see cref="SavePath"/> (no-op if unset).</summary>
    public void Save()
    {
        if (SavePath is { } p) Save(p);
    }

    /// <summary>Emit the recorded session as a demo DSL script.</summary>
    public void WriteTo(TextWriter w)
    {
        List<Entry> entries;
        string? source;
        lock (_gate)
        {
            entries = [.. _entries];
            source = _source;
        }
        entries.Sort((a, b) => a.Start.CompareTo(b.Start));

        w.WriteLine($"demo: {(source is null ? "recorded" : Path.GetFileNameWithoutExtension(source))}");
        if (source is not null) w.WriteLine($"source: {source}");
        w.WriteLine("fps: 60");
        w.WriteLine("# recorded session — tidy the holds, drop fumbles, swap manual zooms for frame_role, etc.");
        w.WriteLine("steps:");

        double cursor = entries.Count > 0 ? entries[0].Start : 0; // no leading hold
        foreach (var e in entries)
        {
            EmitHold(w, e.Start - cursor);
            switch (e.Kind)
            {
                case Kind.Open:
                    w.WriteLine("  - open");
                    cursor = e.End;
                    break;
                case Kind.Tap:
                    w.WriteLine($"  - key: {e.Text}");
                    cursor = e.End;
                    break;
                case Kind.Frame:
                    // Auto-fit zoom (no zoom: arg); add e.g. `zoom: 12` when tuning for a hero shot.
                    w.WriteLine($"  - frame_block: {{ index: {e.Text} }}");
                    cursor = e.End;
                    break;
                case Kind.Hold:
                    w.WriteLine($"  - key_down: {e.Text}");
                    EmitHold(w, e.End - e.Start);
                    w.WriteLine($"  - key_up: {e.Text}");
                    cursor = e.End;
                    break;
            }
        }
    }

    private static void EmitHold(TextWriter w, double ms)
    {
        if (ms < MinHoldMs) return;
        double rounded = Math.Round(ms / RoundMs) * RoundMs;
        if (rounded < MinHoldMs) return;
        w.WriteLine($"  - hold: {rounded.ToString("0", CultureInfo.InvariantCulture)}ms");
    }

    private static bool IsModifier(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
