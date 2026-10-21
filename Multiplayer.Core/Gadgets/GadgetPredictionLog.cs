using System.Collections.Generic;

namespace Multiplayer.Core.Gadgets;

/// <summary>
/// A remote client's predicted gadget changes (gadgets.md, "Optimistic", PR 3b). Each request it sends
/// is applied in its own game at once and kept here, with what undoes it, until the server answers:
/// the change log's entry for it confirms it, a rejection undoes it. A rejection undoes every later
/// prediction too, newest first: they sat on top of it (a bolt on a plate whose install was refused),
/// and the server rejects or answers each of them in turn anyway. One undone and then confirmed is
/// simply applied as the server's. An install takes a provisional negative UID, which never raises
/// vanilla's counter or meets the server's; a request naming one (a wire to the gadget just placed)
/// is held until the install is confirmed and the server's UID is known.
/// </summary>
public sealed class GadgetPredictionLog<TUndo, TRequest>
{
    private sealed class Prediction
    {
        public uint Operation;
        public TUndo Undo;
        public int ProvisionalUid;
    }

    private readonly List<Prediction> live = new();
    private readonly HashSet<uint> undone = new();
    private readonly List<(int uid, TRequest request)> held = new();
    private int lastProvisional;

    public int Count => live.Count;

    /// <summary>A fresh provisional UID: -1, -2, ...</summary>
    public int NewProvisionalUid() => --lastProvisional;

    public static bool IsProvisional(int uid) => uid < 0;

    public void Predicted(uint operation, TUndo undo, int provisionalUid = 0) =>
        live.Add(new Prediction { Operation = operation, Undo = undo, ProvisionalUid = provisionalUid });

    /// <summary>Whether the operation's prediction is still showing (neither confirmed nor undone).</summary>
    public bool IsLive(uint operation) => live.Exists(p => p.Operation == operation);

    /// <summary>Whether the operation's prediction was undone by an earlier rejection.</summary>
    public bool WasUndone(uint operation) => undone.Contains(operation);

    /// <summary>
    /// The server confirmed the operation: its prediction stays, as the server's. Returns its provisional
    /// UID (0 for none), whose held requests <see cref="Release"/> then gives back.
    /// </summary>
    public bool Confirm(uint operation, out int provisionalUid)
    {
        provisionalUid = 0;
        undone.Remove(operation);
        int at = live.FindIndex(p => p.Operation == operation);
        if (at < 0)
            return false;
        provisionalUid = live[at].ProvisionalUid;
        live.RemoveAt(at);
        return true;
    }

    /// <summary>
    /// The server refused the operation: what undoes it and every later prediction, newest first. Their
    /// requests held for a provisional UID are dropped with them.
    /// </summary>
    public List<TUndo> Reject(uint operation)
    {
        List<TUndo> undos = new();
        if (undone.Remove(operation))
            return undos;
        int at = live.FindIndex(p => p.Operation == operation);
        if (at < 0)
            return undos;
        for (int i = live.Count - 1; i >= at; i--)
        {
            Prediction prediction = live[i];
            undos.Add(prediction.Undo);
            if (prediction.Operation != operation)
                undone.Add(prediction.Operation);
            if (prediction.ProvisionalUid != 0)
                held.RemoveAll(h => h.uid == prediction.ProvisionalUid);
            live.RemoveAt(i);
        }
        return undos;
    }

    /// <summary>Holds a request that names a provisional UID until its install is confirmed.</summary>
    public void Hold(int provisionalUid, TRequest request) => held.Add((provisionalUid, request));

    public bool HasHeld(int provisionalUid) => held.Exists(h => h.uid == provisionalUid);

    /// <summary>The requests held for the provisional UID, in the order they were made, and forgets them.</summary>
    public List<TRequest> Release(int provisionalUid)
    {
        List<TRequest> released = new();
        foreach ((int uid, TRequest request) in held)
            if (uid == provisionalUid)
                released.Add(request);
        held.RemoveAll(h => h.uid == provisionalUid);
        return released;
    }

    public void Clear()
    {
        live.Clear();
        undone.Clear();
        held.Clear();
    }
}
