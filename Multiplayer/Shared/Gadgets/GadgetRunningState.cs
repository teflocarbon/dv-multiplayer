using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Gadgets;

/// <summary>
/// What installed gadgets show that no player sets (gadgets.md, "Gadget state"): the ATS countdown,
/// the distance tracker's count, the alternating controller's phase, the defect detector's snooze.
/// The server's game runs it; a client shows the server's, between updates counting on its own where
/// it can (the ATS, the distance tracker). Each key is either a step (sent when it changes) or a
/// count that runs one way (sent once a second, and at once when it jumps the other way: a reset).
/// </summary>
public static class GadgetRunningState
{
    public enum Kind
    {
        Step,
        CountsUp,
        CountsDown
    }

    private sealed class Key
    {
        public string Name;
        public Kind Kind;
        public Func<GadgetBase, float> Get;
        public Action<GadgetBase, float> Set;
    }

    private sealed class Family
    {
        public Func<GadgetBase, bool> Is;
        public Key[] Keys;
        // After the keys are set: what vanilla does when they change.
        public Action<GadgetBase> Changed;
    }

    private static readonly Family[] families =
    {
        new()
        {
            Is = g => g is GadgetATS,
            Keys = new[]
            {
                new Key
                {
                    Name = "timer", Kind = Kind.CountsDown,
                    Get = g => ((GadgetATS)g).currentTimer,
                    Set = (g, v) => ((GadgetATS)g).currentTimer = v
                }
            }
        },
        new()
        {
            Is = g => g is GadgetRoadrunner,
            Keys = new[]
            {
                new Key
                {
                    Name = "countup", Kind = Kind.CountsUp,
                    Get = g => ((GadgetRoadrunner)g).Countup,
                    Set = (g, v) => ((GadgetRoadrunner)g).countup = v
                },
                new Key
                {
                    Name = "completed", Kind = Kind.Step,
                    Get = g => ((GadgetRoadrunner)g).HasCompleted ? 1f : 0f,
                    Set = (g, v) => ((GadgetRoadrunner)g).HasCompleted = v > 0.5f
                }
            }
        },
        new()
        {
            Is = g => g is AlternatingController,
            Keys = new[]
            {
                new Key
                {
                    Name = "phase", Kind = Kind.Step,
                    Get = g => ((AlternatingController)g).alternatorState,
                    Set = (g, v) => ((AlternatingController)g).alternatorState = (int)Math.Round(v)
                }
            },
            Changed = g => ((AlternatingController)g).FireOnOutputValueUpdated()
        },
        new()
        {
            // Its lamp: easing the throttle off a slip, or still stepping it back up (PR 7).
            Is = g => g is GadgetAntiSlip,
            Keys = new[]
            {
                new Key
                {
                    Name = "active", Kind = Kind.Step,
                    Get = g => ((GadgetAntiSlip)g).IsActivated ? 1f : 0f,
                    Set = (g, v) => ((GadgetAntiSlip)g).IsActivated = v > 0.5f
                }
            }
        },
        new()
        {
            // Its lamp blinks once the engine is at its limit (PR 7).
            Is = g => g is GadgetOverheatProtection,
            Keys = new[]
            {
                new Key
                {
                    Name = "limit", Kind = Kind.Step,
                    Get = g => ((GadgetOverheatProtection)g).HasReachedLimit ? 1f : 0f,
                    Set = (g, v) => ((GadgetOverheatProtection)g).HasReachedLimit = v > 0.5f
                }
            }
        },
        new()
        {
            // A hanger's swing (PR 10): the server runs the pendulum; every other game shows its angle and
            // speed, sent while it moves.
            Is = g => Dangler(g) != null,
            Keys = new[]
            {
                Swing("qx", r => r.x), Swing("qy", r => r.y), Swing("qz", r => r.z), Swing("qw", r => r.w),
                Spin("vx", v => v.x, (d, x) => d.angularVelocity.x = x),
                Spin("vy", v => v.y, (d, y) => d.angularVelocity.y = y),
                Spin("vz", v => v.z, (d, z) => d.angularVelocity.z = z)
            },
            Changed = g => Dangler(g).transform.localRotation = Dangler(g).transform.localRotation.normalized
        },
        new()
        {
            // A plain gadget: its LOD is all there is of it, and has its snooze.
            Is = g => DefectDetector(g) != null,
            Keys = new[]
            {
                new Key
                {
                    Name = "snoozed", Kind = Kind.Step,
                    Get = g => DefectDetector(g).isSnoozed ? 1f : 0f,
                    Set = (g, v) => DefectDetector(g).isSnoozed = v > 0.5f
                }
            },
            Changed = g => DefectDetector(g).UpdateOverlay()
        }
    };

    /// <summary>True while a game applies running state it was given.</summary>
    public static bool Applying => applying > 0;
    private static int applying;

    public static bool Has(GadgetBase gadget) => FamilyOf(gadget) != null;

    /// <summary>A hanger's pendulum, if the gadget has one.</summary>
    public static Dangler Dangler(GadgetBase gadget) =>
        gadget == null ? null : gadget.GetComponentInChildren<Dangler>(true);

    // One component of the pendulum's rotation relative to the gadget (the car's frame, so it means the
    // same in every game whichever way the car points).
    private static Key Swing(string name, Func<UnityEngine.Quaternion, float> part) => new()
    {
        Name = name,
        Kind = Kind.Step,
        Get = g => part(Dangler(g).transform.localRotation),
        Set = (g, v) =>
        {
            Transform t = Dangler(g).transform;
            UnityEngine.Quaternion r = t.localRotation;
            if (name == "qx") r.x = v;
            else if (name == "qy") r.y = v;
            else if (name == "qz") r.z = v;
            else r.w = v;
            t.localRotation = r;
        }
    };

    private static Key Spin(string name, Func<UnityEngine.Vector3, float> part, Action<Dangler, float> set) => new()
    {
        Name = name,
        Kind = Kind.Step,
        Get = g => part(Dangler(g).angularVelocity),
        Set = (g, v) => set(Dangler(g), v)
    };

    /// <summary>The defect detector's LOD, which holds its snooze, if the gadget is one.</summary>
    public static GadgetDefectDetectorLOD DefectDetector(GadgetBase gadget) =>
        gadget == null ? null : gadget.LODObjects.OfType<GadgetDefectDetectorLOD>().FirstOrDefault();

    public static Dictionary<string, float> Capture(GadgetBase gadget)
    {
        Family family = FamilyOf(gadget);
        return family?.Keys.ToDictionary(key => key.Name, key => key.Get(gadget));
    }

    public static void Apply(GadgetBase gadget, IReadOnlyList<string> keys, IReadOnlyList<float> values)
    {
        Family family = FamilyOf(gadget);
        if (family == null || keys == null || values == null)
            return;
        applying++;
        try
        {
            bool any = false;
            for (int i = 0; i < keys.Count && i < values.Count; i++)
            {
                Key key = family.Keys.FirstOrDefault(k => k.Name == keys[i]);
                if (key == null)
                    continue;
                key.Set(gadget, values[i]);
                any = true;
            }
            if (any)
                family.Changed?.Invoke(gadget);
        }
        finally
        {
            applying--;
        }
    }

    /// <summary>How <paramref name="now"/> differs from what was sent: not at all, by a count running
    /// its way (send at the next second), or by a step or a reset (send now).</summary>
    public static Change Compare(GadgetBase gadget, IReadOnlyDictionary<string, float> now, IReadOnlyDictionary<string, float> sent)
    {
        Family family = FamilyOf(gadget);
        if (family == null || now == null)
            return Change.None;
        if (sent == null)
            return Change.Now;
        Change change = Change.None;
        foreach (Key key in family.Keys)
        {
            if (!now.TryGetValue(key.Name, out float value) || !sent.TryGetValue(key.Name, out float before))
                return Change.Now;
            float delta = value - before;
            if (Math.Abs(delta) <= 1e-3f)
                continue;
            if (key.Kind == Kind.Step || (key.Kind == Kind.CountsUp ? delta < 0 : delta > 0))
                return Change.Now;
            change = Change.Running;
        }
        return change;
    }

    public enum Change
    {
        None,
        Running,
        Now
    }

    private static Family FamilyOf(GadgetBase gadget) =>
        gadget == null ? null : families.FirstOrDefault(family => family.Is(gadget));
}
