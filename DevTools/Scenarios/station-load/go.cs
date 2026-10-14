// Teleport onto the ground near an absolute x/z (args: x, z), offset a little per peer.
yield return LocalPlayer.TeleportToGround(float.Parse(ctx.Args["x"]) + float.Parse(ctx.Args["dx"]), float.Parse(ctx.Args["z"]));
ctx.Result = new { at = Dev.Abs(PlayerManager.PlayerTransform.position).ToString() };
