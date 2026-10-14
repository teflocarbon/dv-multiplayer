using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Debugging.Protocol;

public static class DebugStrictSnapshotter
{
    private const int MaximumDepth = 3;
    private const int MaximumCollectionItems = 64;
    private const int MaximumStringLength = 2048;

    public static object Snapshot(object value) => Snapshot(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static object Snapshot(object value, int depth, HashSet<object> visited)
    {
        if (value == null) return null;
        Type type = value.GetType();
        if (type.IsEnum) return value.ToString();
        if (value is string text) return text.Length <= MaximumStringLength ? text : text.Substring(0, MaximumStringLength) + "…";
        if (value is char || value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint ||
            value is long || value is ulong || value is float || value is double || value is decimal || value is DateTime || value is DateTimeOffset || value is Guid) return value;
        if (type.FullName?.StartsWith("UnityEngine.", StringComparison.Ordinal) == true)
        {
            if (type.FullName is "UnityEngine.Vector2" or "UnityEngine.Vector3" or "UnityEngine.Vector4" or "UnityEngine.Vector2Int" or "UnityEngine.Vector3Int" or "UnityEngine.Quaternion" or "UnityEngine.Color" or "UnityEngine.Color32" or "UnityEngine.Rect" or "UnityEngine.RectInt" or "UnityEngine.Bounds" or "UnityEngine.BoundsInt") return DebugValueSnapshotter.Snapshot(value);
            return new Dictionary<string, object> { ["_omitted"] = type.FullName };
        }
        if (depth >= MaximumDepth) return new Dictionary<string, object> { ["_truncated"] = true, ["_type"] = type.FullName };
        if (!type.IsValueType && !visited.Add(value)) return new Dictionary<string, object> { ["_cycle"] = type.FullName };
        try
        {
            if (value is IDictionary dictionary)
            {
                Dictionary<string, object> result = new(StringComparer.Ordinal); int count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count++ >= MaximumCollectionItems) { result["_truncated"] = true; result["_originalCount"] = dictionary.Count; break; }
                    result[Convert.ToString(entry.Key)] = Snapshot(entry.Value, depth + 1, visited);
                }
                return result;
            }
            if (value is IEnumerable enumerable)
            {
                List<object> values = new(); int originalCount = value is ICollection collection ? collection.Count : -1;
                foreach (object item in enumerable) { if (values.Count >= MaximumCollectionItems) break; values.Add(Snapshot(item, depth + 1, visited)); }
                if (originalCount > MaximumCollectionItems) return new Dictionary<string, object> { ["values"] = values, ["_truncated"] = true, ["_originalCount"] = originalCount };
                return values;
            }
            Dictionary<string, object> fields = new(StringComparer.Ordinal) { ["_type"] = type.FullName };
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => !field.IsStatic && !field.IsNotSerialized && !typeof(Delegate).IsAssignableFrom(field.FieldType)))
                fields[field.Name] = Snapshot(field.GetValue(value), depth + 1, visited);
            return fields;
        }
        finally { if (!type.IsValueType) visited.Remove(value); }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
