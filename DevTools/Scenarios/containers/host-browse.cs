// Host: the shell's owner, and what the host gets when it tries to browse it itself.
ushort box = ushort.Parse(ctx.Args["box"]);
var server = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Server;
var host = server.LocalPlayer;
AuthoritativeItemRegistry.TryGet(box, out var r);
var result = Multiplayer.Server.Items.NetworkedColdContainerManager.Browse(host, box, 0, 64, out var view);
return new { owner = (int)(r?.PersistentOwnerPlayerId ?? 0), hostId = (int)host.PlayerId, status = result.Status.ToString(), reason = result.Reason ?? "", rows = view?.Slots?.Count ?? -1 };
