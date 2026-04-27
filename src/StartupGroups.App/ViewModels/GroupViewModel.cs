using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupGroups.App.Resources;
using StartupGroups.Core.Models;

namespace StartupGroups.App.ViewModels;

public partial class GroupViewModel : ObservableObject
{
    public GroupViewModel()
    {
        Apps.CollectionChanged += OnAppsChanged;
    }

    public string AppsCountText =>
        string.Format(CultureInfo.CurrentUICulture, Strings.Apps_CountFormat, Apps.Count);

    public bool HasApps => Apps.Count > 0;
    public bool IsEmpty => Apps.Count == 0;

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _icon = "Apps24";

    public ObservableCollection<AppEntryViewModel> Apps { get; } = [];

    public bool AllRunning =>
        Apps.Any(a => a.Enabled) && Apps.Where(a => a.Enabled).All(a => a.IsRunning);

    public bool AnyRunning => Apps.Any(a => a.IsRunning);

    public bool CanLaunchAll => Apps.Any(a => a.Enabled && !a.IsRunning);

    public bool CanStopAll => AnyRunning;

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (AppEntryViewModel app in e.OldItems)
            {
                app.PropertyChanged -= OnAppPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (AppEntryViewModel app in e.NewItems)
            {
                app.PropertyChanged += OnAppPropertyChanged;
            }
        }
        RaiseRunningStateChanged();
        OnPropertyChanged(nameof(AppsCountText));
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppEntryViewModel.IsRunning) or nameof(AppEntryViewModel.Enabled))
        {
            RaiseRunningStateChanged();
        }
    }

    private void RaiseRunningStateChanged()
    {
        OnPropertyChanged(nameof(AllRunning));
        OnPropertyChanged(nameof(AnyRunning));
        OnPropertyChanged(nameof(CanLaunchAll));
        OnPropertyChanged(nameof(CanStopAll));
    }

    public Group ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Icon = Icon,
        Apps = Apps.Select(a => a.ToModel()).ToList()
    };

    public static GroupViewModel FromModel(Group group)
    {
        var vm = new GroupViewModel
        {
            Id = group.Id,
            Name = group.Name,
            Icon = string.IsNullOrWhiteSpace(group.Icon) ? "Apps24" : group.Icon
        };
        foreach (var app in group.Apps)
        {
            vm.Apps.Add(AppEntryViewModel.FromModel(app));
        }
        return vm;
    }
}
