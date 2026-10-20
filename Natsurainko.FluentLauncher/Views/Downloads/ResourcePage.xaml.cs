using CommunityToolkit.WinUI.Controls;
using FluentLauncher.Infra.UI.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.Logging;
using Natsurainko.FluentLauncher.Utils;
using Natsurainko.FluentLauncher.ViewModels.Downloads;
using System;

namespace Natsurainko.FluentLauncher.Views.Downloads;

internal record ResourceAuthor(string Name, string WebLink);

public sealed partial class ResourcePage : Page, IBreadcrumbBarAware
{
    string IBreadcrumbBarAware.Route => "Resource";

    ResourceViewModel VM => (ResourceViewModel)DataContext;

    public MarkdownConfig MarkdownConfig { get; set; } = new MarkdownConfig();
    public static string ContentHeightLabel => LocalizedStrings.Catalog_ContentHeight_Text;

    public ResourcePage()
    {
        this.InitializeComponent();
    }

    private void FilesItemsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args) => VM.SelectedFile = sender.SelectedItem;

    private void FilesItemsView_Unloaded(object sender, RoutedEventArgs e) => VM.SelectedFile = null;

    private void DescriptionMarkdown_Loaded(object sender, RoutedEventArgs e) => DescriptionMarkdown.Text = VM.DescriptionContent;

    private async void DescriptionWebView2_Loaded(object sender, RoutedEventArgs e)
    {
        WebView2 webView2 = DescriptionWebView2;
        string description = VM.DescriptionContent;
        try
        {
            await webView2.EnsureCoreWebView2Async();
            if (!webView2.IsLoaded) return;
            webView2.CoreWebView2.Profile.PreferredColorScheme = webView2.ActualTheme == ElementTheme.Dark
                ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

            string body = $"<meta name=\"color-scheme\" content=\"{(webView2.ActualTheme == ElementTheme.Dark ? "dark light" : "light dark")}\">\r\n"
                + "<style>body{font-family:Segoe UI,sans-serif;margin:12px;}img{width:auto;height:auto;max-width:100%;}</style>\r\n"
                + $"<div>{description}</div>";
            webView2.NavigateToString(body);
        }
        catch (Exception ex)
        {
            App.GetService<ILogger<ResourcePage>>().LogWarning(ex, "Could not initialize the mod description viewer");
        }
    }

    internal static string GetPreferredFolderOptionText(string folder, int index)
    {
        if (folder == null) return string.Empty;

        return index switch
        {
            0 => LocalizedStrings.Downloads_ResourcePage__M2.Replace("${folder}", folder),
            1 => LocalizedStrings.Downloads_ResourcePage__M3.Replace("${folder}", folder),
            _ => throw new NotImplementedException()
        };
    }
}
