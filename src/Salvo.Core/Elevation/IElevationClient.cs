namespace Salvo.Core.Elevation;

public interface IElevationClient
{
    Task<bool> InvokeAsync(ElevationRequest request, CancellationToken cancellationToken = default);
}
