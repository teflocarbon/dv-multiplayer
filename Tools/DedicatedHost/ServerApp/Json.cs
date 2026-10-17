using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DvmpServer;

/// <summary>A JSON object, read leniently: a file that isn't valid JSON still gives its string fields.</summary>
public class Json
{
    private readonly Dictionary<string, object> values;
    private readonly string text;

    private Json(Dictionary<string, object> values, string text)
    {
        this.values = values;
        this.text = text;
    }

    public static Json Parse(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path))
                return null;
            // Shared with a writer: the heartbeat is replaced while we may be reading it.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();
        }
        catch (Exception) { return null; }
        return FromText(text);
    }

    public static Json FromText(string text)
    {
        try
        {
            if (new JavaScriptSerializer().DeserializeObject(text) is Dictionary<string, object> parsed)
                return new Json(parsed, text);
        }
        catch (Exception) { }
        return new Json(null, text);
    }

    public string GetString(string key)
    {
        if (values != null)
            return values.TryGetValue(key, out object value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
    }

    public bool GetBool(string key) =>
        values != null && values.TryGetValue(key, out object value) && value is bool b && b;

    public double? GetNumber(string key) =>
        values != null && values.TryGetValue(key, out object value) && value != null && !(value is string) && !(value is bool)
            ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public string[] GetStrings(string key)
    {
        if (values != null)
            return values.TryGetValue(key, out object value) && value is IEnumerable list && !(value is string)
                ? list.Cast<object>().Where(o => o != null).Select(o => o.ToString()).ToArray()
                : new string[0];
        Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        return m.Success
            ? Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<Match>().Select(x => Regex.Unescape(x.Groups[1].Value)).ToArray()
            : new string[0];
    }
}
