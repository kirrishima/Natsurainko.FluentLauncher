using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Natsurainko.FluentLauncher.Services.UI;
using Natsurainko.FluentLauncher.ViewModels.Downloads;

namespace Natsurainko.FluentLauncher.Views.Downloads;

public sealed partial class ResourceDefaultPage : Page
{
    ResourceDefaultViewModel VM => (ResourceDefaultViewModel)DataContext;
    private ScrollViewer? _scrollViewer;
    private ResourceDefaultViewModel? _subscribedModel;
    private int _restoreVersion;
    private bool _restorePending;
    private System.EventHandler<object>? _pendingLayoutRestore;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.GetService<CatalogNavigationState>().BeginCatalogNavigation(e.NavigationMode is NavigationMode.Back or NavigationMode.Forward);
    }

    public ResourceDefaultPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _subscribedModel = VM;
            _subscribedModel.PageChanged += RestoreScroll;
            _scrollViewer = FindScrollViewer(ListView);
            if (_scrollViewer != null) _scrollViewer.ViewChanged += OnScrollChanged;
        };
        Unloaded += (_, _) =>
        {
            CancelScrollRestore();
            if (_subscribedModel != null) _subscribedModel.PageChanged -= RestoreScroll;
            _subscribedModel = null;
            if (_scrollViewer != null) _scrollViewer.ViewChanged -= OnScrollChanged;
            _scrollViewer = null;
        };
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => CancelScrollRestore()), true);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => CancelScrollRestore()), true);
        AddHandler(KeyDownEvent, new KeyEventHandler((_, _) => CancelScrollRestore()), true);
    }

    void OnScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!_restorePending && _subscribedModel is { Loading: false } model && _scrollViewer != null)
            model.ScrollOffset = _scrollViewer.VerticalOffset;
    }

    void RestoreScroll()
    {
        CancelScrollRestore();
        var model = VM;
        double offset = model.ScrollOffset;
        int version = _restoreVersion;
        _restorePending = true;

        // Wait for the normal layout pass; never force synchronous realization of the list.
        void ApplyRestore()
        {
            if (!_restorePending || version != _restoreVersion) return;
            CancelScrollRestore();
            if (!IsLoaded || !ReferenceEquals(DataContext, model)) return;
            _scrollViewer ??= FindScrollViewer(ListView);
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnScrollChanged;
                _scrollViewer.ViewChanged += OnScrollChanged;
                _scrollViewer.ChangeView(null, offset, null, true);
            }
        }

        _pendingLayoutRestore = (_, _) => ApplyRestore();
        ListView.LayoutUpdated += _pendingLayoutRestore;
        // A cached page can already be laid out, so no further layout event is guaranteed.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyRestore);
    }

    void CancelScrollRestore()
    {
        _restoreVersion++;
        _restorePending = false;
        if (_pendingLayoutRestore != null) ListView.LayoutUpdated -= _pendingLayoutRestore;
        _pendingLayoutRestore = null;
    }

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
