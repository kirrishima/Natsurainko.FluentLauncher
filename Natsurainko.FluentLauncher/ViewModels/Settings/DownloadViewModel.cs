using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentLauncher.Infra.Settings.Mvvm;
using Natsurainko.FluentLauncher.Services.Settings;
using Natsurainko.FluentLauncher.Services.Storage;
using Natsurainko.FluentLauncher.Services.Network;
using Natsurainko.FluentLauncher.Services.UI.Notification;
using FluentLauncher.Infra.UI.Notification;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using Natsurainko.FluentLauncher.Utils;

#nullable disable
namespace Natsurainko.FluentLauncher.ViewModels.Settings;

internal partial class DownloadViewModel : SettingsPageVM, ISettingsViewModel
{
    [SettingsProvider]
    private readonly SettingsService _settingsService;
    private readonly LocalStorageService _localStorageService;

    [ObservableProperty]
    [BindToSetting(Path = nameof(SettingsService.MaxDownloadThreads))]
    public partial int MaxDownloadThreads { get; set; }

    [ObservableProperty]
    [BindToSetting(Path = nameof(SettingsService.EnableFragmentDownload))]
    public partial bool EnableFragmentDownload { get; set; }

    [ObservableProperty]
    [BindToSetting(Path = nameof(SettingsService.FragmentDownloadWorkerCount))]
    public partial int FragmentDownloadWorkerCount { get; set; }

    [ObservableProperty]
    [BindToSetting(Path = nameof(SettingsService.MaxRetryCount))]
    public partial int MaxRetryCount { get; set; }

    [ObservableProperty]
    [BindToSetting(Path = nameof(SettingsService.EnableCatalogCache))]
    public partial bool EnableCatalogCache { get; set; }

    [ObservableProperty]
    [BindToSetting(Path = nameof(SettingsService.CatalogCacheLifetimeMinutes))]
    public partial int CatalogCacheLifetimeMinutes { get; set; }

    [ObservableProperty]
    [BindToSetting(Path = nameof(SettingsService.CatalogCacheMaxSizeMb))]
    public partial int CatalogCacheMaxSizeMb { get; set; }

    partial void OnEnableCatalogCacheChanged(bool value) => ScheduleCacheTrim();
    partial void OnCatalogCacheLifetimeMinutesChanged(int value) => ScheduleCacheTrim();
    partial void OnCatalogCacheMaxSizeMbChanged(int value) => ScheduleCacheTrim();

    // Settings bindings update the provider in PropertyChanged; defer until that has completed.
    void ScheduleCacheTrim() => App.DispatcherQueue.TryEnqueue(() => _ = App.GetService<CatalogPageCache>().TrimAsync());

    [RelayCommand]
    async Task ClearCatalogCache()
    {
        try
        {
            await App.GetService<CatalogPageCache>().ClearAsync();
            App.GetService<INotificationService>().Show<InfoBar>(new DefaultNotification
            {
                Title = LocalizedStrings.Catalog__CacheCleared,
                Type = NotificationType.Success
            });
        }
        catch (Exception ex)
        {
            App.GetService<ILogger<DownloadViewModel>>().LogError(ex, "Could not clear catalog cache");
            App.GetService<INotificationService>().Show<InfoBar>(new DefaultNotification
            {
                Title = LocalizedStrings.Catalog__CacheClearFailed,
                Type = NotificationType.Error
            });
        }
    }

    [ObservableProperty]
    public partial int CurrentDownloadSource { get; set; }

    partial void OnCurrentDownloadSourceChanged(int value)
    {
        _settingsService.CurrentDownloadSource = CurrentDownloadSource switch
        {
            1 => "Bmclapi",
            _ => "Official"
        };
    }

    public DownloadViewModel(SettingsService settingsService, LocalStorageService localStorageService)
    {
        _settingsService = settingsService;
        _localStorageService = localStorageService;

        (this as ISettingsViewModel).InitializeSettings();

        CurrentDownloadSource = settingsService.CurrentDownloadSource switch
        {
            "Bmclapi" => 1,
            _ => 0
        };
    }
}
