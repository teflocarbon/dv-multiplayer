namespace Multiplayer.Core.Gadgets;

/// <summary>
/// The lease on an installed gadget's knobs, levers and joysticks (gadgets.md, "Optimistic"). It is
/// taken and given back through the cab-control path, on the gadget's car, under an id in the same
/// space as the car's control ports: those are the FNV-1a hash of the port's id, and a gadget's is
/// the hash of "gadget/" and its item's NetId, so the two never meet in practice. One lease covers
/// all of a gadget's controls: two players never turn two knobs of one gadget at once.
/// </summary>
public static class GadgetKnobLease
{
    public const string Prefix = "gadget/";

    public static uint IdOf(ushort itemNetId) => Fnv1a(Prefix + itemNetId);

    // The same hash as the car's port ids (Multiplayer.Shared.Utils.StringHashing).
    private static uint Fnv1a(string text)
    {
        unchecked
        {
            uint hash = 0x811C9DC5;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 0x01000193;
            }
            return hash;
        }
    }
}
