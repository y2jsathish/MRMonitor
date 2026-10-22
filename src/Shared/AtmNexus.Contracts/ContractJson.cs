using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtmNexus.Contracts;

/// <summary>Single JSON policy for agent, services and Kafka: camelCase, string enums, ISO-8601 dates.</summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
