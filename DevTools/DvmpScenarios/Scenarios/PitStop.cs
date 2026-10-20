using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Multiplayer.Server.Stations;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Service stations (pit stops and their plugs) with both players at one. One player pulls a
    /// resource lever and lets it go, then picks up a plug and puts it back home. The other game must
    /// show the lever held by someone else while it's pulled and free after, and the plug held by
    /// that player and then nobody, and the server must record who holds the plug. With WHO=host this
    /// is the host player's own requests, which the server handles in-process
    /// (docs/design/host-local-session.md, stage 2).
    /// MODE=yank (WHO=client only): instead of putting the plug back, the client walks off with it.
    /// Its avatar carries the plug on the host until the hose is stretched, and the server's rope
    /// physics yanks it out of the hand (Server/Stations/PropHoseRopePatch): the server has nobody
    /// holding it, the client's plug was moved by the yank the host sent, and both games agree
    /// whether it ended up snapped home.
    /// Every run starts from the plug at home and held by nobody: one that isn't (a yank run before
    /// left it on the ground, a failed run left it held) is docked home by the server, which tells the
    /// client, as when the hose snaps it home.
    /// </summary>
    [Scenario("pit-stop", "A player works a service station's lever and plug: the other game sees the lever and plug held and let go, and the server records who holds the plug (MODE=yank: the hose yanks it from the client)")]
    [Budget(300)]
    public sealed class PitStop : Scenario<PitStop.Options>
    {
        public sealed class Options
        {
            /// <summary>Who acts: client or host.</summary>
            public string Who = "client";
            /// <summary>dock: the plug goes back home; yank: the client walks off with it.</summary>
            public string Mode = "dock";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Mode == "yank" && o.Who == "host") throw new HarnessException("MODE=yank needs the client's avatar to carry the plug (WHO=client)");
            Peer who = Places.Player(pair, o.Who, "WHO"), other = who == pair.Host ? pair.Client : pair.Host;
            string otherName = other.Name;

            await Places.Shop(pair, 3f);
            Found f = await pair.Host.Run(() => InGame.Find(Places.ShopX, Places.ShopZ));
            Report.Note($"station {f.Name}: plug {f.Plug}, lever for resource {f.Resource}");
            // The one who acts stands at the plug: the server only lets a player within reach (4 m) pick
            // it up, measured from where that player last said it was. A dedicated host's parked body
            // stays where it is: the station is kept loaded on the host while a player is near it.
            float hx = o.Who == "host" ? 1 : -3, hz = o.Who == "host" ? 1 : 3, cx = o.Who == "host" ? -3 : 1, cz = o.Who == "host" ? 3 : 1;
            await Places.Both(pair, f.X + hx, f.Z + hz, f.X + cx, f.Z + cz, 2f);

            // The client gets the station's state when it comes into range.
            await Until.True(async () => (await pair.Client.Run(() => InGame.State(f.Station, f.Plug, f.Resource))).Refreshed, 30, "the client got the station's state");
            Require.That((await pair.Client.Run(() => InGame.State(f.Station, f.Plug, f.Resource))).Found, "the client has the station and plug");
            Require.That((await pair.Host.Run(() => InGame.State(f.Station, f.Plug, f.Resource))).Found, "the host has the station and plug");
            if (await pair.Host.Run(() => InGame.SendHome(f.Plug)))
            {
                Report.Note("the plug wasn't home and free: the server docks it home");
                await Until.True(async () =>
                {
                    PeerResults<State> st = await pair.Both.Run(() => InGame.State(f.Station, f.Plug, f.Resource));
                    return st.Host.AtHome && st.Host.HeldBy == 0 && st.Client.AtHome && st.Client.Holding == 0;
                }, 30, "both games have the plug home and held by nobody");
            }
            int me = (await who.Run(() => InGame.State(f.Station, f.Plug, f.Resource))).Me;

            Report.Note($"{o.Who} pulls the lever");
            await who.Run(() => InGame.Lever(f.Station, f.Resource, 1));
            await Task.Delay(2000);
            Expect.That((await other.Run(() => InGame.State(f.Station, f.Plug, f.Resource))).RemoteGrabbed, $"{otherName} sees the lever held by another player");
            Report.Note($"{o.Who} lets it go");
            await who.Run(() => InGame.Lever(f.Station, f.Resource, 0));
            await Task.Delay(2000);
            Expect.That(!(await other.Run(() => InGame.State(f.Station, f.Plug, f.Resource))).RemoteGrabbed, $"{otherName} sees the lever free");

            Report.Note($"{o.Who} picks the plug up");
            Report.Note($"{o.Who} grabbed it: " + await who.Run(() => InGame.Grab(f.Plug)));
            await Task.Delay(2000);
            State h = await pair.Host.Run(() => InGame.State(f.Station, f.Plug, f.Resource)), s = await other.Run(() => InGame.State(f.Station, f.Plug, f.Resource));
            Report.Note($"server: held by P{h.HeldBy}; {otherName} shows P{s.Holding} holding it ({o.Who} is P{me})");
            Expect.Equal(me, h.HeldBy, "the player the server has holding the plug");
            if (other == pair.Client) Expect.Equal(me, s.Holding, "the player the client shows holding the plug");

            if (o.Mode == "yank")
            {
                var watch = pair.Both.Start<Yanked>(y => InGame.Watch(y, f.Plug, 14f), 60);
                await Task.Delay(1000);
                Report.Note("client walks off with the plug");
                await pair.Client.Run(() => Players.Visit(f.X + 35f, f.Z + 35f, 0f), 60);
                PeerResults<Yanked> w = await watch;
                Report.Note($"furthest from home: host {w.Host.MaxAway} m, client {w.Client.MaxAway} m (the hose yanks at {w.Host.Limit} m from its origin); yank settle seen: host {w.Host.SettleSeen}, client {w.Client.SettleSeen}");
                Report.Note($"at the end: host {w.Host.Away} m from home (at home: {w.Host.AtHome}), client {w.Client.Away} m (at home: {w.Client.AtHome}); server: held by P{w.Host.HeldBy}");
                Expect.Zero(w.Host.HeldBy, "the player the server has holding the plug after the client walked off");
                Expect.That(w.Host.MaxAway > 1.5, "the host's plug left home (the client's avatar carried it)", $"{w.Host.MaxAway} m, expected over 1.5");
                Expect.That(w.Client.MaxAway > 1.5, "the client's plug left home (the host's yank reached it)", $"{w.Client.MaxAway} m, expected over 1.5");
                Expect.Equal(w.Host.AtHome, w.Client.AtHome, "the client agrees with the host whether the plug is home");
                return;
            }

            Report.Note($"{o.Who} puts it back home");
            await who.Run(() => InGame.Dock(f.Plug));
            await Task.Delay(2000);
            h = await pair.Host.Run(() => InGame.State(f.Station, f.Plug, f.Resource));
            s = await other.Run(() => InGame.State(f.Station, f.Plug, f.Resource));
            Report.Note($"server: held by P{h.HeldBy}; {otherName} shows P{s.Holding}");
            Expect.Zero(h.HeldBy, "the player the server has holding the plug once docked");
            Expect.Zero(s.Holding, $"the player {otherName} shows holding the plug once docked");
        }

        public sealed class Found
        {
            public ushort Plug, Station;
            public int Resource;
            public string Name;
            public float X, Z;
        }

        public sealed class State
        {
            /// <summary>This game has the station and plug.</summary>
            public bool Found;
            /// <summary>Neither the station nor the plug is still waiting for the server's state.</summary>
            public bool Refreshed;
            /// <summary>The server's holder of the plug, and the player this game shows holding it.</summary>
            public int HeldBy, Holding;
            /// <summary>The lever is held by another player here.</summary>
            public bool RemoteGrabbed;
            /// <summary>The plug is plugged into its own stand here.</summary>
            public bool AtHome;
            /// <summary>This game's own player id (0 on a dedicated host).</summary>
            public int Me;
        }

        public sealed class Yanked
        {
            public bool Found, SettleSeen, AtHome;
            public float MaxAway, Away, X, Z, Limit;
            public int HeldBy;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: the plug nearest an absolute x/z that belongs to a station with a resource lever, and
            /// where its home is (players stand by that: the plug may not be there yet).
            /// </summary>
            [Step]
            public static Found Find(float x, float z)
            {
                float Flat(Vector3 v) => new Vector2(v.x - x, v.z - z).magnitude;
                var plug = Dev.All<NetworkedPluggableObject>()
                    .Where(n => n != null && n.Station != null && n.Station.leverStateLookup.Count > 0 && n.PluggableObject != null && n.PluggableObject.startAttachedTo != null)
                    .OrderBy(n => Flat(Dev.Abs(n.transform.position))).First();
                var abs = Dev.Abs(plug.PluggableObject.GetSnappedWorldPositionFor(plug.PluggableObject.startAttachedTo));
                return new Found { Plug = plug.NetId, Station = plug.Station.NetId, Resource = (int)plug.Station.leverStateLookup.Keys.First(), Name = plug.Station.StationName, X = abs.x, Z = abs.z };
            }

            private static (NetworkedPitStopStation station, NetworkedPluggableObject plug) Get(ushort station, ushort plug)
            {
                NetworkedPitStopStation.Get(station, out NetworkedPitStopStation s);
                NetworkedPluggableObject.Get(plug, out NetworkedPluggableObject p);
                return (s, p);
            }

            /// <summary>The lever's handler, as its RotaryAmplitudeChecker raises it when pulled (1) and let go (0).</summary>
            [Step]
            public static void Lever(ushort station, int resource, int value)
            {
                NetworkedPitStopStation.Get(station, out NetworkedPitStopStation s);
                s.leverStateLookup[(DV.ThingTypes.ResourceType)resource].leverHandler(value);
            }

            /// <summary>The plug's handler, as DV raises it when it's picked up. Result: the plug's state then.</summary>
            [Step]
            public static string Grab(ushort plug)
            {
                NetworkedPluggableObject.Get(plug, out NetworkedPluggableObject p);
                // What the handler checks before it takes the grab (it drops one while either of the first two
                // is set), and whether its LateUpdate will send it (not while processing as host).
                string state = $"waiting for the server: {p.WaitingForServerState}, processing a packet: {NetworkLifecycle.Instance.IsProcessingPacket}, processing as host: {p.ProcessingAsHost}";
                p.OnGrabbed(null);
                return state;
            }

            /// <summary>The plug's handler, as DV raises it when it's put back home.</summary>
            [Step]
            public static void Dock(ushort plug)
            {
                NetworkedPluggableObject.Get(plug, out NetworkedPluggableObject p);
                p.OnPluggedIn(p.PluggableObject, p.PluggableObject.startAttachedTo);
            }

            /// <summary>
            /// Host: unless the plug is home and held by nobody, docks it home through the server, which
            /// tells the players at the station (the path the hose's snap home takes). True if it had to.
            /// </summary>
            [Step]
            public static bool SendHome(ushort plug)
            {
                NetworkedPluggableObject.Get(plug, out NetworkedPluggableObject p);
                if (p.HeldByPlayerId == 0 && p.playerHolding == 0 && AtHome(p.PluggableObject))
                    return false;
                ServerPluggableObject.SnappedByRope(p);
                return true;
            }

            private static bool AtHome(PluggableObject plug) => plug.State == PluggableObject.PluggableState.PluggedIn && plug.Socket == plug.startAttachedTo;

            [Step]
            public static State State(ushort station, ushort plug, int resource)
            {
                var (s, p) = Get(station, plug);
                if (s == null || p == null) return new State();
                return new State
                {
                    Found = true,
                    Refreshed = !s.WaitingForServerState && !p.WaitingForServerState,
                    HeldBy = p.HeldByPlayerId,
                    Holding = p.playerHolding,
                    AtHome = AtHome(p.PluggableObject),
                    RemoteGrabbed = s.isResourceRemoteGrabbedDict.TryGetValue((DV.ThingTypes.ResourceType)resource, out bool g) && g,
                    Me = NetworkLifecycle.Instance.LocalSession?.PlayerId ?? 0
                };
            }

            /// <summary>
            /// Either peer: watches the plug every 0.1 s for <paramref name="seconds"/>: how far it got
            /// from its home socket, whether the yank's settle was seen (vanilla's yankOutOfHand cleared
            /// while it lands), and at the end where it is, how far from home, whether it's plugged in at
            /// home, and the server's holder.
            /// </summary>
            [Step]
            public static IEnumerator Watch(Step<Yanked> step, ushort plugId, float seconds)
            {
                NetworkedPluggableObject.Get(plugId, out NetworkedPluggableObject netPlug);
                if (netPlug == null || netPlug.PluggableObject == null || netPlug.PluggableObject.startAttachedTo == null)
                {
                    step.Result = new Yanked();
                    yield break;
                }
                var plug = netPlug.PluggableObject;
                float Away() => Vector3.Distance(plug.transform.position, plug.GetSnappedWorldPositionFor(plug.startAttachedTo));
                float maxAway = 0f;
                bool settleSeen = false;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
                {
                    maxAway = Math.Max(maxAway, Away());
                    settleSeen |= !plug.yankOutOfHand;
                    yield return new WaitForSecondsRealtime(0.1f);
                }
                var abs = Dev.Abs(plug.transform.position);
                step.Result = new Yanked
                {
                    Found = true,
                    MaxAway = (float)Math.Round(maxAway, 2),
                    SettleSeen = settleSeen,
                    Away = (float)Math.Round(Away(), 2),
                    AtHome = AtHome(plug),
                    X = abs.x, Z = abs.z,
                    HeldBy = netPlug.HeldByPlayerId,
                    // How far from the hose origin a held plug is yanked.
                    Limit = netPlug.Hose == null ? -1 : (float)Math.Round(RopeLength(netPlug.Hose) * netPlug.Hose.maxLengthMultiplier, 2)
                };
            }

            // The hose's rope.ropeParams.ropeLength, by reflection: the rope's type is in VerletRope.dll,
            // which the game assemblies CI builds against don't include. -1 if it isn't there.
            private static float RopeLength(object hose)
            {
                object Member(object o, string name)
                {
                    if (o == null) return null;
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var t = o.GetType();
                    return t.GetField(name, flags)?.GetValue(o) ?? t.GetProperty(name, flags)?.GetValue(o);
                }
                return Member(Member(Member(hose, "rope"), "ropeParams"), "ropeLength") is float length ? length : -1;
            }
        }
    }
}
