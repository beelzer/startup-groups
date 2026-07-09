using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Salvo.App.Resources;
using Salvo.App.ViewModels.Flow;
using Salvo.Core.Models;

namespace Salvo.App.ViewModels;

/// <summary>
/// Group view-model. After the Phase A2 cutover, the authoritative
/// editing surface is <see cref="Graph"/>; the legacy <c>Apps</c>
/// observable mirrors AppNode entries so the old running-state
/// aggregates still light up unchanged.
/// </summary>
public partial class GroupViewModel : ObservableObject
{
    public GroupViewModel()
    {
        Graph.Nodes.CollectionChanged += OnGraphNodesChanged;
    }

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _icon = "Apps24";

    /// <summary>
    /// Flow graph of the group. Owned by this VM; views bind into
    /// Graph.Stages for the Simple view and Graph.Nodes/Edges for the
    /// Flow view.
    /// </summary>
    public GroupGraphViewModel Graph { get; } = new();

    /// <summary>
    /// Flat list of every AppNode's embedded <see cref="AppEntryViewModel"/>.
    /// Maintained for the running-state aggregates that the rest of the
    /// shell consumes (tray menu, group-level run indicators).
    /// </summary>
    public IReadOnlyList<AppEntryViewModel> Apps =>
        Graph.Nodes.OfType<AppNodeViewModel>().Select(n => n.App).ToList();

    public string AppsCountText =>
        string.Format(CultureInfo.CurrentUICulture, Strings.Apps_CountFormat, Apps.Count);

    public bool HasApps => Graph.Nodes.OfType<AppNodeViewModel>().Any();
    public bool IsEmpty => !HasApps;

    public bool AllRunning =>
        Apps.Any(a => a.Enabled) && Apps.Where(a => a.Enabled).All(a => a.IsRunning);

    public bool AnyRunning => Apps.Any(a => a.IsRunning);

    public bool CanLaunchAll => Apps.Any(a => a.Enabled && !a.IsRunning);

    public bool CanStopAll => AnyRunning;

    private void OnGraphNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Subscribe / unsubscribe to AppNodes so their embedded
        // AppEntry running-state changes bubble into the group's
        // aggregates.
        if (e.OldItems is not null)
        {
            foreach (NodeViewModel n in e.OldItems)
            {
                if (n is AppNodeViewModel app) app.App.PropertyChanged -= OnAppPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (NodeViewModel n in e.NewItems)
            {
                if (n is AppNodeViewModel app) app.App.PropertyChanged += OnAppPropertyChanged;
            }
        }
        RaiseRunningStateChanged();
        OnPropertyChanged(nameof(Apps));
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

    public Group ToModel()
    {
        var group = new Group
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
        };
        Graph.WriteTo(group);
        return group;
    }

    public static GroupViewModel FromModel(Group group)
    {
        // Apps[] → graph migration (and Start-seeding for empty groups)
        // is handled by GroupGraphViewModel.FromModel; do it on a temp
        // and then transfer into our owned Graph so this VM's change
        // subscriptions catch the AppNode entries.
        if (group.Nodes.Count == 0 && group.Apps.Count > 0)
        {
            Salvo.Core.Models.Flow.FlowMigration.Migrate(group);
        }
        if (group.Nodes.Count == 0)
        {
            group.Nodes.Add(new Salvo.Core.Models.Flow.StartNode { Id = Guid.NewGuid().ToString() });
        }

        var vm = new GroupViewModel
        {
            Id = group.Id,
            Name = group.Name,
            Icon = string.IsNullOrWhiteSpace(group.Icon) ? "Apps24" : group.Icon,
        };
        foreach (var n in group.Nodes) vm.Graph.Nodes.Add(NodeViewModel.FromModel(n));
        foreach (var e in group.Edges) vm.Graph.Edges.Add(EdgeViewModel.FromModel(e));
        // Lift If then/else edge chains off the flat graph onto the If
        // view-models (inverse of Graph.WriteTo). Must run here too — this
        // load path builds Graph directly instead of via
        // GroupGraphViewModel.FromModel.
        vm.Graph.CollapseBranches();
        vm.Graph.RebuildStages();
        return vm;
    }
}
