using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Debugging.Protocol;

public static class DebugValueSnapshotter
{
    private const int MaxDepth = 6;
    private const int MaxCollectionItems = 256;
    private const int MaxStringLength = 8192;
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> members = new();
    private static readonly HashSet<string> sensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "Token", "ApiToken", "AccessToken", "RefreshToken", "Secret"
    };

    public static Dictionary<string, object> SnapshotObject(object value)
    {
        return Snapshot(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)) as Dictionary<string, object>
               ?? new Dictionary<string, object>(StringComparer.Ordinal) { ["value"] = Snapshot(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)) };
    }

    public static object Snapshot(object value) => Snapshot(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static object Snapshot(object value, int depth, HashSet<object> visited)
    {
        if (value == null) return null;
        Type type = value.GetType();
        Type nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null) type = nullable;
        if (type.IsEnum) return value.ToString();
        if (value is string text) return text.Length <= MaxStringLength ? text : text.Substring(0, MaxStringLength) + "…";
        if (value is char || value is bool || value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal ||
            value is DateTime || value is DateTimeOffset || value is Guid) return value;
        if (TrySnapshotUnityValue(value, type, out object unityValue)) return unityValue;
        if (depth >= MaxDepth) return $"<{type.FullName}: maximum depth>";

        if (!type.IsValueType && !visited.Add(value)) return $"<{type.FullName}: cycle>";
        try
        {
            if (value is IDictionary dictionary)
            {
                Dictionary<string, object> result = new(StringComparer.Ordinal);
                int count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count++ >= MaxCollectionItems) { result["$truncated"] = true; break; }
                    result[Convert.ToString(entry.Key)] = Snapshot(entry.Value, depth + 1, visited);
                }
                return result;
            }
            if (value is IEnumerable enumerable)
            {
                List<object> result = new();
                foreach (object item in enumerable)
                {
                    if (result.Count >= MaxCollectionItems) { result.Add("<truncated>"); break; }
                    result.Add(Snapshot(item, depth + 1, visited));
                }
                return result;
            }

            if (type.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) == true &&
                type.Name is not ("Vector2" or "Vector3" or "Vector4" or "Quaternion" or "Color" or "Color32" or "Rect"))
            {
                Dictionary<string, object> identity = new(StringComparer.Ordinal) { ["type"] = type.FullName };
                TryReadSimpleMember(value, type, "name", identity);
                MethodInfo instanceId = type.GetMethod("GetInstanceID", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (instanceId != null)
                    try { identity["instanceId"] = instanceId.Invoke(value, null); } catch { }
                return identity;
            }

            Dictionary<string, object> objectResult = new(StringComparer.Ordinal);
            foreach (MemberInfo member in members.GetOrAdd(type, GetSerializableMembers))
            {
                string name = member.Name;
                if (IsSensitive(name)) { objectResult[name] = "<redacted>"; continue; }
                try
                {
                    object memberValue = member is FieldInfo field ? field.GetValue(value) : ((PropertyInfo)member).GetValue(value, null);
                    objectResult[name] = Snapshot(memberValue, depth + 1, visited);
                }
                catch (Exception exception) { objectResult[name] = $"<unavailable: {exception.GetType().Name}>"; }
            }
            return objectResult;
        }
        finally
        {
            if (!type.IsValueType) visited.Remove(value);
        }
    }

    private static MemberInfo[] GetSerializableMembers(Type type) => type
        .GetMembers(BindingFlags.Instance | BindingFlags.Public)
        .Where(member => member is FieldInfo || member is PropertyInfo property && property.CanRead && property.GetIndexParameters().Length == 0)
        .OrderBy(member => member.Name, StringComparer.Ordinal)
        .ToArray();

    private static bool IsSensitive(string name) => sensitiveNames.Contains(name) ||
        name.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TrySnapshotUnityValue(object value, Type type, out object result)
    {
        result = null;
        string name = type.FullName;
        string[] members = name switch
        {
            "UnityEngine.Vector2" => new[] { "x", "y" },
            "UnityEngine.Vector3" => new[] { "x", "y", "z" },
            "UnityEngine.Vector4" => new[] { "x", "y", "z", "w" },
            "UnityEngine.Vector2Int" => new[] { "x", "y" },
            "UnityEngine.Vector3Int" => new[] { "x", "y", "z" },
            "UnityEngine.Quaternion" => new[] { "x", "y", "z", "w" },
            "UnityEngine.Color" => new[] { "r", "g", "b", "a" },
            "UnityEngine.Color32" => new[] { "r", "g", "b", "a" },
            "UnityEngine.Rect" => new[] { "x", "y", "width", "height" },
            "UnityEngine.RectInt" => new[] { "x", "y", "width", "height" },
            _ => null
        };
        if (members != null)
        {
            Dictionary<string, object> projected = new(StringComparer.Ordinal);
            foreach (string member in members) projected[member] = ReadNamedValue(value, type, member);
            result = projected; return true;
        }
        if (name is "UnityEngine.Bounds" or "UnityEngine.BoundsInt")
        {
            result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["center"] = Snapshot(ReadNamedValue(value, type, "center"), 0, new HashSet<object>(ReferenceEqualityComparer.Instance)),
                ["size"] = Snapshot(ReadNamedValue(value, type, "size"), 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
            };
            return true;
        }
        return false;
    }

    private static object ReadNamedValue(object value, Type type, string name)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        if (field != null) return field.GetValue(value);
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        return property?.CanRead == true ? property.GetValue(value, null) : null;
    }

    private static void TryReadSimpleMember(object value, Type type, string name, Dictionary<string, object> target)
    {
        try
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanRead == true) target[name] = Convert.ToString(property.GetValue(value, null));
        }
        catch { }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
