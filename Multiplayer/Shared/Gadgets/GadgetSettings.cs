using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Shared.Gadgets;

/// <summary>
/// The settings players change on installed gadgets, per family (gadgets.md, "Gadget state"): the
/// keys vanilla saves them under, read from the gadget's own save data, and the setter for each.
/// Reading them the same way for every family is what lets the server's watch and a client's change
/// detection see a knob turned by hand and a change made from the HUD alike. Running state (the ATS
/// timer, the distance count, the alternating controller's phase) isn't here.
/// </summary>
public static class GadgetSettings
{
    private sealed class Family
    {
        public Type Type;
        // In the order they are applied, which is vanilla's load order where it matters (a DPU's
        // channel before its regime).
        public string[] Keys;
        public Action<GadgetBase, string, float> Set;
    }

    // Most derived first. The alternating controller is a switch whose "value" is an interval with
    // a phase that runs: its own family, with running state, comes later (gadget PR 6b).
    private static readonly Family[] families =
    {
        new() { Type = typeof(AlternatingController), Keys = Array.Empty<string>() },
        new()
        {
            Type = typeof(GadgetSwitch), Keys = new[] { "value" },
            Set = (g, _, v) => ((GadgetSwitch)g).SetOutputValue(v)
        },
        new()
        {
            Type = typeof(GadgetAmpLimiter), Keys = new[] { "mode" },
            Set = (g, _, v) => ((GadgetAmpLimiter)g).ModeIndex = (int)Math.Round(v)
        },
        new()
        {
            Type = typeof(GadgetOverheatProtection), Keys = new[] { "mode", "cut" },
            Set = (g, key, v) =>
            {
                var gadget = (GadgetOverheatProtection)g;
                if (key == "mode") gadget.ModeIndex = (int)Math.Round(v);
                else gadget.cutEngine = v > 0.5f;
            }
        },
        new()
        {
            Type = typeof(GadgetATS), Keys = new[] { "mode" },
            Set = (g, _, v) => ((GadgetATS)g).SetRegime((int)Math.Round(v))
        },
        new()
        {
            Type = typeof(GadgetRoadrunner), Keys = new[] { "target" },
            Set = (g, _, v) => ((GadgetRoadrunner)g).LengthMeters = (int)Math.Round(v)
        },
        new()
        {
            Type = typeof(GadgetDPU), Keys = new[] { "channel", "reverse", "regime" },
            Set = (g, key, v) =>
            {
                var gadget = (GadgetDPU)g;
                if (key == "channel") gadget.Channel = (int)Math.Round(v);
                else if (key == "reverse") gadget.ReverseOrientation = v > 0.5f;
                else gadget.Regime = (GadgetDPU.WirelessMode)(int)Math.Round(v);
            }
        },
        new()
        {
            Type = typeof(GadgetSwitchSetter), Keys = new[] { "dir", "sideCorrection", "mode" },
            Set = (g, key, v) =>
            {
                var gadget = (GadgetSwitchSetter)g;
                if (key == "dir") gadget.DirectionMode = (int)Math.Round(v);
                else if (key == "sideCorrection") gadget.SideCorrectRegime = v > 0.5f;
                else gadget.Mode = (int)Math.Round(v);
            }
        },
        new()
        {
            Type = typeof(GadgetProximityScreen), Keys = new[] { "channel", "mode" },
            Set = (g, key, v) =>
            {
                var gadget = (GadgetProximityScreen)g;
                if (key == "channel") gadget.CurrentChannel = (int)Math.Round(v);
                else gadget.CurrentMode = (int)Math.Round(v);
            }
        },
        new()
        {
            Type = typeof(GadgetControlPanel), Keys = new[] { "tilt" },
            Set = (g, _, v) => ((GadgetControlPanel)g).tilt = v
        }
    };

    private static readonly Dictionary<Type, MethodInfo> syncControls = new();

    /// <summary>True while a game applies settings it was given (the server a request, a client the
    /// server's): what changes then isn't a player's own change.</summary>
    public static bool Applying => applying > 0;
    private static int applying;

    /// <summary>Whether the gadget has settings players change.</summary>
    public static bool Has(GadgetBase gadget) => FamilyOf(gadget) is { Keys.Length: > 0 };

    /// <summary>The gadget's settings by save key, or null if it has none.</summary>
    public static Dictionary<string, float> Capture(GadgetBase gadget)
    {
        Family family = FamilyOf(gadget);
        if (family == null || family.Keys.Length == 0)
            return null;
        JObject data = new();
        gadget.SaveDataRequested(data);
        Dictionary<string, float> settings = new(family.Keys.Length);
        foreach (string key in family.Keys)
            if (data[key] is JValue value)
                settings[key] = value.Type == JTokenType.Boolean ? ((bool)value ? 1f : 0f) : (float)value;
        return settings;
    }

    /// <summary>
    /// Sets the given settings through vanilla's setters, in the family's order, then moves the
    /// gadget's physical controls to match (its LODs' SyncControls). Unknown keys are ignored.
    /// </summary>
    public static void Apply(GadgetBase gadget, IReadOnlyList<string> keys, IReadOnlyList<float> values)
    {
        Family family = FamilyOf(gadget);
        if (family?.Set == null || keys == null || values == null)
            return;
        applying++;
        try
        {
            foreach (string key in family.Keys)
            {
                int index = IndexOf(keys, key);
                if (index >= 0 && index < values.Count)
                    family.Set(gadget, key, values[index]);
            }
            foreach (CustomizerLODObject lod in gadget.LODObjects)
                SyncControlsOf(lod.GetType())?.Invoke(lod, null);
        }
        finally
        {
            applying--;
        }
    }

    /// <summary>Whether two captures differ by more than a knob's rounding.</summary>
    public static bool Differ(IReadOnlyDictionary<string, float> a, IReadOnlyDictionary<string, float> b)
    {
        if (a == null || b == null)
            return a != b;
        if (a.Count != b.Count)
            return true;
        return a.Any(pair => !b.TryGetValue(pair.Key, out float other) || Math.Abs(pair.Value - other) > 1e-3f);
    }

    private static Family FamilyOf(GadgetBase gadget) =>
        gadget == null ? null : families.FirstOrDefault(family => family.Type.IsInstanceOfType(gadget));

    private static int IndexOf(IReadOnlyList<string> keys, string key)
    {
        for (int i = 0; i < keys.Count; i++)
            if (keys[i] == key)
                return i;
        return -1;
    }

    private static MethodInfo SyncControlsOf(Type lodType)
    {
        if (!syncControls.TryGetValue(lodType, out MethodInfo method))
            syncControls[lodType] = method = lodType.GetMethod("SyncControls",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        return method;
    }
}
