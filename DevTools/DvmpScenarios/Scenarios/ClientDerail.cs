using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Client-only derail (the other session's finding #6): a physics hitch derails a car on one
    /// client while the host's stays on the rails. It used to stay derailed there for good, since
    /// bogie samples are ignored while derailed. The client must put it back on the host's track,
    /// where the host has it, still coupled, and the host's train must not be touched.
    /// </summary>
    [Scenario("client-derail", "A car derailed on the client only is put back on the host's track, still coupled, without touching the host's train")]
    [Budget(240)]
    public sealed class ClientDerail : Scenario<ClientDerail.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            Three t = await pair.Host.Run<Three>(s => Cars.SpawnThree(s, "BoxcarBrown"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, t.Cars)));
            await Task.Delay(3000);
            Report.Note($"the client derails {t.Last} (coupled to {t.Mid}) on its own");
            Require.That(await pair.Client.Run(() => Cars.Derail(t.Last)), "the test derail happened on the client");
            await Task.Delay(8000);
            Placed c = await pair.Client.Run(() => InGame.State(t.Last, t.Mid));
            Placed h = await pair.Host.Run(() => InGame.State(t.Last, t.Mid));
            float apart = Peers.Horizontal(c.Abs, h.Abs);
            Report.Note($"after 8 s: client derailed {c.Derailed}, coupled {c.Coupled}, on {c.Track}; host derailed {h.Derailed}, coupled {h.Coupled}; apart {apart:0.00} m");
            Expect.That(!h.Derailed && h.Coupled, "the host's train is unchanged (on the rails, coupled)", $"derailed {h.Derailed}, coupled {h.Coupled}");
            Expect.That(!c.Derailed, "back on the rails on the client");
            Expect.That(c.Coupled, "still coupled on the client");
            Expect.That(apart < 1, "the client's car where the host has it (under 1 m)", $"{apart:0.00} m");
        }

        public sealed class Placed
        {
            public bool Derailed, Coupled;
            public Abs Abs;
            public string Track;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Either peer: is the car derailed, is it in its neighbour's trainset, where is it.</summary>
            [Step]
            public static Placed State(string id, string other)
            {
                var car = Trains.Find(id);
                return new Placed { Derailed = car.derailed, Coupled = car.trainset == Trains.Find(other).trainset, Abs = Positions.Of(car.transform.position), Track = car.Bogies[0].track?.name ?? "-" };
            }
        }
    }
}
