// Host: the livery of a loco with a Custom Car Loader mechanical stoker, or "none". Arg stoker:
// auto (the first one installed), none, or a livery id.
var wanted = ctx.Args["stoker"];
if (wanted == "none") return new { livery = "none" };
if (wanted != "auto") return new { livery = wanted };
var stoker = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.prefab != null &&
    l.prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(m => m != null && m.GetType().Name.StartsWith("SteamMechanicalStokerDefinition")));
return new { livery = stoker?.id ?? "none" };
