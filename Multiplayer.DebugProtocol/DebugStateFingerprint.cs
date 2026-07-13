using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Text;

namespace Multiplayer.Debugging.Protocol;

public static class DebugStateFingerprint
{
    public static string Compute(object value)
    {
        if (value == null) return string.Empty;
        JToken token = value as JToken ?? JToken.FromObject(value, JsonSerializer.Create(DebugJson.Settings));
        string canonical = Canonical(token).ToString(Formatting.None);
        byte[] bytes = Encoding.UTF8.GetBytes(canonical);
        return DebugPayloadFingerprint.Compute(bytes, 0, bytes.Length);
    }

    private static JToken Canonical(JToken token)
    {
        if (token is JObject obj)
            return new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new JProperty(property.Name, Canonical(property.Value))));
        if (token is JArray array) return new JArray(array.Select(Canonical));
        return token.DeepClone();
    }
}
