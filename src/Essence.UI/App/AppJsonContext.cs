using System.Text.Json;
using System.Text.Json.Serialization;

namespace Essence.UI.App;

/// <summary>Source-generated (trim-safe) serialization of what the Android app keeps on the device.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LastFix))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
