using System.Collections.Generic;
using System.Linq;
using DV.ThingTypes;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;
using Multiplayer.Shared.World;
using UnityEngine;

namespace Multiplayer.Server.World;

/// <summary>
/// The host's side of hazmat tiles (see <see cref="HazmatSync"/>): sends the tiles that changed
/// every half second and every tile to a joining player, puts out tiles vanilla leaves burning,
/// and sets a tile alight for a client in reach.
/// </summary>
public static class HazmatServer
{
    private const int SendIntervalTicks = NetworkLifecycle.TICK_RATE / 2;

    // What each tile looked like when last sent (flags and quantised liquids).
    private static readonly Dictionary<int, byte[]> lastSent = new();

    private static NetworkServer Server => NetworkServer.Instance;

    public static void Reset() => lastSent.Clear();

    public static void Tick(uint tick)
    {
        if (tick % SendIntervalTicks != 0)
            return;
        HazmatTileManager manager = HazmatSync.Manager;
        if (manager == null)
            return;

        PutOutBurntTiles(manager);

        List<(int coord, HazmatGridTile tile)> changed = [];
        HashSet<int> present = [];
        foreach (KeyValuePair<int, HazmatGridTile> entry in manager.TileDictionary)
        {
            present.Add(entry.Key);
            byte[] key = HazmatSync.VisibleState(entry.Value);
            if (lastSent.TryGetValue(entry.Key, out byte[] previous) && previous.SequenceEqual(key))
                continue;
            lastSent[entry.Key] = key;
            changed.Add((entry.Key, entry.Value));
        }
        List<int> removed = lastSent.Keys.Where(coord => !present.Contains(coord)).ToList();
        foreach (int coord in removed)
            lastSent.Remove(coord);

        if (changed.Count == 0 && removed.Count == 0)
            return;
        foreach (byte[] chunk in HazmatSync.Encode(changed, removed))
            Server.SendHazmatTiles(chunk, null);
    }

    // Vanilla can leave ground fire burning forever (a patch burnt 20 minutes with no leak,
    // 2026-10-17). A tile only burns the liquids in its private flammables list, and a burning
    // tile's list isn't rebuilt while its burning neighbours keep raising its reaction:
    //  - once it burns down to a trace (2.7e-6 L of diesel) it weighs next to nothing and the list
    //    is rebuilt empty, yet with a liquid left it doesn't count as empty, so it stays alight;
    //  - liquid flowing in from neighbours (33 L) then never burns either.
    // Rebuild the list where it's empty, and clear traces, and vanilla burns the fuel off and puts
    // the tile out as it does an empty one. Tried on the live server: all 31 tiles went out.
    private const float BurntOutLitres = 0.01f;
    private static readonly System.Reflection.FieldInfo FlammablesField =
        typeof(HazmatGridTile).GetField("flammables", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void PutOutBurntTiles(HazmatTileManager manager)
    {
        foreach (HazmatGridTile tile in manager.TileDictionary.Values)
        {
            if (tile == null || !tile.IsIgnited || tile.liquidContent.Count == 0)
                continue;
            if (tile.liquidContent.Values.All(litres => litres <= BurntOutLitres))
            {
                foreach (CargoType trace in tile.liquidContent.Keys.ToList())
                    tile.RemoveLiquidFromTile(trace);
                continue;
            }
            if (FlammablesField?.GetValue(tile) is System.Collections.IList { Count: 0 } &&
                tile.liquidContent.Keys.Any(TrainCarAndCargoDamageProperties.IsCargoFlammableLiquid))
                tile.ReCalculateReactionValues();
        }
    }

    // A joining player gets every tile.
    public static void SendAll(ITransportPeer peer)
    {
        HazmatTileManager manager = HazmatSync.Manager;
        if (manager == null || manager.TileDictionary.Count == 0)
            return;
        List<(int, HazmatGridTile)> all = manager.TileDictionary.Select(e => (e.Key, e.Value)).ToList();
        foreach (byte[] chunk in HazmatSync.Encode(all, []))
            Server.SendHazmatTiles(chunk, peer);
    }

    public static void Ignite(PlayerIgnite request)
    {
        HazmatTileManager manager = HazmatSync.Manager;
        if (manager == null || !manager.TileDictionary.ContainsKey(request.GridCoords))
            return;
        Vector3 tilePosition = manager.GetWorldPositionFromGridCoords(manager.GetTileCoordX(request.GridCoords),
            manager.GetTileCoordY(request.GridCoords), usingWorldShift: false);
        // An igniter works at arm's length; allow for the player moving while the request travels.
        Vector2 horizontal = new(tilePosition.x - request.PlayerPosition.x, tilePosition.z - request.PlayerPosition.z);
        if (horizontal.sqrMagnitude > 40f * 40f)
            return;
        manager.IgniteTile(request.GridCoords, Mathf.Clamp(request.Strength, 0f, 100000f));
    }

    public readonly struct PlayerIgnite
    {
        public readonly int GridCoords;
        public readonly float Strength;
        public readonly Vector3 PlayerPosition;

        public PlayerIgnite(int gridCoords, float strength, Vector3 playerPosition)
        {
            GridCoords = gridCoords;
            Strength = strength;
            PlayerPosition = playerPosition;
        }
    }
}
