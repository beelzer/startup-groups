using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public interface IConfigStore
{
    string ConfigPath { get; }

    event EventHandler<Configuration>? Changed;

    Configuration Load();

    void Save(Configuration configuration);

    void BeginWatching();

    void StopWatching();
}
