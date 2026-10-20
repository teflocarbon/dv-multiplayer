using DV.CashRegister;
using HarmonyLib;
using Multiplayer.Server.Players;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Multiplayer.Server.Stations;

// Measures from the server's players, not the local one. Vanilla starts the check in OnEnable, which
// CashRegisterBasePatch.OnEnable skips for a remote client's module registers.
[HarmonyPatch]
public class CashRegisterBaseReturnMoneyToPlayerCheckPatch
{
    const int TARGET_NOPS = 3;
    static readonly CodeInstruction targetMethod = CodeInstruction.Call(typeof(Vector3), "op_Subtraction", [typeof(Vector3), typeof(Vector3)], null);

    public static IEnumerable<MethodBase> TargetMethods()
    {
        //We're targeting an 'IEnumerable'; this gets compiled as a state machine with
        //a method per state.
        //Find all of the resultant states that are a 'MoveNext', these are the methods we need to patch.
        //Doing this dynamically reduces the chance a game update breaks the transpiler
        return typeof(CashRegisterBase)
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(t => t.Name.StartsWith("<ReturnMoneyToPlayerCheck>"))
            .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(m => m.Name == "MoveNext");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int nopCtr = 0;
        bool foundEntry = false;

        List<CodeInstruction> newCode = [] ;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && instruction.operand?.ToString() == targetMethod.operand?.ToString())
            {
                foundEntry = true;
                newCode.Add(CodeInstruction.Call(typeof(PlayerDistance), nameof(PlayerDistance.AnyPlayerSqrMag), [typeof(Vector3)], null)); //inject our method
            }
            else if (foundEntry && nopCtr < TARGET_NOPS)
            {
                nopCtr++;
                newCode.Add(new CodeInstruction(OpCodes.Nop));
            }
            else
            {
                newCode.Add(instruction);
            }
        }

        return newCode;
    }
}
