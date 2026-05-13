using Salvo.Core.Models;

namespace Salvo.Core.Services;

public interface IConfigStore
{
    string ConfigPath { get; }

    event EventHandler<Configuration>? Changed;

    Configuration Load();

    void Save(Configuration configuration);

    void BeginWatching();

    void StopWatching();
}
