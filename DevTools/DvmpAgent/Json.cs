using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// Serialisation for arbitrary probe results. Unity objects become small stubs instead of
/// whole graphs, Unity value types become plain {x,y,z} objects, and members that throw are
/// skipped rather than failing the response. Must run on the main thread for Unity objects.
/// </summary>
internal static class Json
{
    private const int MaxResultChars = 512 * 1024;

    private static readonly JsonSerializerSettings settings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        ContractResolver = new SafeContractResolver(),
        Converters = { new UnityObjectConverter(), new UnityValueConverter() },
        Error = (_, args) => args.ErrorContext.Handled = true,
        MaxDepth = 32
    };

    private static readonly JsonSerializer serializer = JsonSerializer.Create(settings);

    public static JToken ToToken(object value)
    {
        if (value == null) return JValue.CreateNull();
        try { return JToken.FromObject(value, serializer); }
        catch (Exception e) { return new JObject { ["$unserializable"] = value.GetType().FullName, ["error"] = e.Message }; }
    }

    public static string Serialize(object value)
    {
        string text = JsonConvert.SerializeObject(value, Formatting.None, settings);
        return text;
    }

    /// <summary>Serialises a probe result, truncating oversized output instead of flooding the caller.</summary>
    public static JToken ToBoundedToken(object value, out bool truncated)
    {
        JToken token = ToToken(value);
        string text = token.ToString(Formatting.None);
        truncated = text.Length > MaxResultChars;
        if (!truncated) return token;
        return new JObject
        {
            ["$truncated"] = true,
            ["totalChars"] = text.Length,
            ["preview"] = text.Substring(0, MaxResultChars)
        };
    }

    public static string Error(string message) => Serialize(new { ok = false, error = message });

    public static object Stub(UnityEngine.Object obj)
    {
        if (obj == null) return null; // Unity "fake null" for destroyed objects lands here too.
        string path = obj is Component c ? PathOf(c.transform) : obj is GameObject g ? PathOf(g.transform) : null;
        return new Dictionary<string, object>
        {
            ["$unity"] = obj.GetType().Name,
            ["name"] = obj.name,
            ["id"] = obj.GetInstanceID(),
            ["path"] = path
        };
    }

    public static string PathOf(Transform t)
    {
        if (t == null) return null;
        List<string> parts = new();
        for (Transform cur = t; cur != null && parts.Count < 16; cur = cur.parent) parts.Add(cur.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private sealed class SafeContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);
            Type type = property.PropertyType;
            if (type != null && (typeof(Delegate).IsAssignableFrom(type) || type == typeof(IntPtr) ||
                                 typeof(MemberInfo).IsAssignableFrom(type)))
                property.Ignored = true;
            return property;
        }
    }

    private sealed class UnityObjectConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => typeof(UnityEngine.Object).IsAssignableFrom(objectType);
        public override bool CanRead => false;
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer s) =>
            throw new NotSupportedException();
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer s)
        {
            object stub = Stub(value as UnityEngine.Object);
            if (stub == null) { writer.WriteNull(); return; }
            JToken.FromObject(stub).WriteTo(writer);
        }
    }

    private sealed class UnityValueConverter : JsonConverter
    {
        public override bool CanConvert(Type t) => IsUnityValue(Nullable.GetUnderlyingType(t) ?? t);

        private static bool IsUnityValue(Type t) =>
            t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) || t == typeof(Quaternion) ||
            t == typeof(Color) || t == typeof(Color32) || t == typeof(Bounds) || t == typeof(Rect);
        public override bool CanRead => false;
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer s) =>
            throw new NotSupportedException();

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer s)
        {
            JObject o = value switch
            {
                Vector2 v => new JObject { ["x"] = R(v.x), ["y"] = R(v.y) },
                Vector3 v => new JObject { ["x"] = R(v.x), ["y"] = R(v.y), ["z"] = R(v.z) },
                Vector4 v => new JObject { ["x"] = R(v.x), ["y"] = R(v.y), ["z"] = R(v.z), ["w"] = R(v.w) },
                Quaternion q => Euler(q),
                Color c => new JObject { ["r"] = R(c.r), ["g"] = R(c.g), ["b"] = R(c.b), ["a"] = R(c.a) },
                Color32 c => new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a },
                Bounds b => new JObject { ["center"] = JToken.FromObject(b.center, s), ["size"] = JToken.FromObject(b.size, s) },
                Rect r => new JObject { ["x"] = R(r.x), ["y"] = R(r.y), ["w"] = R(r.width), ["h"] = R(r.height) },
                _ => new JObject()
            };
            o.WriteTo(writer);
        }

        // Quaternions are reported as Euler angles because that is what a human or agent can read.
        private static JObject Euler(Quaternion q)
        {
            Vector3 e = q.eulerAngles;
            return new JObject { ["euler"] = new JObject { ["x"] = R(e.x), ["y"] = R(e.y), ["z"] = R(e.z) } };
        }

        private static double R(float value) => Math.Round(value, 4);
    }
}
