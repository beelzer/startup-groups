using System.Resources;
using System.Windows;

// Skip satellite-assembly probing for English by stating the neutral
// language up-front. Without this, the resource manager probes localized
// satellite folders ("ar", "de", "fr", ...) before falling back to the
// main assembly — measurable cold-start cost (~50-150ms) on first launch.
// MainAssembly tells it the en-US strings live in Salvo.dll itself.
[assembly: NeutralResourcesLanguage("en-US", UltimateResourceFallbackLocation.MainAssembly)]

// Theme info: tells WPF where to look for default control styles. We don't
// ship any themed resources of our own (WPF-UI provides its own theme
// dictionaries via App.xaml merging), so SourceAssembly is the right
// choice for any future custom controls and avoids the runtime probing
// for theme dictionaries that don't exist.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
