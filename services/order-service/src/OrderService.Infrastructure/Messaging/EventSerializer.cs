using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrderService.Application.Events;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Turns an <see cref="IntegrationEvent"/> into the JSON on the wire.
/// </summary>
/// <remarks>
/// camelCase, and every timestamp in UTC with a <c>Z</c> suffix, as the contract in
/// <c>docs/events/README.md</c> requires. System.Text.Json would otherwise write
/// <c>+00:00</c>, which is valid ISO-8601 but not what the README promises consumers.
/// </remarks>
public static class EventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new UtcTimestampConverter() }
    };

    public static string Serialize(IntegrationEvent @event) =>
        JsonSerializer.Serialize(@event, Options);

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture));
    }
}
