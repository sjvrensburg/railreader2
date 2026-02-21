using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class SearchBarView : UserControl
{
    private DispatcherTimer? _debounceTimer;

    public SearchBarView()
    {
        InitializeComponent();

        SearchInput.TextChanged += OnSearchTextChanged;
        SearchInput.KeyDown += OnSearchKeyDown;
        NextButton.Click += OnNextClick;
        PrevButton.Click += OnPrevClick;
        CloseButton.Click += OnCloseClick;
        CaseSensitiveToggle.IsCheckedChanged += OnOptionChanged;
        RegexToggle.IsCheckedChanged += OnOptionChanged;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public bool IsSearchInputFocused => SearchInput.IsFocused;

    public void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounceTimer?.Stop();
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RunSearch();
        };
        _debounceTimer.Start();
    }

    private void OnOptionChanged(object? sender, RoutedEventArgs e)
    {
        RunSearch();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Vm?.PreviousMatch();
                UpdateMatchDisplay();
                e.Handled = true;
                break;
            case Key.Enter:
                Vm?.NextMatch();
                UpdateMatchDisplay();
                e.Handled = true;
                break;
            case Key.Escape:
                Vm?.CloseSearch();
                e.Handled = true;
                break;
        }
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        Vm?.NextMatch();
        UpdateMatchDisplay();
    }

    private void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        Vm?.PreviousMatch();
        UpdateMatchDisplay();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Vm?.CloseSearch();
    }

    private void RunSearch()
    {
        if (Vm is null) return;
        bool caseSensitive = CaseSensitiveToggle.IsChecked == true;
        bool useRegex = RegexToggle.IsChecked == true;
        Vm.ExecuteSearch(SearchInput.Text ?? "", caseSensitive, useRegex);
        UpdateMatchDisplay();
    }

    private void UpdateMatchDisplay()
    {
        if (Vm is null) return;
        int total = Vm.SearchMatches.Count;
        int current = total > 0 ? Vm.ActiveMatchIndex + 1 : 0;
        MatchCount.Text = $"{current} of {total}";
    }
}
