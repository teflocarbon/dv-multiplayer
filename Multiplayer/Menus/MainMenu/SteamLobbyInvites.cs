using DV;
using DV.Localization;
using DV.Platform.Steam;
using DV.UIFramework;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Menus.MainMenu;

/// <summary>Joining a lobby from Steam: the +connect_lobby argument, a join request and a friend's invite.</summary>
public static class SteamLobbyInvites
{
    private static bool hasJoinedCL;

    public static ulong GetLobbyIdFromArgs()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "+connect_lobby")
                return ulong.Parse(args[i + 1]);

        return 0;
    }

    public static IEnumerator JoinFromCommandLine()
    {
        float time = Time.time;

        Multiplayer.LogDebug(() => $"JoinFromCommandLine() {DVSteamworks.Success}");

        if (hasJoinedCL || BuildInfo.BUILD_DESTINATION != "steam")
            yield break;

        hasJoinedCL = true;

        //allow steamworks to initialise
        yield return new WaitUntil(() => { return DVSteamworks.Success || (Time.deltaTime - time) > 5; });

        if (!DVSteamworks.Success)
            yield break;

        var id = GetLobbyIdFromArgs();
        var sId = new SteamId
        {
            Value = id
        };

        var lobby = new Lobby(sId);
        lobby.Refresh();

        QueueLobbyInvite(lobby);
    }

    private static bool CanHandleLobbyRequest()
    {
        return !NetworkLifecycle.Instance.IsServerRunning &&
               !NetworkLifecycle.Instance.IsClientRunning;
    }

    public static void OnLobbyJoinRequest(Lobby lobby, SteamId id)
    {
        var gameCheck = lobby.GetData(SteamworksUtils.LOBBY_MP_MOD_KEY);

        if (gameCheck != SteamworksUtils.LOBBY_MP_MOD_KEY)
            return;

        if (!CanHandleLobbyRequest())
            return;

        lobby.Refresh();

        QueueLobbyInvite(lobby);
    }

    public static void OnLobbyInviteRequest(Friend friend, Lobby lobby)
    {
        var gameCheck = lobby.GetData(SteamworksUtils.LOBBY_MP_MOD_KEY);

        if (gameCheck != SteamworksUtils.LOBBY_MP_MOD_KEY)
            return;

        if (!CanHandleLobbyRequest())
            return;

        lobby.Refresh();

        NetworkLifecycle.Instance.QueueMainMenuEvent(() =>
        {
            var popup = MainMenuThingsAndStuff.Instance.ShowYesNoPopup();

            if (popup == null)
                return;

            popup.labelTMPro.text = $"{friend.Name} invited you to play!\r\nDo you wish to join?";

            Localize locPos = popup.positiveButton.GetComponentInChildren<Localize>();
            locPos.key = "yes";
            locPos.UpdateLocalization();

            Localize locNeg = popup.negativeButton.GetComponentInChildren<Localize>();
            locNeg.key = "no";
            locNeg.UpdateLocalization();

            popup.Closed += (PopupResult result) =>
            {
                Multiplayer.LogDebug(() => $"Agreed to join: {result.closedBy}");
                if (result.closedBy == PopupClosedByAction.Positive)
                    QueueLobbyInvite(lobby);
            };

        });

        NetworkLifecycle.Instance.TriggerMainMenuEventLater();
    }

    public static void QueueLobbyInvite(Lobby lobby)
    {
        NetworkLifecycle.Instance.QueueMainMenuEvent(() =>
        {
            ServerBrowserPane.lobbyToJoin = lobby;
            MainMenuThingsAndStuff.Instance.SwitchToMenu((byte)RightPaneController_Patch.joinMenuIndex);
        });

        NetworkLifecycle.Instance.TriggerMainMenuEventLater();
    }
}
