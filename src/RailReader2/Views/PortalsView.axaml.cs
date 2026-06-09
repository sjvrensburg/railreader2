using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Portals pane — the in-window half of the linked-context feature. Lists this document's portals
/// (editable label, "Go to source", "Delete") and shows the active target crop bound to
/// <see cref="MainWindowViewModel.ActivePortalImage"/>. The image is push-driven by the VM's sync
/// loop (so it stays correct even when this pane is collapsed and when the view is popped out); only
/// the row list is rebuilt here, on portal mutations and tab switch. Self-contained: wires its
/// subscriptions on load and tears them down on unload, like the other accordion panes.
/// </summary>
public partial class PortalsView : PaneRefreshView
{
    private MainWindowViewModel? _vm;
    private TabViewModel? _watchedTab;
    // Set while a rename is in flight so the resulting PortalsChanged doesn't rebuild the ItemsSource
    // (and destroy the TextBox) underneath the LostFocus/KeyDown event that triggered it.
    private bool _suppressRowRebuild;

    public PortalsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Attach(DataContext as MainWindowViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        Detach();
        base.OnUnloaded(e);
    }

    private void Attach(MainWindowViewModel? vm)
    {
        Detach();
        _vm = vm;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.PortalsChanged += OnPortalsChanged;
        _watchedTab = _vm.ActiveTab;
        RefreshIfVisible();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.PortalsChanged -= OnPortalsChanged;
        _vm = null;
        _watchedTab = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        // ActiveTab is raised synthetically on every navigation; the row list depends only on the
        // document, so rebuild only on a real tab switch. The bound target image clears itself via
        // the VM's owner-check, but nudge a fresh evaluation so the swap is prompt.
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab) && _vm?.ActiveTab != _watchedTab)
        {
            _watchedTab = _vm?.ActiveTab;
            _vm?.EvaluatePortals(forceRender: true);
            RefreshIfVisible();
        }
    }

    private void OnPortalsChanged()
    {
        if (_suppressRowRebuild) return;
        RefreshIfVisible();
    }

    protected override void Refresh()
    {
        if (_vm is null)
        {
            PortalList.ItemsSource = null;
            NoPortalsLabel.IsVisible = false;
            return;
        }
        var rows = _vm.BuildPortalRows();
        PortalList.ItemsSource = rows;
        NoPortalsLabel.IsVisible = rows.Count == 0;
    }

    // --- Row actions ---

    private static string? RowId(object? sender)
        => (sender as Control)?.DataContext is PortalRowViewModel row ? row.Portal.Id : null;

    private void OnGoToSourceClick(object? sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id) _vm?.GoToPortalSource(id);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id) _vm?.DeletePortal(id);
    }

    private void OnLabelLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PortalRowViewModel row } tb)
            Rename(row.Portal.Id, tb.Text);
    }

    private void OnLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { DataContext: PortalRowViewModel row } tb)
        {
            Rename(row.Portal.Id, tb.Text);
            e.Handled = true;
        }
    }

    private void Rename(string id, string? label)
    {
        // Suppress the row rebuild the rename triggers — it would replace this very TextBox mid-event.
        _suppressRowRebuild = true;
        try { _vm?.RenamePortal(id, label ?? ""); }
        finally { _suppressRowRebuild = false; }
    }

    // --- Pop-out / dock ---

    private void OnPopOutClick(object? sender, RoutedEventArgs e) => _vm?.PopOutPortal();
    private void OnDockClick(object? sender, RoutedEventArgs e) => _vm?.DockPortal();
}
