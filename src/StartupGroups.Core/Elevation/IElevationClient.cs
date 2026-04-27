namespace StartupGroups.Core.Elevation;

public interface IElevationClient
{
    bool IsElevated { get; }

    Task<bool> InvokeAsync(ElevationRequest request, CancellationToken cancellationToken = default);
}
