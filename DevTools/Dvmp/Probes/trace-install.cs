// Mutation tracer. Finds every call site in the game and mod that changes an object's physical
// state (physics freeze, parent, active, destroy, colliders, sleep, position, velocity) and
// reroutes it through a wrapper that performs the original call and, for networked items,
// records who did it. Unity's setters are native and can't be Harmony-patched themselves, but
// the game isn't obfuscated, so their callers can be.
//
// Install is idempotent: running it again only refreshes which items are watched (picks up
// items spawned since). History lives in DevContext.State so other probes can read it.
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.Shared.Items;

public static class Probe
{
    private const string HarmonyId = "dvmp.tracer";
    private const int HistoryPerItem = 60;

    public static object Run(DevContext ctx)
    {
        int watched = RefreshWatchList();
        if (DevContext.State.ContainsKey("dvmp.trace.installed"))
            return new { installed = true, refreshed = true, watchedObjects = watched };

        var targets = Targets();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var candidates = FindCallers(targets.Keys);
        var harmony = new Harmony(HarmonyId);
        var transpiler = new HarmonyMethod(typeof(Probe).GetMethod(nameof(Transpile), BindingFlags.NonPublic | BindingFlags.Static));
        int patched = 0, failed = 0;
        var failures = new List<string>();
        foreach (MethodBase method in candidates)
        {
            // Patching can fail transiently with IOException (Harmony/MonoMod temp files shared
            // between game processes), so retry before giving up on a method.
            Exception last = null;
            for (int attempt = 0; attempt < 4 && (attempt == 0 || last is System.IO.IOException); attempt++)
            {
                try { harmony.Patch(method, transpiler: transpiler); last = null; break; }
                catch (Exception e) { last = e; }
            }
            if (last == null) { patched++; continue; }
            failed++;
            if (failures.Count < 10) failures.Add(method.DeclaringType?.Name + "." + method.Name + ": " + last.GetType().Name + ": " + last.Message);
        }
        DevContext.State["dvmp.trace.installed"] = true;
        return new { installed = true, candidates = candidates.Count, patched, failed, failures, watchedObjects = watched, ms = sw.ElapsedMilliseconds };
    }

    // ---------------------------------------------------------------- what to intercept

    private static readonly Dictionary<MethodInfo, MethodInfo> replacements = new();

    private static Dictionary<MethodInfo, MethodInfo> Targets()
    {
        if (replacements.Count > 0) return replacements;
        void Map(Type type, string name, Type[] args, string wrapper)
        {
            MethodInfo original = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, null, args, null);
            MethodInfo replacement = typeof(Probe).GetMethod(wrapper, BindingFlags.Public | BindingFlags.Static);
            if (original != null && replacement != null) replacements[original] = replacement;
        }
        Map(typeof(Rigidbody), "set_isKinematic", new[] { typeof(bool) }, nameof(Kinematic));
        Map(typeof(Rigidbody), "set_detectCollisions", new[] { typeof(bool) }, nameof(DetectCollisions));
        Map(typeof(Rigidbody), "Sleep", Type.EmptyTypes, nameof(Sleep));
        Map(typeof(Rigidbody), "WakeUp", Type.EmptyTypes, nameof(WakeUp));
        Map(typeof(Rigidbody), "set_velocity", new[] { typeof(Vector3) }, nameof(Velocity));
        Map(typeof(Rigidbody), "set_position", new[] { typeof(Vector3) }, nameof(RbPosition));
        Map(typeof(Rigidbody), "MovePosition", new[] { typeof(Vector3) }, nameof(RbMovePosition));
        Map(typeof(Collider), "set_enabled", new[] { typeof(bool) }, nameof(ColliderEnabled));
        Map(typeof(Collider), "set_isTrigger", new[] { typeof(bool) }, nameof(ColliderTrigger));
        Map(typeof(GameObject), "SetActive", new[] { typeof(bool) }, nameof(SetActive));
        Map(typeof(Transform), "SetParent", new[] { typeof(Transform) }, nameof(SetParent1));
        Map(typeof(Transform), "SetParent", new[] { typeof(Transform), typeof(bool) }, nameof(SetParent2));
        Map(typeof(Transform), "set_parent", new[] { typeof(Transform) }, nameof(SetParentProperty));
        Map(typeof(Transform), "set_position", new[] { typeof(Vector3) }, nameof(Position));
        Map(typeof(Transform), "set_localPosition", new[] { typeof(Vector3) }, nameof(LocalPosition));
        Map(typeof(Transform), "SetPositionAndRotation", new[] { typeof(Vector3), typeof(Quaternion) }, nameof(PositionAndRotation));
        Map(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object) }, nameof(Destroy1));
        Map(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object), typeof(float) }, nameof(Destroy2));
        Map(typeof(UnityEngine.Object), "DestroyImmediate", new[] { typeof(UnityEngine.Object) }, nameof(DestroyImmediate1));
        return replacements;
    }

    // Byte-scans IL for call/callvirt tokens that resolve to a target. Much faster than reading
    // every method through Harmony; false positives are harmless (the transpiler finds nothing).
    private static List<MethodBase> FindCallers(IEnumerable<MethodInfo> targets)
    {
        var targetSet = new HashSet<MethodBase>(targets);
        var result = new List<MethodBase>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = assembly.GetName().Name;
            if (!(name == "Assembly-CSharp" || name == "Multiplayer" || name.StartsWith("DV.") || name == "WorldStreamer"))
                continue;
            Type[] types;
            try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (Type type in types)
            {
                if (type.ContainsGenericParameters) continue;
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    byte[] il;
                    try { il = method.GetMethodBody()?.GetILAsByteArray(); } catch { continue; }
                    if (il == null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x28 && il[i] != 0x6F) continue; // call, callvirt
                        byte table = il[i + 4];
                        if (table != 0x0A && table != 0x06 && table != 0x2B) continue; // MemberRef, MethodDef, MethodSpec
                        int token = BitConverter.ToInt32(il, i + 1);
                        MethodBase called;
                        try { called = method.Module.ResolveMethod(token, type.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null); }
                        catch { continue; }
                        if (called != null && targetSet.Contains(called)) { result.Add(method); break; }
                    }
                }
            }
        }
        return result;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        string caller = (original.DeclaringType?.FullName ?? "?") + "." + original.Name;
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo called && replacements.TryGetValue(called, out MethodInfo wrapper))
            {
                // Branches into the original call now land on the injected caller string.
                CodeInstruction tag = new CodeInstruction(OpCodes.Ldstr, caller) { labels = instruction.labels.ToList(), blocks = instruction.blocks.ToList() };
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return tag;
                yield return new CodeInstruction(OpCodes.Call, wrapper);
                continue;
            }
            yield return instruction;
        }
    }

    // ---------------------------------------------------------------- recording

    private static Dictionary<int, ushort> Watch =>
        (Dictionary<int, ushort>)(DevContext.State.TryGetValue("dvmp.trace.watch", out object w) ? w : DevContext.State["dvmp.trace.watch"] = new Dictionary<int, ushort>());

    private static Dictionary<ushort, Queue<string>> History =>
        (Dictionary<ushort, Queue<string>>)(DevContext.State.TryGetValue("dvmp.trace.history", out object h) ? h : DevContext.State["dvmp.trace.history"] = new Dictionary<ushort, Queue<string>>());

    // Maps the instance ids of every networked item's GameObject, Transform, Rigidbody and
    // colliders (and their GameObjects/Transforms) to its NetId.
    private static int RefreshWatchList()
    {
        var watch = Watch;
        watch.Clear();
        foreach (NetworkedItem item in Dev.All<NetworkedItem>())
        {
            if (item == null || item.NetId == 0) continue;
            ushort id = item.NetId;
            watch[item.gameObject.GetInstanceID()] = id;
            watch[item.transform.GetInstanceID()] = id;
            foreach (Rigidbody rb in item.GetComponentsInChildren<Rigidbody>(true)) watch[rb.GetInstanceID()] = id;
            foreach (Collider c in item.GetComponentsInChildren<Collider>(true))
            {
                watch[c.GetInstanceID()] = id;
                watch[c.gameObject.GetInstanceID()] = id;
                watch[c.transform.GetInstanceID()] = id;
            }
        }
        return watch.Count;
    }

    // Objects already checked and found not to belong to a networked item.
    private static readonly HashSet<int> unrelated = new();

    private static void Record(UnityEngine.Object target, string op, string caller)
    {
        if (target == null) return;
        int instance = target.GetInstanceID();
        if (!Watch.TryGetValue(instance, out ushort id))
        {
            // Not pre-registered (e.g. spawned after install, or installed before the world
            // loaded): walk up the hierarchy once to find the owning networked item.
            if (unrelated.Contains(instance)) return;
            NetworkedItem owner = OwningItem(target);
            if (owner == null) { unrelated.Add(instance); return; }
            if (owner.NetId == 0)
            {
                // Not registered yet (e.g. during world load): keep the events under the item
                // instance and merge them once it gets its NetId.
                Append(Pending, owner.GetInstanceID(), Describe(target, op, caller));
                return;
            }
            Watch[instance] = id = owner.NetId;
            if (Pending.TryGetValue(owner.GetInstanceID(), out Queue<string> early))
            {
                Pending.Remove(owner.GetInstanceID());
                foreach (string e in early) Append(History, id, e);
            }
        }
        Append(History, id, Describe(target, op, caller));
    }

    private static Dictionary<int, Queue<string>> Pending =>
        (Dictionary<int, Queue<string>>)(DevContext.State.TryGetValue("dvmp.trace.pending", out object p) ? p : DevContext.State["dvmp.trace.pending"] = new Dictionary<int, Queue<string>>());

    private static string Describe(UnityEngine.Object target, string op, string caller)
    {
        string where = target is Component c ? " @" + V(c.transform.position) : target is GameObject g ? " @" + V(g.transform.position) : "";
        return $"{Time.time:F2}s f{Time.frameCount} {op}{where} <- {caller}";
    }

    private static void Append<TKey>(Dictionary<TKey, Queue<string>> map, TKey key, string entry)
    {
        if (!map.TryGetValue(key, out Queue<string> queue)) map[key] = queue = new Queue<string>();
        queue.Enqueue(entry);
        while (queue.Count > HistoryPerItem) queue.Dequeue();
    }

    private static NetworkedItem OwningItem(UnityEngine.Object target)
    {
        Transform t = target is Component c ? c.transform : target is GameObject g ? g.transform : null;
        for (int depth = 0; t != null && depth < 8; depth++, t = t.parent)
        {
            NetworkedItem item = t.GetComponent<NetworkedItem>();
            if (item != null) return item;
        }
        return null;
    }

    private static string V(Vector3 v) => Dev.Abs(v).ToString("F2");

    // Wrappers: same stack shape as the original call plus the caller string.
    public static void Kinematic(Rigidbody rb, bool value, string c) { rb.isKinematic = value; Record(rb, "isKinematic=" + value, c); }
    public static void DetectCollisions(Rigidbody rb, bool value, string c) { rb.detectCollisions = value; Record(rb, "detectCollisions=" + value, c); }
    public static void Sleep(Rigidbody rb, string c) { rb.Sleep(); Record(rb, "Sleep()", c); }
    public static void WakeUp(Rigidbody rb, string c) { rb.WakeUp(); Record(rb, "WakeUp()", c); }
    public static void Velocity(Rigidbody rb, Vector3 value, string c) { rb.velocity = value; Record(rb, "velocity=" + value.ToString("F2"), c); }
    public static void RbPosition(Rigidbody rb, Vector3 value, string c) { rb.position = value; Record(rb, "rb.position=" + V(value), c); }
    public static void RbMovePosition(Rigidbody rb, Vector3 value, string c) { rb.MovePosition(value); Record(rb, "MovePosition " + V(value), c); }
    public static void ColliderEnabled(Collider col, bool value, string c) { col.enabled = value; Record(col, $"collider {col.name}.enabled={value}", c); }
    public static void ColliderTrigger(Collider col, bool value, string c) { col.isTrigger = value; Record(col, $"collider {col.name}.isTrigger={value}", c); }
    public static void SetActive(GameObject go, bool value, string c) { go.SetActive(value); Record(go, $"SetActive({value}) {go.name}", c); }
    public static void SetParent1(Transform t, Transform p, string c) { t.SetParent(p); Record(t, "SetParent " + (p ? p.name : "null"), c); }
    public static void SetParent2(Transform t, Transform p, bool keep, string c) { t.SetParent(p, keep); Record(t, "SetParent " + (p ? p.name : "null"), c); }
    public static void SetParentProperty(Transform t, Transform p, string c) { t.parent = p; Record(t, "parent=" + (p ? p.name : "null"), c); }
    public static void Position(Transform t, Vector3 value, string c) { t.position = value; Record(t, "position=" + V(value), c); }
    public static void LocalPosition(Transform t, Vector3 value, string c) { t.localPosition = value; Record(t, "localPosition=" + value.ToString("F2"), c); }
    public static void PositionAndRotation(Transform t, Vector3 p, Quaternion r, string c) { t.SetPositionAndRotation(p, r); Record(t, "SetPositionAndRotation " + V(p), c); }
    public static void Destroy1(UnityEngine.Object o, string c) { Record(o, "Destroy " + (o ? o.name : "null"), c); UnityEngine.Object.Destroy(o); }
    public static void Destroy2(UnityEngine.Object o, float t, string c) { Record(o, "Destroy " + (o ? o.name : "null"), c); UnityEngine.Object.Destroy(o, t); }
    public static void DestroyImmediate1(UnityEngine.Object o, string c) { Record(o, "DestroyImmediate " + (o ? o.name : "null"), c); UnityEngine.Object.DestroyImmediate(o); }
}
