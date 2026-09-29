using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RailReader2.Services;

namespace RailReader2.Controls;

/// <summary>
/// Adds spell checking to an ordinary <see cref="TextBox"/> without replacing its template:
/// misspelled words get a wavy underline, right-click (or the context-menu key) on one offers
/// suggestions plus Add to Dictionary / Ignore, and F7 selects the next misspelling and opens
/// the same menu beside it, so the whole flow works from the keyboard.
/// <para>
/// The underline is drawn by a <see cref="SquiggleOverlay"/> slipped into the Fluent template's
/// <c>Panel</c> right beside <c>PART_TextPresenter</c>. Being a sibling inside the
/// <c>ScrollViewer</c>, it scrolls and clips exactly like the text, and it positions each word from
/// the presenter's own <see cref="TextPresenter.TextLayout"/> — the same geometry the presenter
/// draws its selection highlight from — so wrapping, alignment and font scale line up for free.
/// </para>
/// <para>
/// The word being typed is not flagged until the caret leaves it (a half-typed word is always
/// "misspelled"); arrow keys, a click, or losing focus end the typing state.
/// </para>
/// </summary>
internal sealed class TextBoxSpellCheck
{
    private readonly TextBox _box;
    private readonly SpellCheckService _service;
    private readonly MenuFlyout _menu = new();
    private readonly PlacementMode _defaultPlacement;
    private TextPresenter? _presenter;
    private SquiggleOverlay? _overlay;
    private IReadOnlyList<Misspelling> _all = [];
    private IReadOnlyList<Misspelling> _visible = [];
    private bool _typing;
    private int _menuIndex = -1;

    /// <summary>Raised on the UI thread whenever <see cref="Misspellings"/> may have changed.</summary>
    public event Action? Changed;

    /// <summary>The misspellings currently underlined (excludes the word being typed).</summary>
    public IReadOnlyList<Misspelling> Misspellings => _visible;

    public static TextBoxSpellCheck Attach(TextBox box, SpellCheckService service) => new(box, service);

    private TextBoxSpellCheck(TextBox box, SpellCheckService service)
    {
        _box = box;
        _service = service;
        _defaultPlacement = _menu.Placement;
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu(); // never let the flyout exist empty
        box.ContextFlyout = _menu;

        box.TemplateApplied += OnTemplateApplied;
        // Text set from code (an existing note loaded for editing) isn't "being typed".
        box.TextChanged += (_, _) => { _typing = box.IsFocused; Recheck(); };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty) UpdateVisible();
        };
        box.LostFocus += (_, _) => { _typing = false; UpdateVisible(); };
        box.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.AddHandler(InputElement.PointerPressedEvent, (_, _) => _typing = false,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        // The service is app-lifetime; don't let it keep a closed dialog alive.
        service.Changed += OnServiceChanged;
        box.DetachedFromVisualTree += (_, _) => service.Changed -= OnServiceChanged;
        service.EnsureLoaded();
        Recheck();
    }

    private void OnServiceChanged() => Dispatcher.UIThread.Post(Recheck);

    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_overlay?.GetVisualParent() is Panel oldPanel) oldPanel.Children.Remove(_overlay);
        _overlay = null;

        _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        if (_presenter?.GetVisualParent() is not Panel panel) return;

        _overlay = new SquiggleOverlay(this);
        panel.Children.Add(_overlay);
        // Re-wrapping (a resize or font-scale change) moves words without changing the text.
        _presenter.PropertyChanged += (_, ev) =>
        {
            if (ev.Property == Visual.BoundsProperty) _overlay?.InvalidateVisual();
        };
    }

    private void Recheck()
    {
        _all = _service.Check(_box.Text);
        UpdateVisible();
    }

    private void UpdateVisible()
    {
        var caret = _box.CaretIndex;
        _visible = _typing && _box.IsFocused
            ? _all.Where(m => caret < m.Start || caret > m.End).ToList()
            : _all;
        // Warm suggestions for settled words only — prefetching every prefix of the word being
        // typed would queue a Hunspell search per keystroke.
        _service.PrefetchSuggestions(_visible.Select(m => m.Word));
        _overlay?.InvalidateVisual();
        Changed?.Invoke();
    }

    private Misspelling? FindAt(int index)
    {
        foreach (var m in _all)
            if (m.Start <= index && index <= m.End) return m;
        return null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F7 when e.KeyModifiers == KeyModifiers.None:
                ReviewNext();
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                _typing = false;
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Tunnels ahead of the flyout's own (bubbling) handler, so the target word is known
        // by the time Opening fires.
        if (_presenter is not null && e.TryGetPosition(_presenter, out var point))
        {
            _menuIndex = _presenter.TextLayout.HitTestPoint(point).TextPosition;
            PlaceMenuAt(null); // pointer-triggered: the flyout opens at the pointer
        }
        else
        {
            _menuIndex = _box.CaretIndex;
            PlaceMenuAt(FindAt(_menuIndex));
        }
    }

    /// <summary>F7: select the next misspelling after the caret (wrapping) and open its menu.</summary>
    private void ReviewNext()
    {
        if (_all.Count == 0) return;
        var from = Math.Max(_box.SelectionStart, _box.SelectionEnd);
        var next = _all.FirstOrDefault(m => m.Start >= from, _all[0]);

        _typing = false;
        _box.SelectionStart = next.Start;
        _box.SelectionEnd = next.End;
        _menuIndex = next.Start;
        // Let the ScrollViewer bring the selection into view before measuring where the word is.
        Dispatcher.UIThread.Post(() =>
        {
            PlaceMenuAt(next);
            _menu.ShowAt(_box);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Anchors the menu just below <paramref name="word"/>, or restores default placement.</summary>
    private void PlaceMenuAt(Misspelling? word)
    {
        _menu.Placement = _defaultPlacement;
        _menu.HorizontalOffset = 0;
        _menu.VerticalOffset = 0;
        if (word is not { } m || _presenter is null) return;

        var rects = _presenter.TextLayout.HitTestTextRange(m.Start, m.Length).ToList();
        if (rects.Count == 0 || _presenter.TranslatePoint(rects[0].BottomLeft, _box) is not { } at) return;
        _menu.Placement = PlacementMode.AnchorAndGravity;
        _menu.PlacementAnchor = PopupAnchor.TopLeft;
        _menu.PlacementGravity = PopupGravity.BottomRight;
        _menu.HorizontalOffset = at.X;
        _menu.VerticalOffset = at.Y;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        if (FindAt(_menuIndex) is { } m)
        {
            var suggestions = _service.Suggest(m.Word);
            if (suggestions.Count == 0)
                _menu.Items.Add(new MenuItem { Header = "No spelling suggestions", IsEnabled = false });
            foreach (var suggestion in suggestions)
                AddItem(suggestion, true, () => Replace(m, suggestion)).FontWeight = FontWeight.SemiBold;

            _menu.Items.Add(new Separator());
            AddItem($"Add “{m.Word}” to Dictionary", true,
                () => _service.AddToPersonalDictionary(m.Word));
            AddItem($"Ignore “{m.Word}”", true, () => _service.Ignore(m.Word));
            _menu.Items.Add(new Separator());
        }

        AddItem("Cut", _box.CanCut, _box.Cut, TextBox.CutGesture);
        AddItem("Copy", _box.CanCopy, _box.Copy, TextBox.CopyGesture);
        AddItem("Paste", _box.CanPaste, _box.Paste, TextBox.PasteGesture);
        AddItem("Select All", !string.IsNullOrEmpty(_box.Text), _box.SelectAll);
    }

    private MenuItem AddItem(string header, bool enabled, Action action, KeyGesture? gesture = null)
    {
        // Suggestions are dictionary words, but escape '_' anyway so it can't become an access key.
        var item = new MenuItem { Header = header.Replace("_", "__"), IsEnabled = enabled, InputGesture = gesture };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return item;
    }

    private void Replace(Misspelling m, string replacement)
    {
        var text = _box.Text ?? "";
        if (m.End > text.Length || string.CompareOrdinal(text, m.Start, m.Word, 0, m.Length) != 0) return;
        _box.SelectionStart = m.Start;
        _box.SelectionEnd = m.End;
        _box.SelectedText = replacement; // goes through text input, so Ctrl+Z undoes it
        _box.Focus();
    }

    /// <summary>Draws the wavy underlines, in the text presenter's coordinate space.</summary>
    private sealed class SquiggleOverlay : Control
    {
        private static readonly IBrush s_lightBrush = new ImmutableSolidColorBrush(Color.Parse("#D32F2F"));
        private static readonly IBrush s_darkBrush = new ImmutableSolidColorBrush(Color.Parse("#FF7373"));
        private readonly TextBoxSpellCheck _owner;

        public SquiggleOverlay(TextBoxSpellCheck owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            var presenter = _owner._presenter;
            var words = _owner._visible;
            if (presenter is null || words.Count == 0) return;
            // IME composition text is spliced into the layout, shifting every offset after it.
            if (!string.IsNullOrEmpty(presenter.PreeditText)) return;
            if (presenter.TranslatePoint(default, this) is not { } origin) return;

            var layout = presenter.TextLayout;
            var textLength = presenter.Text?.Length ?? 0;
            var fontSize = _owner._box.FontSize;
            var amplitude = Math.Max(1.5, fontSize / 10);
            var brush = _owner._box.ActualThemeVariant == ThemeVariant.Dark ? s_darkBrush : s_lightBrush;
            var pen = new Pen(brush, Math.Max(1.0, fontSize / 12), lineJoin: PenLineJoin.Round);

            foreach (var m in words)
            {
                if (m.End > textLength) continue;
                foreach (var rect in layout.HitTestTextRange(m.Start, m.Length))
                {
                    var r = rect.Translate(origin);
                    DrawWave(context, pen, r.Left, r.Right, r.Bottom - amplitude - pen.Thickness / 2, amplitude);
                }
            }
        }

        private static void DrawWave(DrawingContext context, IPen pen, double left, double right,
            double midline, double amplitude)
        {
            if (right - left < 1) return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var halfWave = amplitude * 2;
                var x = left;
                var up = true;
                g.BeginFigure(new Point(x, midline + amplitude), isFilled: false);
                while (x < right)
                {
                    x = Math.Min(x + halfWave, right);
                    g.LineTo(new Point(x, up ? midline - amplitude : midline + amplitude));
                    up = !up;
                }
                g.EndFigure(isClosed: false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
