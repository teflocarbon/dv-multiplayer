// Host: what the save it just wrote holds for its own player: the format marker, how many item
// records its GUID owns, and how many entries DV's own inventory list (the copy for vanilla) has.
var data = SaveGameManager.Instance.data;
var root = data.GetJObject("Multiplayer");
string guid = Multiplayer.Multiplayer.Settings.GetGuid().ToString("D");
int records = (root?["WorldItemsV1"] as Newtonsoft.Json.Linq.JArray)?.OfType<Newtonsoft.Json.Linq.JObject>()
    .Count(r => string.Equals((string)r["placementPlayerIdentity"], guid, StringComparison.OrdinalIgnoreCase)) ?? 0;
var mirror = StorageSerializer.LoadStorageData(DV.ThingTypes.StorageType.Inventory, data);
return new { format = (int?)root?["PlayerStateFormat"] ?? 0, hostRecords = records, mirror = mirror?.Count ?? 0 };
