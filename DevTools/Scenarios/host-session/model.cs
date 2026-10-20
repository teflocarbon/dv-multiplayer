// Client: whether the host's avatar wears character `id` (its model is that prefab's instance).
var host = Multiplayer.Client.Session.NetworkClient.Instance.ClientPlayerManager.Players
    .FirstOrDefault(p => p.PlayerId == byte.Parse(ctx.Args["host"]));
var prefab = global::Multiplayer.Multiplayer.PlayerModelRegistry.GetModelById(ctx.Args["id"]).Prefab;
string model = host?.playerModel?.name ?? "";
return new { found = host != null, model, wears = prefab != null && model.StartsWith(prefab.name) };
