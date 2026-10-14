// Move the local player (args: dx, dz metres) away from where they stand, onto the ground there.
Vector3 now = Dev.Abs(PlayerManager.PlayerTransform.position);
yield return LocalPlayer.TeleportToGround(now.x + float.Parse(ctx.Args["dx"]), now.z + float.Parse(ctx.Args["dz"]));
ctx.Result = new { at = Dev.Abs(PlayerManager.PlayerTransform.position).ToString() };
