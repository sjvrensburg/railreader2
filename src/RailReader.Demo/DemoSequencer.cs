using System.Globalization;

namespace RailReader.Demo;

/// <summary>
/// Executes a <see cref="DemoScript"/> against an <see cref="IControlClient"/>: issues each verb
/// and waits the right way before the next step. Motion verbs default to waiting on the real
/// <c>Settled</c> signal (so a cut lands on the actual animation end); <c>goto_page</c> waits on
/// <c>PageChanged</c>; <c>hold</c> is wall-clock dwell for pacing. Blind sleeps are never used to
/// time motion — only dwell. Every default can be overridden per step with <c>wait:</c>.
/// </summary>
public sealed class DemoSequencer
{
    private readonly IControlClient _client;
    private readonly TextWriter _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _settleTimeout;

    /// <param name="delay">Injectable delay (tests pass a no-op so dwell/timeouts don't sleep).</param>
    /// <param name="settleTimeout">How long to wait for a signal before giving up and moving on.</param>
    public DemoSequencer(
        IControlClient client,
        TextWriter log,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? settleTimeout = null)
    {
        _client = client;
        _log = log;
        _delay = delay ?? Task.Delay;
        _settleTimeout = settleTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Run the script. If <paramref name="recorder"/> is given and the script has an
    /// <c>output:</c>, the whole run is bracketed by a screen capture (with a short lead-in/out so
    /// the video doesn't start/end exactly on the first/last motion). The capture is continuous;
    /// fidelity comes from each step cutting on the real <c>Settled</c> signal, not from per-step
    /// video edits.</summary>
    public async Task RunAsync(DemoScript script, IScreenRecorder? recorder = null, CancellationToken ct = default)
    {
        bool capturing = recorder is not null && !string.IsNullOrEmpty(script.Output);

        if (script.Fullscreen)
        {
            _log.WriteLine("fullscreen on");
            await _client.SetFullScreenAsync(true, ct).ConfigureAwait(false);
            await _delay(FullScreenSettle, ct).ConfigureAwait(false); // let the resize/relayout settle
        }

        int start = 0;
        if (capturing)
        {
            // Pre-roll: run leading 'open' steps BEFORE recording starts so the slow PDF load
            // (and first-page render) isn't captured as dead time at the head of the video.
            while (start < script.Steps.Count && script.Steps[start].Verb == "open")
            {
                await ExecuteAsync(script, script.Steps[start], ct).ConfigureAwait(false);
                start++;
            }
            // Cursor: only the draw on/off is reliable on Wayland. "show"/"follow" draw the
            // pointer; "hidden"/"park"/unset hide it. (Pointer *motion* for park/follow isn't
            // wired — synthetic pointer control is unreliable on this platform.)
            bool drawCursor = script.Cursor?.Trim().ToLowerInvariant() is "show" or "follow";
            await recorder!.StartAsync(script.Output!, script.Fps ?? 60, drawCursor, ct).ConfigureAwait(false);
            await _delay(LeadIn, ct).ConfigureAwait(false);
        }

        try
        {
            for (int i = start; i < script.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteAsync(script, script.Steps[i], ct).ConfigureAwait(false);
            }
            _log.WriteLine("Demo complete.");
        }
        finally
        {
            if (capturing)
            {
                // Lead-out + stop are best-effort even on cancellation; use a fresh token so a
                // cancelled run still flushes the tail and finalises the file.
                try { await _delay(LeadOut, CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
                await recorder!.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (script.Fullscreen)
                try { await _client.SetFullScreenAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private static readonly TimeSpan FullScreenSettle = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LeadOut = TimeSpan.FromMilliseconds(800);

    private enum WaitKind { None, Settled, PageChanged, Duration }

    private async Task ExecuteAsync(DemoScript script, DemoStep step, CancellationToken ct)
    {
        switch (step.Verb)
        {
            case "open":
            {
                string path = script.Source
                    ?? throw new DemoRunException("the 'open' step needs a 'source:' setting", step.Line);
                _log.WriteLine($"open {path}");
                bool ok = await _client.OpenDocumentAsync(path, ct).ConfigureAwait(false);
                if (!ok) throw new DemoRunException($"failed to open '{path}'", step.Line);
                break;
            }
            case "goto_page":
            {
                int page = Int(step, DemoStep.ValueKey);
                _log.WriteLine($"goto_page {page}");
                await IssueAndWaitAsync(step, WaitKind.PageChanged,
                    () => _client.GoToPageAsync(page, ct).ContinueWith(_ => (bool?)null, ct), ct);
                break;
            }
            case "fit_page":
                _log.WriteLine("fit_page");
                await IssueAndWaitAsync(step, WaitKind.None,
                    () => _client.FitPageAsync(ct).ContinueWith(_ => (bool?)null, ct), ct);
                break;
            case "fit_width":
                _log.WriteLine("fit_width");
                await IssueAndWaitAsync(step, WaitKind.None,
                    () => _client.FitWidthAsync(ct).ContinueWith(_ => (bool?)null, ct), ct);
                break;
            case "frame_role":
            {
                string role = Str(step, "role");
                int occ = OptInt(step, "index", 0);
                double zoom = OptDouble(step, "zoom", 0);
                _log.WriteLine($"frame_role role={role} index={occ} zoom={(zoom > 0 ? zoom.ToString(CultureInfo.InvariantCulture) : "auto")}");
                await IssueAndWaitAsync(step, WaitKind.Settled,
                    async () => await _client.FrameRoleAsync(role, occ, zoom, ct).ConfigureAwait(false), ct);
                break;
            }
            case "frame_block":
            {
                if (step.Args.ContainsKey("role"))
                    throw new DemoRunException("frame_block takes 'index' (a page block index); use frame_role for a role", step.Line);
                int index = Int(step, "index");
                double zoom = OptDouble(step, "zoom", 0);
                _log.WriteLine($"frame_block index={index} zoom={(zoom > 0 ? zoom.ToString(CultureInfo.InvariantCulture) : "auto")}");
                await IssueAndWaitAsync(step, WaitKind.Settled,
                    async () => await _client.FrameBlockAsync(index, zoom, ct).ConfigureAwait(false), ct);
                break;
            }
            case "hold":
            {
                var dur = Duration(step, DemoStep.ValueKey);
                _log.WriteLine($"hold {dur.TotalMilliseconds:0}ms");
                await _delay(dur, ct).ConfigureAwait(false);
                break;
            }
            case "set_zoom":
            {
                double pct = step.Args.ContainsKey("percent") ? OptDouble(step, "percent", 0) : OptDouble(step, DemoStep.ValueKey, 0);
                if (pct <= 0) throw new DemoRunException("set_zoom needs a positive percent", step.Line);
                _log.WriteLine($"set_zoom {pct.ToString(CultureInfo.InvariantCulture)}%");
                await IssueAndWaitAsync(step, WaitKind.Settled,
                    () => _client.SetZoomAsync(pct, ct).ContinueWith(_ => (bool?)null, ct), ct);
                break;
            }
            case "colour_effect":
            case "color_effect":
            {
                string name = step.Args.ContainsKey("name") ? Str(step, "name") : Str(step, DemoStep.ValueKey);
                _log.WriteLine($"colour_effect {name}");
                if (!await _client.SetColourEffectAsync(name, ct).ConfigureAwait(false))
                    _log.WriteLine($"  (unknown colour effect '{name}')");
                break;
            }
            case "navigate":
            {
                string role = step.Args.ContainsKey("role") ? Str(step, "role") : Str(step, DemoStep.ValueKey);
                bool forward = !DirIsBackward(step);
                _log.WriteLine($"navigate {(forward ? "next" : "prev")} {role}");
                await IssueAndWaitAsync(step, WaitKind.Settled,
                    async () => await _client.NavigateRoleAsync(role, forward, ct).ConfigureAwait(false), ct);
                break;
            }
            case "rail_advance_lines":
            {
                int count = step.Args.ContainsKey("count") ? Int(step, "count") : Int(step, DemoStep.ValueKey);
                bool forward = !DirIsBackward(step); // dir: up reverses
                var perLine = step.Args.ContainsKey("per_line")
                    ? ParseDuration(step.Args["per_line"], step.Line)
                    : TimeSpan.FromMilliseconds(600);
                _log.WriteLine($"rail_advance_lines count={count} dir={(forward ? "down" : "up")} per_line={perLine.TotalMilliseconds:0}ms");
                for (int n = 0; n < count; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    await IssueAsync(WaitKind.Settled, default,
                        () => _client.RailAdvanceLineAsync(forward, ct).ContinueWith(t => (bool?)t.Result, ct), ct).ConfigureAwait(false);
                    await _delay(perLine, ct).ConfigureAwait(false);
                }
                break;
            }
            case "line_highlight":
            {
                bool on = Bool(step, DemoStep.ValueKey);
                _log.WriteLine($"line_highlight {(on ? "on" : "off")}");
                await _client.SetLineHighlightAsync(on, ct).ConfigureAwait(false);
                break;
            }
            case "line_focus_blur":
            {
                bool on = Bool(step, DemoStep.ValueKey);
                _log.WriteLine($"line_focus_blur {(on ? "on" : "off")}");
                await _client.SetLineFocusBlurAsync(on, ct).ConfigureAwait(false);
                break;
            }
            case "key":
                await SendKeyAsync(step, down: true, up: true, ct).ConfigureAwait(false);
                break;
            case "key_down":
                await SendKeyAsync(step, down: true, up: false, ct).ConfigureAwait(false);
                break;
            case "key_up":
                await SendKeyAsync(step, down: false, up: true, ct).ConfigureAwait(false);
                break;
            default:
                throw new DemoRunException($"unknown verb '{step.Verb}'", step.Line);
        }
    }

    /// <summary>Issue a verb then apply its wait. For signal waits the subscription is set up
    /// BEFORE issuing so a fast signal can't be missed. <paramref name="issue"/> returns
    /// true/false for verbs that report a match (frame_*) or null for void verbs; a false result
    /// means nothing animates, so the signal wait is skipped.</summary>
    private async Task IssueAndWaitAsync(DemoStep step, WaitKind defaultWait, Func<Task<bool?>> issue, CancellationToken ct)
    {
        var (kind, dur) = ResolveWait(step, defaultWait);
        await IssueAsync(kind, dur, issue, ct).ConfigureAwait(false);
    }

    /// <summary>Issue a verb then wait per an explicit kind (used directly by loops like
    /// rail_advance_lines that don't take a per-step <c>wait:</c> override).</summary>
    private async Task IssueAsync(WaitKind kind, TimeSpan dur, Func<Task<bool?>> issue, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action settled = () => tcs.TrySetResult();
        Action<int> paged = _ => tcs.TrySetResult();
        if (kind == WaitKind.Settled) _client.Settled += settled;
        else if (kind == WaitKind.PageChanged) _client.PageChanged += paged;

        try
        {
            bool? ok = await issue().ConfigureAwait(false);

            switch (kind)
            {
                case WaitKind.None:
                    break;
                case WaitKind.Duration:
                    await _delay(dur, ct).ConfigureAwait(false);
                    break;
                default: // Settled / PageChanged
                    if (ok == false)
                    {
                        _log.WriteLine("  (matched nothing — not waiting)");
                        break;
                    }
                    await WaitSignalOrTimeoutAsync(tcs.Task, kind, ct).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            if (kind == WaitKind.Settled) _client.Settled -= settled;
            else if (kind == WaitKind.PageChanged) _client.PageChanged -= paged;
        }
    }

    private async Task WaitSignalOrTimeoutAsync(Task signal, WaitKind kind, CancellationToken ct)
    {
        if (signal.IsCompleted) return; // signal already fired during/just after the verb — don't arm a timeout
        var completed = await Task.WhenAny(signal, _delay(_settleTimeout, ct)).ConfigureAwait(false);
        if (completed != signal)
            _log.WriteLine($"  (timed out waiting for {kind} after {_settleTimeout.TotalSeconds:0}s — continuing)");
    }

    private (WaitKind kind, TimeSpan dur) ResolveWait(DemoStep step, WaitKind defaultWait)
    {
        if (step.Wait is null) return (defaultWait, default);
        return step.Wait.Trim().ToLowerInvariant() switch
        {
            "settled" => (WaitKind.Settled, default),
            "none" => (WaitKind.None, default),
            "pagechanged" or "page_changed" => (WaitKind.PageChanged, default),
            var s => (WaitKind.Duration, ParseDuration(s, step.Line)),
        };
    }

    // --- arg helpers ---

    private static string Str(DemoStep s, string key) =>
        s.Args.TryGetValue(key, out var v) && v.Length > 0
            ? v
            : throw new DemoRunException($"'{s.Verb}' requires '{key}'", s.Line);

    private static int Int(DemoStep s, string key)
    {
        var raw = Str(s, key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new DemoRunException($"'{s.Verb}' {key} must be an integer, got '{raw}'", s.Line);
    }

    private static int OptInt(DemoStep s, string key, int fallback) =>
        s.Args.ContainsKey(key) ? Int(s, key) : fallback;

    private static double OptDouble(DemoStep s, string key, double fallback)
    {
        if (!s.Args.TryGetValue(key, out var raw)) return fallback;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new DemoRunException($"'{s.Verb}' {key} must be a number, got '{raw}'", s.Line);
    }

    private static TimeSpan Duration(DemoStep s, string key) => ParseDuration(Str(s, key), s.Line);

    private static bool Bool(DemoStep s, string key) => Str(s, key).ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1" => true,
        "off" or "false" or "no" or "0" => false,
        var v => throw new DemoRunException($"'{s.Verb}' {key} must be on/off, got '{v}'", s.Line),
    };

    /// <summary>A step's direction arg ('dir') indicates backward when up/prev/previous/back.</summary>
    private static bool DirIsBackward(DemoStep s) =>
        s.Args.TryGetValue("dir", out var d) && d.ToLowerInvariant() is "up" or "prev" or "previous" or "back" or "backward";

    /// <summary>Drive a keyboard shortcut (the generic key verbs). Honours a per-step <c>wait:</c>
    /// override (default none) — add <c>wait: settled</c> for shortcuts that animate.</summary>
    private async Task SendKeyAsync(DemoStep step, bool down, bool up, CancellationToken ct)
    {
        string chord = step.Args.ContainsKey("chord") ? Str(step, "chord") : Str(step, DemoStep.ValueKey);
        _log.WriteLine($"{step.Verb} {chord}");
        await IssueAndWaitAsync(step, WaitKind.None, async () =>
        {
            if (!await _client.SendKeyAsync(chord, down, up, ct).ConfigureAwait(false))
                _log.WriteLine($"  (unknown key chord '{chord}')");
            return (bool?)null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Parse "800ms", "2s", "1.5s", or a bare number (milliseconds).</summary>
    internal static TimeSpan ParseDuration(string raw, int line)
    {
        string t = raw.Trim().ToLowerInvariant();
        double Num(string n) => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : throw new DemoRunException($"invalid duration '{raw}'", line);
        if (t.EndsWith("ms", StringComparison.Ordinal)) return TimeSpan.FromMilliseconds(Num(t[..^2]));
        if (t.EndsWith('s')) return TimeSpan.FromSeconds(Num(t[..^1]));
        return TimeSpan.FromMilliseconds(Num(t));
    }
}

/// <summary>Thrown when a step can't be executed (bad/missing arg, unknown verb, open failure).</summary>
public sealed class DemoRunException(string message, int line)
    : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}
