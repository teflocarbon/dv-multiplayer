using DV.KeyboardInput;
using DV.Simulation.Controllers;
using DV.Simulation.Ports;
using DV.ThingTypes;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Trains;

/// <summary>
/// The sim ports a livery's interactables write: the ports players drive (ControlPortWriters).
/// Read from the livery's prefabs, so every game has the same set whether or not it has the cab
/// or the car-body controls loaded, a dedicated server included. CCL builds its cars' controls
/// from the same DV components when it imports them, before any car spawns.
/// </summary>
internal static class InteractablePortScan
{
    private static readonly Dictionary<TrainCarLivery, string[]> cache = new();

    public static string[] PortsOf(TrainCarLivery livery)
    {
        if (livery == null)
            return [];
        if (cache.TryGetValue(livery, out string[] ports))
            return ports;

        HashSet<string> found = new();
        // The car body, its cab and its car-body controls (doors, windows, oiling points), and the
        // versions of those it swaps to once it's exploded.
        foreach (GameObject prefab in new[] { livery.prefab, livery.interiorPrefab, livery.explodedInteriorPrefab,
                     livery.externalInteractablesPrefab, livery.explodedExternalInteractablesPrefab })
        {
            if (prefab == null)
                continue;
            // Levers, valves, buttons, windows and doors; the base controls (throttle, reverser
            // and so on) take their values from these too.
            Add(found, prefab.GetComponentsInChildren<InteractablePortFeeder>(true).Select(f => f.portId));
            // An oiler can poured into an oiling point.
            Add(found, prefab.GetComponentsInChildren<OilingPointPortFeederReader>(true).Select(o => o.refillPortId));
            // Keyboard inputs that write a port directly instead of moving a control.
            Add(found, prefab.GetComponentsInChildren<NotchedPortAnalogInput>(true).Select(k => k.portId));
            Add(found, prefab.GetComponentsInChildren<NotchedPortIncrementalInput>(true).Select(k => k.portId));
            Add(found, prefab.GetComponentsInChildren<NotchedPortToggleInput>(true).Select(k => k.portId));
            // A handcar's pump handle.
            Add(found, prefab.GetComponentsInChildren<HandcarBarController>(true).Select(h => h.handleEngagedPortId));
        }

        ports = found.ToArray();
        cache[livery] = ports;
        Multiplayer.LogDebug(() => $"InteractablePortScan: {livery.id} has {ports.Length} interactable ports: {string.Join(", ", ports)}");
        return ports;
    }

    private static void Add(HashSet<string> found, IEnumerable<string> portIds)
    {
        foreach (string portId in portIds)
            if (!string.IsNullOrEmpty(portId))
                found.Add(portId);
    }
}
