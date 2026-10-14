// Soak bot: performs random item interactions around the player's starting point.
// Args: seed (int), actions (int), radius (metres), pause (seconds between actions),
// filter (optional: only items whose name starts with it, e.g. "JobOverview").
// Every action is logged to ctx output so a failing desync can be traced back to it.
using Multiplayer.Components.Networking.World;
using DV.Interaction;

public static class Probe
{
    public static object Run(DevContext ctx) => Routine(ctx);

    private static IEnumerator Routine(DevContext ctx)
    {
        int seed = int.Parse(Arg(ctx, "seed", "1"));
        string filter = Arg(ctx, "filter", string.Empty); // only items whose name starts with this
        int actions = int.Parse(Arg(ctx, "actions", "20"));
        float radius = float.Parse(Arg(ctx, "radius", "40"), System.Globalization.CultureInfo.InvariantCulture);
        float pause = float.Parse(Arg(ctx, "pause", "1"), System.Globalization.CultureInfo.InvariantCulture);
        System.Random rng = new System.Random(seed);
        Vector3 anchor = Dev.PlayerAbs ?? throw new Exception("no local player");
        int ok = 0, failed = 0;

        for (int i = 0; i < actions; i++)
        {
            // Never start an action while holding something from a failed previous one.
            if (Hands.Held != null) yield return Steps.Try(Hands.Drop(), _ => { });

            string action = Pick(rng, ("carry", 4), ("throw", 3), ("drop-here", 2), ("wander", 1));
            NetworkedItem item = action == "wander" ? null : RandomLooseItem(rng, anchor, radius, filter);
            string error = null;

            if (action != "wander" && item == null)
            {
                ctx.Log(new { i, action, skipped = "no loose item in range" });
                continue;
            }

            switch (action)
            {
                case "carry":
                    yield return Steps.Try(Carry(item, rng), e => error = e.Message);
                    break;
                case "throw":
                    yield return Steps.Try(Throw(item, rng), e => error = e.Message);
                    break;
                case "drop-here":
                    yield return Steps.Try(PickAndDrop(item), e => error = e.Message);
                    break;
                case "wander":
                    Vector3 offset = new Vector3((float)(rng.NextDouble() * 2 - 1), 0, (float)(rng.NextDouble() * 2 - 1)) * radius * 0.5f;
                    yield return Steps.Try(LocalPlayer.TeleportAbs(anchor + offset, 0.5f), e => error = e.Message);
                    break;
            }

            if (error == null) ok++; else failed++;
            ctx.Log(new
            {
                i,
                action,
                id = item != null ? (int?)item.NetId : null,
                name = item != null ? item.name.Replace("(Clone)", "") : null,
                error,
                at = item != null ? Dev.Abs(item.transform.position).ToString("F1") : null
            });
            yield return new WaitForSeconds(pause);
        }

        if (Hands.Held != null) yield return Steps.Try(Hands.Drop(), _ => { });
        Hands.ReleaseControl();
        ctx.Result = new { seed, actions, ok, failed };
    }

    private static IEnumerator Carry(NetworkedItem item, System.Random rng)
    {
        yield return LocalPlayer.GoTo(item);
        yield return Hands.Pickup(item.NetId);
        Vector3 step = new Vector3((float)(rng.NextDouble() * 2 - 1), 0, (float)(rng.NextDouble() * 2 - 1)).normalized * (2 + (float)rng.NextDouble() * 4);
        yield return LocalPlayer.TeleportAbs(Dev.PlayerAbs.Value + step, 0.5f);
        yield return Hands.Drop();
        yield return new WaitForSeconds(2);
    }

    private static IEnumerator Throw(NetworkedItem item, System.Random rng)
    {
        yield return LocalPlayer.GoTo(item);
        yield return Hands.Pickup(item.NetId);
        Vector3 direction = new Vector3((float)(rng.NextDouble() * 2 - 1), 0.3f + (float)rng.NextDouble() * 0.7f, (float)(rng.NextDouble() * 2 - 1));
        yield return Hands.Throw(direction);
        yield return new WaitForSeconds(4); // let it land and settle
    }

    private static IEnumerator PickAndDrop(NetworkedItem item)
    {
        yield return LocalPlayer.GoTo(item);
        yield return Hands.Pickup(item.NetId);
        yield return new WaitForSeconds(0.5f);
        yield return Hands.Drop();
        yield return new WaitForSeconds(2);
    }

    private static NetworkedItem RandomLooseItem(System.Random rng, Vector3 anchor, float radius, string filter)
    {
        var candidates = Dev.All<NetworkedItem>()
            .Where(n => n.NetId != 0 && n.gameObject.activeInHierarchy && n.GetItemState() == ItemState.Dropped
                        && n.GetComponent<GrabHandlerItem>() != null && TrainCar.Resolve(n.transform) == null
                        && (filter.Length == 0 || n.name.StartsWith(filter))
                        && Vector3.Distance(Dev.Abs(n.transform.position), anchor) <= radius)
            .OrderBy(n => n.NetId)
            .ToList();
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    private static string Pick(System.Random rng, params (string Name, int Weight)[] options)
    {
        int roll = rng.Next(options.Sum(o => o.Weight));
        foreach (var option in options)
        {
            if (roll < option.Weight) return option.Name;
            roll -= option.Weight;
        }
        return options[0].Name;
    }

    private static string Arg(DevContext ctx, string key, string fallback) =>
        ctx.Args.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : fallback;
}
