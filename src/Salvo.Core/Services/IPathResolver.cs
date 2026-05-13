namespace Salvo.Core.Services;

public interface IPathResolver
{
    string? Resolve(string? rawPath);
}
