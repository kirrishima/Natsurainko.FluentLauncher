// Minimal UI boundary for running the actual catalog view model without Windows/WinUI.
namespace Microsoft.UI.Xaml.Controls { internal sealed class InfoBar { } }
namespace FluentLauncher.Infra.UI.Navigation
{
    internal interface INavigationAware
    {
        void SetNavigationKey(string key);
        void OnNavigatedTo(object? parameter);
        void OnNavigatedFrom();
    }
    internal interface INavigationService { void NavigateTo(string key, object parameter); }
}
namespace FluentLauncher.Infra.UI.Notification
{
    internal enum NotificationType { Info, Error, Success }
    internal interface INotificationService
    {
        void Show<T>(Natsurainko.FluentLauncher.Services.UI.Notification.DefaultNotification notification);
    }
}
namespace Natsurainko.FluentLauncher
{
    internal static class App
    {
        private static readonly Dictionary<Type, object> Services = [];
        public static T GetService<T>() where T : notnull => (T)Services[typeof(T)];
        public static void Set<T>(T value) where T : notnull => Services[typeof(T)] = value;
    }
    internal static class LocalizedStrings
    {
        public const string ResourceCategories__All = "All versions";
        public const string Catalog__Page = "Page {0}";
        public const string Catalog__LastPage = "This is the last page. There are no more results.";
        public const string Catalog__SearchFailed = "Could not load the catalog page";
        public const string Catalog__Retry = "Please try refreshing the page.";
    }
}
namespace Natsurainko.FluentLauncher.Services.UI.Notification
{
    internal sealed class DefaultNotification
    {
        public required string Title { get; init; }
        public string? Message { get; init; }
        public double Delay { get; init; } = 5;
        public global::FluentLauncher.Infra.UI.Notification.NotificationType Type { get; init; }
    }
}
namespace Natsurainko.FluentLauncher.Services.Settings
{
    internal sealed class SettingsService { public bool EnableLocalizedResourceSuggestions { get; set; } }
}
namespace Natsurainko.FluentLauncher.Services.UI
{
    internal sealed class SearchProviderService
    {
        public Action<string>? Submit { get; private set; }
        public BindedSearchProvider BindProvider(object owner) => new(this);
        internal sealed class Suggestion { }
        internal sealed class BindedSearchProvider(SearchProviderService owner) : IDisposable
        {
            public void BindQuerySubmition(Action<string> submit) => owner.Submit = submit;
            public void BindSuggestionsSource(Func<string, IEnumerable<Suggestion>> source) { }
            public void ClearInput() { }
            public void Dispose() => owner.Submit = null;
        }
    }
}
namespace Natsurainko.FluentLauncher.Utils
{
    internal static class SuggestionHelper
    {
        public static IEnumerable<Services.UI.SearchProviderService.Suggestion> GetSearchModSuggestions(
            string query, int source, Action<string> invoke) => [];
    }
}
namespace Natsurainko.FluentLauncher.ViewModels
{
    internal class PageVM : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public bool IsActive { get; set; }
        protected virtual void OnLoaded() { }
    }
}
