using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Natsurainko.FluentLauncher.ViewModels.Downloads;

namespace Natsurainko.FluentLauncher.Views.Downloads;

public sealed partial class ResourceDefaultPage : Page
{
    ResourceDefaultViewModel VM => (ResourceDefaultViewModel)DataContext;

    public ResourceDefaultPage()
    {
        InitializeComponent();
        Loaded += (_, _) => VM.PageChanged += ScrollToTop;
        Unloaded += (_, _) => VM.PageChanged -= ScrollToTop;
    }

    void ScrollToTop() => DispatcherQueue.TryEnqueue(() => FindScrollViewer(ListView)?.ChangeView(null, 0, null, true));

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
