using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Natsurainko.FluentLauncher.Services.UI;
using Natsurainko.FluentLauncher.ViewModels.Downloads;

namespace Natsurainko.FluentLauncher.Views.Downloads;

public sealed partial class ResourceDefaultPage : Page
{
    ResourceDefaultViewModel VM => (ResourceDefaultViewModel)DataContext;
    private ScrollViewer? _scrollViewer;

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
            VM.PageChanged += RestoreScroll;
            _scrollViewer = FindScrollViewer(ListView);
            if (_scrollViewer != null) _scrollViewer.ViewChanged += OnScrollChanged;
        };
        Unloaded += (_, _) =>
        {
            VM.PageChanged -= RestoreScroll;
            if (_scrollViewer != null) _scrollViewer.ViewChanged -= OnScrollChanged;
            _scrollViewer = null;
        };
    }

    void OnScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!VM.Loading && _scrollViewer != null) VM.ScrollOffset = _scrollViewer.VerticalOffset;
    }

    void RestoreScroll()
    {
        double offset = VM.ScrollOffset;
        DispatcherQueue.TryEnqueue(() =>
        {
            ListView.UpdateLayout();
            _scrollViewer ??= FindScrollViewer(ListView);
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnScrollChanged;
                _scrollViewer.ViewChanged += OnScrollChanged;
                _scrollViewer.ChangeView(null, offset, null, true);
            }
        });
    }

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
