// Host: whether the server handles the host's own requests of these types in-process instead of
// over its loopback client (docs/design/host-local-session.md, stage 2). Args: types, comma-separated
// names in Multiplayer.Shared.Protocol.Packets.Serverbound.
var server = Multiplayer.Server.Session.NetworkServer.Instance;
object queue = HarmonyLib.AccessTools.Field(server.GetType(), "localRequests").GetValue(server);
var handles = queue.GetType().GetMethod("Handles");
var missing = ctx.Args["types"].Split(',')
    .Where(name => !(bool)handles.Invoke(queue, new object[] {
        server.GetType().Assembly.GetType("Multiplayer.Shared.Protocol.Packets.Serverbound." + name, true) }))
    .ToArray();
return new { handled = missing.Length == 0, missing = string.Join(",", missing) };
