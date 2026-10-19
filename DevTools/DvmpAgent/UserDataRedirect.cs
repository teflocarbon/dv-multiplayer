using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// -dvmp-userdata DIR: the game keeps its user data (profiles, preferences, control bindings,
/// careers and saves) in DIR instead of the real profile (Application.persistentDataPath), so
/// harness games in parallel dvmp sessions never write each other's saves or the player's own.
/// dvmp seeds DIR from the real profile (docs/design/parallel-test-sessions.md).
///
/// DV reads and writes all of it through the one FileSystemStorage that UserManager creates when
/// its scene loads, after mods have loaded; the storage's parameterless constructor points it at
/// the persistent data path, and a postfix moves it to DIR. Found by name, so the agent needs no
/// reference to DV.UserManagement.
/// </summary>
internal static class UserDataRedirect
{
    private const string StorageType = "DV.UserManagement.Storage.Implementation.FileSystemStorage";

    /// <summary>The folder the game was told to use, or null.</summary>
    public static string Dir { get; private set; }

    private static FieldInfo basePath;

    internal static void InstallIfRequested()
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-dvmp-userdata");
        if (i < 0 || i + 1 >= args.Length) return;

        Dir = Path.GetFullPath(args[i + 1]);
        Directory.CreateDirectory(Dir);
        Type storage = AccessTools.TypeByName(StorageType) ?? throw new Exception(StorageType + " not found");
        basePath = AccessTools.Field(storage, "basePath") ?? throw new Exception(StorageType + ".basePath not found");
        new Harmony("dvmp.agent.userdata").Patch(AccessTools.Constructor(storage, Type.EmptyTypes),
            postfix: new HarmonyMethod(typeof(UserDataRedirect), nameof(AfterCreate)));

        // Too late if DV already made its storage: it has read the real profile. dvmp checks the
        // storage's path at the main menu and refuses the game.
        Type manager = AccessTools.TypeByName("DV.UserManagement.UserManager");
        UnityEngine.Object existing = manager == null ? null : UnityEngine.Object.FindObjectOfType(manager);
        if (existing != null && AccessTools.Field(manager, "storage")?.GetValue(existing) != null)
            Debug.LogError("dvmp: user data: DV created its storage before the dev agent loaded; it uses the real profile, not " + Dir);
        else
            Debug.Log("dvmp: user data in " + Dir);
    }

    private static void AfterCreate(object __instance) => basePath.SetValue(__instance, Dir);
}
