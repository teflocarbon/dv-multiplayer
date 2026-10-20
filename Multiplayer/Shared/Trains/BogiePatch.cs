using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Multiplayer.Shared.Trains;

// Every game: drops the Debug.LogError in vanilla's Bogie.UpdatePointSetTraveller. A remote
// client's own bogie patches are Client/Trains/BogieFollowerPatch.
[HarmonyPatch(typeof(Bogie))]
public static class BogiePatch
{

    [HarmonyTranspiler]
    [HarmonyPatch(nameof(Bogie.UpdatePointSetTraveller))]
    private static IEnumerable<CodeInstruction> UpdatePointSetTraveller(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        // Find the Debug.LogError call and remove it along with its argument preparation
        for (int i = 0; i < codes.Count; i++)
        {
            // Look for the Debug.LogError call
            if (codes[i].opcode == OpCodes.Call &&
                codes[i].operand is MethodInfo method &&
                method.DeclaringType == typeof(Debug) &&
                method.Name == nameof(Debug.LogError))
            {
                // Remove the 5 instructions that prepare and call LogError:
                // ldstr, ldarg.1, box, call String.Format, call Debug.LogError
                if (i >= 4)
                {
                    codes.RemoveRange(i - 4, 5);
                    break;
                }
            }
        }

        return codes;
    }
}

