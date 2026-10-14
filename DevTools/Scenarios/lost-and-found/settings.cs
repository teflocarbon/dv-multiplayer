// Host: set the Lost & Found policy (args: owner, nearby, grace) and return the previous values.
var s = Multiplayer.Multiplayer.Settings;
var old = new { owner = s.LostItemOwnerDistance, nearby = s.LostItemNearbyPlayerProtectionDistance, grace = s.LostItemCollectionGraceSeconds };
s.LostItemOwnerDistance = float.Parse(ctx.Args["owner"]);
s.LostItemNearbyPlayerProtectionDistance = float.Parse(ctx.Args["nearby"]);
s.LostItemCollectionGraceSeconds = float.Parse(ctx.Args["grace"]);
return old;
