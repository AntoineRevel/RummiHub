using System.Text.Json;
using System.Text.Json.Serialization;

namespace RummiHub.Core.Contracts;

/// <summary>
/// Centralized JSON serialization options for all RummiHub services.
/// Ensures consistent serialization of Tile structs and enums across API, Worker, and Web.
/// </summary>
public static class RummiJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
