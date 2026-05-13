using System.Text.Json.Serialization;
using Salvo.Core.Elevation;
using Salvo.Core.WindowsStartup;

namespace Salvo.Core.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Configuration))]
[JsonSerializable(typeof(ElevationRequest))]
[JsonSerializable(typeof(RegistryRunValueEdit))]
public sealed partial class ConfigurationJsonContext : JsonSerializerContext;
