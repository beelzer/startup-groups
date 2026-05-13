using System.Text.Json.Serialization;
using Salvo.Core.Elevation;
using Salvo.Core.Models.Flow;
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
// Polymorphic flow-graph types: registering the abstract bases is enough
// for source-gen to handle the [JsonDerivedType] discriminators.
[JsonSerializable(typeof(Node))]
[JsonSerializable(typeof(FlowCondition))]
public sealed partial class ConfigurationJsonContext : JsonSerializerContext;
