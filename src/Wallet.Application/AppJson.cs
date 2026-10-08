using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallet.Application;

public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
