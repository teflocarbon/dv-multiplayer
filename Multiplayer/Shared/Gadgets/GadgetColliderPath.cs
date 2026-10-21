using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Shared.Gadgets;

/// <summary>
/// The collider a gadget was placed onto, named so another game finds the same one (gadgets.md, "What
/// the server checks"): the child indices from the car down to it, and its index among the colliders on
/// that object. A car builds the same hierarchy in every game, its placement surfaces too
/// (<c>Gadget_Mesh_Placing</c>, copied from its cab at setup); a collider that only a loaded cab has
/// isn't below the car and has no path.
/// </summary>
public static class GadgetColliderPath
{
    public static bool TryDescribe(TrainCar car, Collider collider, out string path, out byte index)
    {
        path = string.Empty;
        index = 0;
        if (car == null || collider == null || !collider.transform.IsChildOf(car.transform))
            return false;
        List<int> steps = new();
        for (Transform t = collider.transform; t != car.transform; t = t.parent)
            steps.Add(t.GetSiblingIndex());
        steps.Reverse();
        path = string.Join("/", steps);
        int at = Array.IndexOf(collider.GetComponents<Collider>(), collider);
        if (at < 0 || at > byte.MaxValue)
            return false;
        index = (byte)at;
        return true;
    }

    public static Collider Resolve(TrainCar car, string path, byte index)
    {
        if (car == null || string.IsNullOrEmpty(path))
            return null;
        Transform t = car.transform;
        foreach (string step in path.Split('/'))
        {
            if (!int.TryParse(step, out int child) || child < 0 || child >= t.childCount)
                return null;
            t = t.GetChild(child);
        }
        Collider[] colliders = t.GetComponents<Collider>();
        return index < colliders.Length ? colliders[index] : null;
    }
}
