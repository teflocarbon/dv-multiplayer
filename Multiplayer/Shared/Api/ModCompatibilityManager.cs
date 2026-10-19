using DV.JObjectExtstensions;
using DV.Utils;
using JetBrains.Annotations;
using MPAPI.Types;
using Multiplayer.Menus.MainMenu;
using Multiplayer.Shared.Protocol.Data;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityModManagerNet;

namespace Multiplayer.Shared.Api;

public class ModCompatibilityManager : SingletonBehaviour<ModCompatibilityManager>
{
    private const string JSON_FILE = "info.json";
    private const string JSON_MP_COMPAT_KEY = "MultiplayerCompatibility";
    private static readonly Dictionary<string, MultiplayerCompatibility> _modCompatibility = [];

    protected override void Awake()
    {
        base.Awake();

        DontDestroyOnLoad(this);

        //Register ourselves
        RegisterCompatibility("Multiplayer", MultiplayerCompatibility.All);

        //we don't care if the client does/doesn't have these mods
        RegisterCompatibility("RuntimeUnityEditor", MultiplayerCompatibility.Client);
        RegisterCompatibility("BookletOrganizer", MultiplayerCompatibility.Host);
        RegisterCompatibility("RemoteDispatch", MultiplayerCompatibility.Client);
        RegisterCompatibility("CommsRadioAPI", MultiplayerCompatibility.Client);
        RegisterCompatibility("DVCustomCargo", MultiplayerCompatibility.All);
        RegisterCompatibility("DVDiscordPresenceMod", MultiplayerCompatibility.Client);
        RegisterCompatibility("DVLangHelper", MultiplayerCompatibility.Client);
        RegisterCompatibility("LightingOverhaul", MultiplayerCompatibility.Client);
        RegisterCompatibility("dv-improved-job-overview", MultiplayerCompatibility.Client);
        RegisterCompatibility("dv_f_spammer", MultiplayerCompatibility.Client);
        RegisterCompatibility("MessageBox", MultiplayerCompatibility.Client);
        RegisterCompatibility("BetterModUI", MultiplayerCompatibility.Client);
        // The dvmp harness agent: an eval/screenshot server on loopback, no gameplay changes, so
        // one player can run it without everyone else needing it.
        RegisterCompatibility("MultiplayerDevTools", MultiplayerCompatibility.Client);

        //Json entries will override hardcoded entries
        ReadModJsons();

        //Hardcoded and json entries will be overridden by API calls
    }

    public void RegisterCompatibility(string modId, MultiplayerCompatibility compatibility)
    {
        Multiplayer.LogDebug(() => $"RegisterCompatibility({modId}, {compatibility})");

        if (!string.IsNullOrEmpty(modId))
            _modCompatibility[modId] = compatibility;
    }

    private void ReadModJsons()
    {
        foreach (var modEntry in UnityModManager.modEntries)
        {
            var jsonPath = Path.Combine(modEntry.Path, JSON_FILE);

            if (File.Exists(jsonPath))
            {
                try
                {
                    var json = File.ReadAllText(jsonPath);
                    var jobj = JObject.Parse(json);
                    var compatStr = jobj.GetString(JSON_MP_COMPAT_KEY);

                    var parsed = Enum.TryParse(compatStr, out MultiplayerCompatibility compatibility);

                    Multiplayer.LogDebug(() => $"Mod '{modEntry.Info.DisplayName}' ({modEntry.Info.Id}) has MP mod compatibility entry \'{compatStr}\', parses to: {compatibility}");

                    if (parsed)
                        RegisterCompatibility(modEntry.Info.Id, compatibility);
                }
                catch (Exception e)
                {
                    Multiplayer.LogException($"Failed to parse mod entry {modEntry.Info.Id}", e);
                }
            }
            else
            {
                Multiplayer.LogWarning($"No json found for {modEntry.Info.Id}");
            }
        }
    }

    public bool TryGetCompatibility(string modId, out MultiplayerCompatibility compatibility)
    {
        return _modCompatibility.TryGetValue(modId, out compatibility);
    }

    public MultiplayerCompatibility GetCompatibility(ModInfo mod)
    {
        if (TryGetCompatibility(mod.Id, out var compatibility))
            return compatibility;

        return MultiplayerCompatibility.Undefined;
    }

    public ModValidationResult ValidateClientMods(ModInfo[] clientMods)
    {
        var localMods = GetLocalMods();
        clientMods ??= [];

        var localById = localMods
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var clientById = clientMods
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        List<ModInfo> missing = clientMods
            .Where(client => !localById.ContainsKey(client.Id))
            .ToList();
        List<ModInfo> extra = localMods
            .Where(local => !clientById.ContainsKey(local.Id))
            .ToList();

        // A shared gameplay mod with a different version is not compatible merely
        // because its ID matches. Report both sides so the existing mismatch UI shows
        // exactly which client and host packages need to be aligned.
        foreach (ModInfo local in localMods)
        {
            if (!clientById.TryGetValue(local.Id, out ModInfo client) ||
                string.Equals(local.Version, client.Version, StringComparison.OrdinalIgnoreCase))
                continue;

            extra.Add(local);
            missing.Add(client);
        }

        bool valid = missing.Count == 0 && extra.Count == 0;

        return new()
        {
            IsValid = valid,
            Missing = missing,
            Extra = extra
        };
    }

    /// <summary>
    /// Checks if any incompatible mods are enabled and generates an message box to alert the user
    /// </summary>
    /// <returns>True if incompatible mods have been found</returns>
    public bool CheckModCompatibility()
    {
        List<string> incompatible = [];

        foreach (var modInfo in ModInfo.FromModEntries(UnityModManager.modEntries))
        {
            if (TryGetCompatibility(modInfo.Id, out var compatibility))
            {
                if (compatibility == MultiplayerCompatibility.Incompatible)
                    incompatible.Add(modInfo.Id);
            }
        }

        if (incompatible.Count == 0)
            return false;

        var message = $"{Locale.MAIN_MENU__INCOMPATIBLE_MODS} {string.Join(", ", incompatible)}";

        MainMenuThingsAndStuff.Instance.ShowOkPopup(message, () => { });

        return true;
    }

    // Returns a list of mods installed and enabled, filtered for mods that are required for hosts and clients
    public ModInfo[] GetLocalMods()
    {
        List<ModInfo> localMods = [];

        foreach (var modInfo in ModInfo.FromModEntries(UnityModManager.modEntries))
        {
            if (TryGetCompatibility(modInfo.Id, out var compatibility))
            {
                // Only include mods that are relevant for client validation
                switch (compatibility)
                {
                    case MultiplayerCompatibility.Undefined:
                    case MultiplayerCompatibility.All:
                        //undefined and "All" mods are required by all clients
                        localMods.Add(modInfo);
                        break;

                    case MultiplayerCompatibility.Incompatible:
                        //There shouldn't be any at this stage
                        localMods.Add(modInfo);
                        break;

                    case MultiplayerCompatibility.Host:
                    case MultiplayerCompatibility.Client:
                        // Not required, should have no impact on game play
                        break;
                }
            }
            else
            {
                // No compatibility info - include for validation (safe default)
                Multiplayer.LogWarning($"No compatibility info for mod {modInfo.Id}, including in validation");
                localMods.Add(modInfo);
            }
        }
        return localMods.ToArray();
    }

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(ModCompatibilityManager)}]";
    }
}

public class ModValidationResult
{
    public bool IsValid { get; set; }
    public List<ModInfo> Missing { get; set; } = [];
    public List<ModInfo> Extra { get; set; } = [];
}
