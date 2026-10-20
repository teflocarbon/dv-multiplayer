using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Items on a moving train: a player carries a mug into a loco cab and drops or throws it
    /// (before or while the train moves); the host pushes the loco along the track. On both peers
    /// the mug must stay in the cab, attached to the loco, at the same cab position, and not fall
    /// through. WHO=host needs a player host.
    /// </summary>
    [Scenario("train-items", "A mug dropped or thrown in a loco cab stays in the cab, attached to the loco at the same spot, on both peers while the host pushes the train")]
    public sealed class TrainItems : Scenario<TrainItems.Options>
    {
        public sealed class Options
        {
            /// <summary>Who carries the mug: client or host.</summary>
            public string Who = "client";
            /// <summary>drop or throw.</summary>
            public string Mode = "drop";
            /// <summary>When the mug is let go: stopped or moving.</summary>
            public string When = "stopped";
            /// <summary>m/s.</summary>
            public float Speed = 8;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Who == "host" && pair.Dedicated) throw new HarnessException("WHO=host needs a player host: a dedicated host has no player to carry the mug");
            Peer carrier = o.Who == "host" ? pair.Host : pair.Client;
            // Around the host's player, as the bash scenario did (on a dedicated host, its player
            // transform follows the client).
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            if (!pair.Dedicated) await pair.Host.Run(() => Players.Board(loco));
            await pair.Client.Run(() => Players.Board(loco));

            Running<object> push = o.When == "moving" ? pair.Host.Start(() => Cars.Push(loco, o.Speed, 6f, 3f), 90) : null;
            ushort mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", o.Who));
            float interior = await carrier.Run<float>(s => Items.CarryIntoCab(s, loco, mug, o.Mode, 90), 90);
            Report.Note($"{o.Who} {o.Mode}s mug {mug} in the cab ({o.When}); the cab interior was there after {interior:0.#} s");
            if (push != null) await push;
            else await pair.Host.Run(() => Cars.Push(loco, o.Speed, 6f, 3f), 90);
            await Task.Delay(3000);   // the last movement reaches the client

            PeerResults<ItemOnCar> on = await pair.Both.Run(() => Items.OnCar(mug, loco));
            ItemRecord record = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"host {on.Host.Local} client {on.Client.Local} record {record.Placement} parent {record.ParentNetId} (loco {on.Host.CarNetId})");
            foreach (var (peer, v) in on)
            {
                if (!Expect.That(v.Present, $"{peer}: has the mug")) continue;
                Expect.That(v.InCar, $"{peer}: mug attached to the loco", v.State);
                Expect.AtLeast(v.Local.Y, 0.5, $"{peer}: mug's height above the loco's origin (m), below is under the cab floor");
            }
            if (on.Host.Present && on.Client.Present)
                Expect.AtMost(Point.Distance(on.Host.Local, on.Client.Local), 0.3, "host/client cab positions apart (m)");
            if (record.Known)
                Expect.That(record.Placement == "TrainInterior" && record.ParentNetId == on.Host.CarNetId, "host record: in the loco's interior", record.ToString() + $" parent {record.ParentKind} {record.ParentNetId}");
        }
    }
}
