using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StartupGroups.App.Resources;
using StartupGroups.App.Services;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.App.ViewModels;

public enum PickerAction
{
    Cancel,
    AddSelected,
    EditSelected,
    AddBlank
}

public partial class AddAppPickerViewModel : ObservableObject
{
    private readonly IInstalledAppsProvider _provider;
    private readonly ILogger<AddAppPickerViewModel> _logger;
    private CancellationTokenSource? _loadCts;

    public AddAppPickerViewModel(
        IInstalledAppsProvider provider,
        ILogger<AddAppPickerViewModel> logger)
    {
        _provider = provider;
        _logger = logger;

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;
        AppsView.SortDescriptions.Add(new SortDescription(nameof(InstalledAppViewModel.Name), ListSortDirection.Ascending));
    }

    public ObservableCollection<InstalledAppViewModel> Apps { get; } = [];

    public ICollectionView AppsView { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private InstalledAppViewModel? _highlightedApp;

    partial void OnHighlightedAppChanged(InstalledAppViewModel? value)
    {
        EditSelectedCommand.NotifyCanExecuteChanged();
    }

    public PickerAction Result { get; private set; } = PickerAction.Cancel;

    public int CheckedCount => Apps.Count(a => a.IsChecked);

    public string CheckedCountText =>
        string.Format(CultureInfo.CurrentUICulture, Strings.AddAppPicker_SelectedFormat, CheckedCount);

    public IReadOnlyList<InstalledApp> GetCheckedModels() =>
        Apps.Where(a => a.IsChecked).Select(a => a.Model).ToList();

    public InstalledApp? GetHighlightedModel() => HighlightedApp?.Model;

    partial void OnSearchTextChanged(string value)
    {
        AppsView.Refresh();
    }

    private bool FilterApp(object obj)
    {
        if (obj is not InstalledAppViewModel vm) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var query = SearchText.Trim();
        return vm.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (vm.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        IsLoading = true;
        StatusMessage = "Scanning installed apps...";
        Apps.Clear();

        try
        {
            var models = await _provider.EnumerateAsync(token).ConfigureAwait(true);

            var newItems = new List<InstalledAppViewModel>(models.Count);
            foreach (var model in models)
            {
                token.ThrowIfCancellationRequested();
                var vm = new InstalledAppViewModel(model);
                vm.PropertyChanged += OnItemPropertyChanged;
                Apps.Add(vm);
                newItems.Add(vm);
            }

            StatusMessage = $"Found {Apps.Count} installed apps";
            StartIconLoading(newItems, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate installed apps");
            StatusMessage = "Failed to list installed apps.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void StartIconLoading(IReadOnlyList<InstalledAppViewModel> items, CancellationToken token)
    {
        if (items.Count == 0) return;

        var dispatcher = Dispatcher.CurrentDispatcher;
        var thread = new Thread(() =>
        {
            foreach (var vm in items)
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    var source = !string.IsNullOrWhiteSpace(vm.Model.IconPath)
                        ? vm.Model.IconPath!
                        : vm.Model.ParsingName;
                    var icon = ShellIconExtractor.GetImage(source, 32);
                    if (icon is not null)
                    {
                        dispatcher.BeginInvoke(() => vm.Icon = icon, DispatcherPriority.Background);
                    }
                }
                catch
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "ShellIcon-STA",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledAppViewModel.IsChecked))
        {
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(CheckedCountText));
            AddSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddSelected))]
    private void AddSelected()
    {
        Result = PickerAction.AddSelected;
        RequestClose?.Invoke(this, true);
    }

    private bool CanAddSelected() => CheckedCount > 0;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void EditSelected()
    {
        Result = PickerAction.EditSelected;
        RequestClose?.Invoke(this, true);
    }

    private bool CanEditSelected() => HighlightedApp is not null;

    [RelayCommand]
    private void AddBlank()
    {
        Result = PickerAction.AddBlank;
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = PickerAction.Cancel;
        RequestClose?.Invoke(this, false);
    }

    public event EventHandler<bool>? RequestClose;
}
