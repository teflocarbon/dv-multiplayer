using System;

namespace Multiplayer.Shared.Gadgets;

/// <summary>
/// While a game applies a gadget change it was told about (gadgets.md, "Requests"): the server a
/// player's request, a client the server's change log or snapshot. The patches that turn a player's
/// gadget action into a request pass straight through inside it, so the same vanilla methods serve the
/// player's intent and the apply.
/// </summary>
public static class GadgetApplyScope
{
    private static int depth;

    public static bool Active => depth > 0;

    public static IDisposable Enter()
    {
        depth++;
        return new Exit();
    }

    private sealed class Exit : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done)
                return;
            done = true;
            depth--;
        }
    }
}
