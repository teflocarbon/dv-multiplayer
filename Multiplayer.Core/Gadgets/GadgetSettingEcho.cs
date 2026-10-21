namespace Multiplayer.Core.Gadgets;

public enum GadgetSettingEchoAction : byte
{
    /// <summary>Show the server's values.</summary>
    Apply,
    /// <summary>Keep what this game shows: its own newer change is still on its way.</summary>
    KeepPrediction
}

public readonly struct GadgetSettingEchoDecision
{
    public GadgetSettingEchoDecision(GadgetSettingEchoAction action, bool clearsPending)
    {
        Action = action;
        ClearsPending = clearsPending;
    }

    public GadgetSettingEchoAction Action { get; }
    /// <summary>The server has answered this game's latest change.</summary>
    public bool ClearsPending { get; }
}

/// <summary>
/// What a client does with the server's settings for a gadget it may have changed itself
/// (gadgets.md, "Desync and rollback"). Its change shows at once and goes to the server, which
/// answers every request in order. Until the answer to its latest change arrives, the client keeps
/// what it shows: anything the server sends before that answer is older than the change. The
/// answer itself confirms the prediction, or, refused, carries the server's values to put back.
/// </summary>
public static class GadgetSettingEcho
{
    /// <param name="pendingOperation">This client's latest unanswered change to the gadget, 0 for none.</param>
    /// <param name="fromPlayerId">The player whose request the server's values come from, 0 for none.</param>
    /// <param name="fromOperation">That request's operation id, 0 for none.</param>
    public static GadgetSettingEchoDecision Decide(byte localPlayerId, uint pendingOperation,
        byte fromPlayerId, uint fromOperation, bool refused)
    {
        if (pendingOperation == 0)
            return new GadgetSettingEchoDecision(GadgetSettingEchoAction.Apply, false);
        bool answersLatest = fromPlayerId != 0 && fromPlayerId == localPlayerId && fromOperation == pendingOperation;
        if (!answersLatest)
            return new GadgetSettingEchoDecision(GadgetSettingEchoAction.KeepPrediction, false);
        return new GadgetSettingEchoDecision(
            refused ? GadgetSettingEchoAction.Apply : GadgetSettingEchoAction.KeepPrediction, true);
    }
}
