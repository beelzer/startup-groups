using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StartupGroups.App.Resources;
using StartupGroups.App.Services;
using StartupGroups.App.Views;
using StartupGroups.Core.WindowsStartup;

namespace StartupGroups.App.ViewModels;

public partial class WindowsStartupViewModel : ObservableObject
{
    private readonly IWindowsStartupService _service;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WindowsStartupViewModel> _logger;
    private bool _isUpdating;

    public WindowsStartupViewModel(
        IWindowsStartupService service,
        IDialogService dialogs,
        IServiceProvider serviceProvider,
        ILogger<WindowsStartupViewModel> logger)
    {
        _service = service;
        _dialogs = dialogs;
        _serviceProvider = serviceProvider;
        _logger = logger;
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        ApplySort();
    }

    public ObservableCollection<WindowsStartupEntryViewModel> Entries { get; } = [];

    public ICollectionView EntriesView { get; }

    public bool HasEntries => Entries.Count > 0;
    public bool IsEmpty => Entries.Count == 0;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _sortColumn = nameof(WindowsStartupEntryViewModel.Name);
    [ObservableProperty] private bool _sortAscending = true;

    public string NameSortIndicator => IndicatorFor(nameof(WindowsStartupEntryViewModel.Name));
    public string CommandSortIndicator => IndicatorFor(nameof(WindowsStartupEntryViewModel.Command));
    public string SourceSortIndicator => IndicatorFor(nameof(WindowsStartupEntryViewModel.Source));
    public string EnabledSortIndicator => IndicatorFor(nameof(WindowsStartupEntryViewModel.Enabled));

    partial void OnSortColumnChanged(string value) => RaiseIndicatorsChanged();
    partial void OnSortAscendingChanged(bool value) => RaiseIndicatorsChanged();

    private void RaiseIndicatorsChanged()
    {
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(CommandSortIndicator));
        OnPropertyChanged(nameof(SourceSortIndicator));
        OnPropertyChanged(nameof(EnabledSortIndicator));
    }

    private string IndicatorFor(string column) =>
        string.Equals(column, SortColumn, StringComparison.Ordinal)
            ? (SortAscending ? " ↑" : " ↓")
            : string.Empty;

    public void Refresh()
    {
        _isUpdating = true;
        try
        {
            foreach (var entry in Entries)
            {
                entry.PropertyChanged -= OnEntryPropertyChanged;
            }

            Entries.Clear();
            foreach (var entry in _service.Enumerate())
            {
                var vm = new WindowsStartupEntryViewModel(entry);
                vm.PropertyChanged += OnEntryPropertyChanged;
                Entries.Add(vm);
            }

            StatusMessage = string.Format(CultureInfo.CurrentUICulture, Strings.WinStartup_CountFormat, Entries.Count);
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(IsEmpty));
            ApplySort();
            WindowsStartupIconLoader.LoadFor(Entries);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        if (string.Equals(column, SortColumn, StringComparison.Ordinal))
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }

        ApplySort();
    }

    private void ApplySort()
    {
        EntriesView.SortDescriptions.Clear();
        EntriesView.SortDescriptions.Add(new SortDescription(
            SortColumn,
            SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));
        EntriesView.Refresh();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdating || sender is not WindowsStartupEntryViewModel vm)
        {
            return;
        }

        if (e.PropertyName != nameof(WindowsStartupEntryViewModel.Enabled))
        {
            return;
        }

        var result = _service.TrySetEnabled(vm.Model, vm.Enabled);
        if (result.Succeeded)
        {
            StatusMessage = string.Format(CultureInfo.CurrentUICulture, Strings.Status_AppResultFormat, vm.Name, result.Message);
            return;
        }

        _logger.LogWarning("Failed to toggle {Name}: {Message}", vm.Name, result.Message);
        _ = _dialogs.ShowErrorAsync(
            Strings.Dialog_Startup_Title,
            string.Format(CultureInfo.CurrentUICulture, Strings.WinStartup_ToggleFailedFormat, vm.Name, result.Message));
        _isUpdating = true;
        vm.Enabled = !vm.Enabled;
        _isUpdating = false;
    }

    [RelayCommand]
    private void ReloadList()
    {
        Refresh();
    }

    [RelayCommand]
    private async Task AddEntryAsync()
    {
        var path = _dialogs.PromptForExecutable();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var quoted = path.Contains(' ') ? $"\"{path}\"" : path;

        var result = _service.TryAddUserRunEntry(name, quoted);
        if (!result.Succeeded)
        {
            await _dialogs.ShowErrorAsync(
                Strings.Dialog_Startup_Title,
                string.Format(CultureInfo.CurrentUICulture, Strings.WinStartup_AddFailedFormat, result.Message));
            return;
        }

        StatusMessage = string.Format(CultureInfo.CurrentUICulture, Strings.WinStartup_AddedFormat, name);
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveEntryAsync(WindowsStartupEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (!entry.CanModify)
        {
            await _dialogs.ShowErrorAsync(
                Strings.Dialog_Startup_Title,
                Strings.WinStartup_RequiresAdmin);
            return;
        }

        if (!await _dialogs.ConfirmAsync(
            Strings.Dialog_RemoveStartupItem_Title,
            string.Format(CultureInfo.CurrentUICulture, Strings.Dialog_RemoveStartupItem_MessageFormat, entry.Name)))
        {
            return;
        }

        var result = _service.TryRemove(entry.Model);
        if (!result.Succeeded)
        {
            await _dialogs.ShowErrorAsync(
                Strings.Dialog_Startup_Title,
                string.Format(CultureInfo.CurrentUICulture, Strings.WinStartup_RemoveFailedFormat, result.Message));
            return;
        }

        StatusMessage = string.Format(CultureInfo.CurrentUICulture, Strings.WinStartup_RemovedFormat, entry.Name);
        Refresh();
    }

    [RelayCommand]
    private void OpenLocation(WindowsStartupEntryViewModel? entry)
    {
        if (entry is null) return;

        switch (entry.Model.Source)
        {
            case StartupEntrySource.RegistryRunUser:
            case StartupEntrySource.RegistryRunUser32:
            case StartupEntrySource.RegistryRunMachine:
            case StartupEntrySource.RegistryRunMachine32:
                OpenRegistryEditor(entry);
                break;

            case StartupEntrySource.StartupFolderUser:
            case StartupEntrySource.StartupFolderCommon:
                OpenStartupFolderTarget(entry);
                break;
        }
    }

    private void OpenRegistryEditor(WindowsStartupEntryViewModel entry)
    {
        try
        {
            var editor = _serviceProvider.GetRequiredService<RegistryRunValueEditorViewModel>();
            editor.LoadFrom(entry.Model);

            var window = new RegistryRunValueEditorWindow(editor);
            var result = window.ShowDialog();
            if (result == true)
            {
                Refresh();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open registry editor for {Name}", entry.Name);
        }
    }

    private void OpenStartupFolderTarget(WindowsStartupEntryViewModel entry)
    {
        if (string.IsNullOrEmpty(entry.SourceDescription)) return;

        try
        {
            if (System.IO.File.Exists(entry.SourceDescription))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{entry.SourceDescription}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open folder for {Name}", entry.Name);
        }
    }
}
