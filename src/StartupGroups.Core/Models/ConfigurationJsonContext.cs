using System.Text.Json.Serialization;
using StartupGroups.Core.Elevation;
using StartupGroups.Core.WindowsStartup;

namespace StartupGroups.Core.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Configuration))]
[JsonSerializable(typeof(ElevationRequest))]
[JsonSerializable(typeof(RegistryRunValueEdit))]
public sealed partial class ConfigurationJsonContext : JsonSerializerContext;
