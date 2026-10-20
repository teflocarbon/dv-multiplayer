using DV.CabControls.Spec;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using DV.Simulation.Ports;
using DV.ThingTypes;
using HarmonyLib;
using LocoSim.Definitions;
using LocoSim.Implementations;
using Multiplayer.Core.Trains;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Shared.Compatibility;

/// <summary>
/// Gives a sim port to the Custom Car Loader controls that have none, the way DV wires its own doors
/// and windows: a <see cref="GenericControlDefinition"/> in the car's simulation, and an
/// <see cref="InteractablePortFeeder"/> on the control writing its EXT_IN port.
///
/// A DV control only reaches the simulation through a feeder on it, and CCL adds one only where the
/// car's author placed a feeder proxy. Older cars have controls without: the C class's cab doors, its
/// twenty engine-room doors, its sliding windows and wall seat; the S464-730's tender toolboxes; the
/// Santa Fe 2901's tender doors and hatches. Those moved on one game only, and none of them was kept in
/// the save. With a port, they sync like any other control (the cab's and the car body's controls are
/// hooked as they load, and <see cref="Trains.InteractablePortScan"/> makes them player-driven), and the
/// vanilla save keeps them.
///
/// Done to the livery's prefabs the frame after CCL builds each car, long before any spawns, so every game, a
/// dedicated server included, adds the same components under the same names
/// (<see cref="AddedControlIds"/>). Only a control nothing else drives is given one: it's not a button
/// (momentary), has no feeder of any kind, isn't driven by another control (CCL's constant and ratchet
/// feeders, a whistle's dummy), and has nothing on it that's not known to be harmless. Anything unknown
/// leaves it alone, logged.
/// </summary>
internal static class CustomCarLoaderPorts
{
    private const string CCL_ASSEMBLY = "CCL.Importer";
    private const string CCL_CAR_MANAGER = "CCL.Importer.CarManager";

    // Components that give a control a port, or a value from something other than the player.
    private static readonly HashSet<string> Wired = new()
    {
        nameof(InteractablePortFeeder), "InteractableFuseFeeder", "HandbrakeFeeder", "BrakeCylinderReleaseButtonFeeder",
        "OilingPointPortFeederReader", "NotchedPortAnalogInput", "NotchedPortIncrementalInput", "NotchedPortToggleInput",
        "ControlConstantFeederInternal", "ControlRatchetFeederInternal", "CarCouplerFeederInternal", "CarUncouplerFeederInternal",
        // The proxies CCL replaces those with, in case one wasn't.
        "ControlConstantFeeder", "ControlRatchetFeeder", "CarCouplerFeeder", "CarUncouplerFeeder",
    };

    // Components a control may carry and still be given a port: they look, sound, take input or
    // react to its value, but write no port. By type or base type.
    private static readonly HashSet<string> Harmless = new()
    {
        "Transform", "Collider", "Rigidbody", "Renderer", "MeshFilter", "AudioSource",
        "StaticInteractionArea", "AKeyboardInput", "ControlNameHolderBase", "AHandPoseSnapper",
        "OpenableControl", "InternalExternalSnapshotSwitcher", "ControlNameTMPDisplay", "ControlNameTMPDisplayInternal",
    };

    // Components that drive another control, by the field naming it.
    private static readonly Dictionary<string, string> Drives = new()
    {
        ["ControlConstantFeederInternal"] = "ControlObject",
        ["ControlRatchetFeederInternal"] = "ControlObject",
        ["ControlConstantFeeder"] = "ControlObject",
        ["ControlRatchetFeeder"] = "ControlObject",
        ["WhistleDistanceControllerInternal"] = "DummyControl",
        ["WhistleDistanceController"] = "DummyControl",
    };

    private static Harmony harmony;
    private static bool cclPatched;
    private static FieldInfo customCarTypes;
    private static readonly HashSet<TrainCarLivery> wired = new();
    // The controls given a port, with its component: a prefab two liveries share is wired once, and
    // the second livery's simulation gets the components too.
    private static readonly Dictionary<GameObject, (string id, float rest)> given = new();
    private static readonly HashSet<string> addedIds = new();

    public static void Patch(Harmony harmony)
    {
        CustomCarLoaderPorts.harmony = harmony;
        if (!TryPatchCcl())
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name != CCL_ASSEMBLY)
            return;
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        TryPatchCcl();
    }

    private static bool TryPatchCcl()
    {
        if (cclPatched)
            return true;
        Type carManager = AccessTools.TypeByName(CCL_CAR_MANAGER);
        if (carManager == null)
            return false;

        cclPatched = true;
        customCarTypes = AccessTools.Field(carManager, "CustomCarTypes");
        MethodInfo loadCar = AccessTools.Method(carManager, "LoadCar");
        if (customCarTypes == null || loadCar == null)
        {
            Multiplayer.LogWarning("CustomCarLoaderPorts: CCL's CarManager.CustomCarTypes or LoadCar not found, its cars' unwired controls won't sync");
            return true;
        }
        // CCL builds its cars as it loads, and again for a car mod enabled later.
        harmony.Patch(loadCar, postfix: new HarmonyMethod(AccessTools.Method(typeof(CustomCarLoaderPorts), nameof(LoadCarPostfix))));
        harmony.Patch(AccessTools.Method(typeof(SimulationFlow), nameof(SimulationFlow.SetSaveStateData)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CustomCarLoaderPorts), nameof(FillMissingSaveData))));
        // The ones it built before Multiplayer loaded.
        WireNextFrame();
        return true;
    }

    private static void LoadCarPostfix(bool __result)
    {
        if (__result)
            WireNextFrame();
    }

    // CCL swaps its proxies for DV's components with Object.Destroy, which takes effect at the end
    // of the frame: until then each control still carries its LeverProxy or the like, which isn't
    // known to be harmless. Every mod loads in the same frame and cars spawn long after, so the cars
    // are wired on the next.
    private static GameObject pending;

    private static void WireNextFrame()
    {
        if (pending != null)
            return;
        pending = new GameObject("[Multiplayer CCL ports]");
        UnityEngine.Object.DontDestroyOnLoad(pending);
        pending.AddComponent<NextFrame>();
    }

    private sealed class NextFrame : MonoBehaviour
    {
        private void Update()
        {
            pending = null;
            Destroy(gameObject);
            WireLoadedCars();
        }
    }

    private static void WireLoadedCars()
    {
        if (customCarTypes.GetValue(null) is not IEnumerable types)
            return;
        foreach (TrainCarType_v2 type in types.OfType<TrainCarType_v2>())
            foreach (TrainCarLivery livery in type.liveries)
            {
                if (livery == null || !wired.Add(livery))
                    continue;
                try
                {
                    Wire(livery);
                }
                catch (Exception e)
                {
                    Multiplayer.LogException($"CustomCarLoaderPorts: wiring {livery.id}'s controls failed, they won't sync", e);
                }
            }
    }

    private static void Wire(TrainCarLivery livery)
    {
        (string part, GameObject prefab)[] prefabs =
        {
            ("body", livery.prefab),
            ("cab", livery.interiorPrefab), ("cab", livery.explodedInteriorPrefab),
            ("ext", livery.externalInteractablesPrefab), ("ext", livery.explodedExternalInteractablesPrefab),
        };
        prefabs = prefabs.Where(p => p.prefab != null).GroupBy(p => p.prefab).Select(g => g.First()).ToArray();

        HashSet<GameObject> driven = new(prefabs.SelectMany(p => DrivenControls(p.prefab)));
        SimConnectionDefinition sim = livery.prefab.GetComponentInChildren<SimController>(true)?.connectionsDefinition;
        AddedControlIds ids = new(sim?.executionOrder?.Select(d => d != null ? d.ID : null) ?? Enumerable.Empty<string>());
        Dictionary<string, GenericControlDefinition> definitions = new();
        int unwired = 0;

        foreach ((string part, GameObject prefab) in prefabs)
        {
            ids.NextPrefab();
            List<InteractablePortFeeder> feeders = new();
            foreach (ControlSpec spec in prefab.GetComponentsInChildren<ControlSpec>(true))
            {
                GameObject control = spec.gameObject;
                if (given.TryGetValue(control, out (string id, float rest) earlier))
                {
                    if (sim != null && !definitions.ContainsKey(earlier.id))
                        definitions[earlier.id] = AddDefinition(sim, earlier.id, earlier.rest);
                    continue;
                }
                if (!Unwired(spec, driven, livery))
                    continue;
                if (sim == null)
                {
                    unwired++;
                    continue;
                }

                string id = ids.For(part, PathFrom(prefab.transform, spec.transform));
                float rest = RestValue(spec);
                if (!definitions.ContainsKey(id))
                    definitions[id] = AddDefinition(sim, id, rest);
                InteractablePortFeeder feeder = control.AddComponent<InteractablePortFeeder>();
                feeder.portId = id + ".EXT_IN";
                feeders.Add(feeder);
                given[control] = (id, rest);
            }

            if (feeders.Count == 0)
                continue;
            InteractablePortFeedersController controller = prefab.GetComponent<InteractablePortFeedersController>();
            if (controller == null)
                controller = prefab.AddComponent<InteractablePortFeedersController>();
            controller.entries = (controller.entries ?? new InteractablePortFeeder[0]).Concat(feeders).ToArray();
        }

        if (unwired > 0)
            Multiplayer.LogWarning($"CustomCarLoaderPorts: {livery.id} has {unwired} control(s) with no sim port and no simulation to give them one; they won't sync");
        if (definitions.Count == 0)
            return;
        sim.executionOrder = sim.executionOrder.Concat(definitions.Values).ToArray();
        addedIds.UnionWith(definitions.Keys);
        Multiplayer.Log($"CustomCarLoaderPorts: gave {definitions.Count} of {livery.id}'s controls a sim port");
        Multiplayer.LogDebug(() => $"CustomCarLoaderPorts: {livery.id}: {string.Join(", ", definitions.Keys)}");
    }

    // A control nothing but the player moves, and nothing writes a port from: a lever, puller,
    // knob, switch, wheel or screw with no feeder of any kind.
    private static bool Unwired(ControlSpec spec, HashSet<GameObject> driven, TrainCarLivery livery)
    {
        if (spec is not (Lever or Puller or Rotary or ToggleSwitch or Wheel) && spec.GetType().Name != "ScrewInternal")
            return false;
        Component[] components = spec.GetComponents<Component>();
        if (components.Any(c => c != null && Is(c, Wired)) || driven.Contains(spec.gameObject))
            return false;
        // A missing script does nothing.
        Component unknown = components.FirstOrDefault(c => c != null && c != spec && !Is(c, Harmless));
        if (unknown != null)
        {
            Multiplayer.LogWarning($"CustomCarLoaderPorts: {livery.id}'s control {spec.name} has no sim port, but has a {unknown.GetType().FullName}; left alone");
            return false;
        }
        return true;
    }

    private static bool Is(Component component, HashSet<string> names)
    {
        for (Type type = component.GetType(); type != null && type != typeof(Component); type = type.BaseType)
            if (names.Contains(type.Name))
                return true;
        return false;
    }

    private static IEnumerable<GameObject> DrivenControls(GameObject prefab)
    {
        foreach (MonoBehaviour behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            if (behaviour != null && Drives.TryGetValue(behaviour.GetType().Name, out string field) &&
                AccessTools.Field(behaviour.GetType(), field)?.GetValue(behaviour) is GameObject control && control != null)
                yield return control;
    }

    private static IEnumerable<string> PathFrom(Transform root, Transform control)
    {
        Stack<string> names = new();
        for (Transform t = control; t != null && t != root; t = t.parent)
            names.Push(t.name);
        return names;
    }

    private static GenericControlDefinition AddDefinition(SimConnectionDefinition sim, string id, float rest)
    {
        GameObject holder = new(id);
        holder.transform.SetParent(sim.transform, false);
        GenericControlDefinition definition = holder.AddComponent<GenericControlDefinition>();
        definition.ID = id;
        definition.defaultValue = rest;
        // Doors and windows stay where they were left, in the vanilla save like DV's own.
        definition.saveState = true;
        return definition;
    }

    // The value the control has as the prefab places it, which the port now starts at: a car spawns
    // with its doors where they always were. The control's implementation takes it from its pose
    // when it's made (see LeverBase, PullerBase, RotaryBase, WheelBase, ToggleSwitchBase, CCL's
    // ScrewBase).
    private static float RestValue(ControlSpec spec)
    {
        switch (spec)
        {
            case Lever lever:
                return AngleValue(lever.jointLimitMin, lever.jointLimitMax, 0f, lever.useLimits, lever.invertDirection);
            case Rotary rotary:
                return AngleValue(rotary.jointLimitMin, rotary.jointLimitMax, rotary.useSpring ? rotary.jointStartingPos : 0f,
                    rotary.useLimits, rotary.invertDirection);
            case Wheel wheel:
                return AngleValue(wheel.jointLimitMin, wheel.jointLimitMax, wheel.useSpring ? wheel.jointStartingPos : 0f,
                    wheel.useLimits, wheel.invertDirection);
            case ToggleSwitch toggle:
                return AngleValue(toggle.jointLimitMin, toggle.jointLimitMax, 0f, true, false) >= 0.5f ? 1f : 0f;
            case Puller puller:
                float pulled = puller.linearLimit > 0f
                    ? Mathf.Round(Mathf.Clamp01(Mathf.Abs(puller.transform.localPosition.y) / (2f * puller.linearLimit)) * 100f) / 100f
                    : 0f;
                return puller.invertDirection ? 1f - pulled : pulled;
            default:
                // A screw starts at none of its turns.
                return AccessTools.Field(spec.GetType(), "InvertDirection")?.GetValue(spec) is true ? 1f : 0f;
        }
    }

    private static float AngleValue(float min, float max, float angle, bool useLimits, bool invert)
    {
        min = Mathf.Clamp(min, -177f, 177f);
        max = Mathf.Clamp(max, -177f, 177f);
        float value = useLimits && max != min ? Mathf.InverseLerp(min, max, angle) : 0f;
        return invert ? 1f - value : value;
    }

    // A save from before a car's controls had ports has nothing for them: vanilla logs an error for
    // each one, for every car (two dozen a C class). They start where the prefab has them instead.
    private static void FillMissingSaveData(SimulationFlow __instance, JObject savedData)
    {
        if (savedData == null || addedIds.Count == 0)
            return;
        foreach (SimComponent component in __instance.OrderedSimComps)
            if (component is GenericControl && addedIds.Contains(component.id) && savedData[component.id] == null)
                savedData[component.id] = component.GetSaveStateData();
    }
}
