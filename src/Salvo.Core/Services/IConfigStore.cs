using Salvo.Core.Models;

namespace Salvo.Core.Services;

public interface IConfigStore
{
    string ConfigPath { get; }

    /// <summary>
    /// True when the most recent <see cref="Load"/> could not parse the
    /// config file (the corrupt file has been quarantined alongside the
    /// original). Cleared by the next successful load or save. Lets the
    /// App surface "your config could not be read" instead of silently
    /// presenting an empty state.
    /// </summary>
    bool LastLoadFailed { get; }

    event EventHandler<Configuration>? Changed;

    Configuration Load();

    void Save(Configuration configuration);

    void BeginWatching();

    void StopWatching();
}
