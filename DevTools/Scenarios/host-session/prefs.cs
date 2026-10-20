// Host: changes (action=set) or restores (action=restore, id=...) the host player's character model
// setting, as the settings screen does. Returns the setting before, the one set, and the server's
// record of the host's character.
var settings = global::Multiplayer.Multiplayer.Settings;
string before = settings.CharacterId;
string id = ctx.Args["action"] == "restore"
    ? ctx.Args["id"]
    : global::Multiplayer.Multiplayer.PlayerModelRegistry.Models.Select(m => m.CharacterId).FirstOrDefault(m => m != before) ?? "";
if (id != "")
{
    settings.CharacterId = id;
    global::Multiplayer.Settings.OnSettingsUpdated?.Invoke(settings);
}
yield return new WaitForSecondsRealtime(1f);
ctx.Result = new { before, set = id, server = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Server.LocalPlayer.CharacterId };
