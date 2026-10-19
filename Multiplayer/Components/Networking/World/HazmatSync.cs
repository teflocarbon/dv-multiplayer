using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DV.ThingTypes;
using DV.Utils;
using Multiplayer.Networking.TransportLayers;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Hazmat ground tiles (spilled liquids, fire, corrosion, biohazard, radiation on an 8 m grid)
/// are the host's. Every game used to run its own tile simulation, fed by its own copy of the
/// leaks, so fires and spills were in different places on each screen, and a player who joined
/// later saw none of them.
///
/// The host sends tiles whose contents or state changed visibly every half second (and every
/// tile on join), in vanilla's own tile format. Clients don't simulate tiles: they apply the
/// host's and spawn or remove the fire, corrosion and biohazard effects as the tiles change, as
/// vanilla does when loading a save. A client setting a tile alight asks the host to.
/// </summary>
public static class HazmatSync
{
    private const int SendIntervalTicks = NetworkLifecycle.TICK_RATE / 2;
    private const int TilesPerPacket = 256;
    // Liquid changes smaller than this (litres) aren't worth sending on their own; a burning
    // tile loses a little every frame.
    private const float LiquidQuantum = 20f;

    // Host: what each tile looked like when last sent (flags and quantised liquids).
    private static readonly Dictionary<int, byte[]> lastSent = new();

    // Client: when each tile's ignition was last forwarded, so a lighter held over a spill
    // doesn't send every frame.
    private static readonly Dictionary<int, float> lastIgniteForward = new();

    internal static bool ApplyingHostTiles;

    private static HazmatTileManager Manager
    {
        get
        {
            HazmatTileManager manager = SingletonBehaviour<HazmatTileManager>.Instance;
            return manager != null && manager.enabled ? manager : null;
        }
    }

    public static bool IsClient =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsClientRunning && !NetworkLifecycle.Instance.IsHost();

    public static void Reset()
    {
        lastSent.Clear();
        lastIgniteForward.Clear();
    }

    #region Host

    public static void Server_Tick(uint tick)
    {
        if (tick % SendIntervalTicks != 0)
            return;
        HazmatTileManager manager = Manager;
        if (manager == null)
            return;

        PutOutBurntTiles(manager);

        List<(int coord, HazmatGridTile tile)> changed = [];
        HashSet<int> present = [];
        foreach (KeyValuePair<int, HazmatGridTile> entry in manager.TileDictionary)
        {
            present.Add(entry.Key);
            byte[] key = VisibleState(entry.Value);
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
        foreach (byte[] chunk in Encode(changed, removed))
            NetworkLifecycle.Instance.Server.SendHazmatTiles(chunk, null);
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
    public static void Server_SendAll(ITransportPeer peer)
    {
        HazmatTileManager manager = Manager;
        if (manager == null || manager.TileDictionary.Count == 0)
            return;
        List<(int, HazmatGridTile)> all = manager.TileDictionary.Select(e => (e.Key, e.Value)).ToList();
        foreach (byte[] chunk in Encode(all, []))
            NetworkLifecycle.Instance.Server.SendHazmatTiles(chunk, peer);
    }

    public static void Server_Ignite(ServerPlayerIgnite request)
    {
        HazmatTileManager manager = Manager;
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

    public readonly struct ServerPlayerIgnite
    {
        public readonly int GridCoords;
        public readonly float Strength;
        public readonly Vector3 PlayerPosition;

        public ServerPlayerIgnite(int gridCoords, float strength, Vector3 playerPosition)
        {
            GridCoords = gridCoords;
            Strength = strength;
            PlayerPosition = playerPosition;
        }
    }

    private static byte[] VisibleState(HazmatGridTile tile)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)((tile.IsIgnited ? 1 : 0) | (tile.IsCorroded ? 2 : 0) | (tile.IsDefiled ? 4 : 0) | (tile.IsRadiated ? 8 : 0)));
        foreach (KeyValuePair<CargoType, float> liquid in tile.liquidContent.OrderBy(l => l.Key))
        {
            writer.Write((byte)liquid.Key);
            writer.Write(Mathf.RoundToInt(liquid.Value / LiquidQuantum));
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static IEnumerable<byte[]> Encode(List<(int coord, HazmatGridTile tile)> tiles, List<int> removed)
    {
        int index = 0;
        int removedIndex = 0;
        while (index < tiles.Count || removedIndex < removed.Count)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(0); // count, patched below
            int count = 0;
            for (; index < tiles.Count && count < TilesPerPacket; index++, count++)
            {
                writer.Write(tiles[index].coord);
                writer.Write(true);
                tiles[index].tile.SerializeData(writer);
            }
            for (; removedIndex < removed.Count && count < TilesPerPacket; removedIndex++, count++)
            {
                writer.Write(removed[removedIndex]);
                writer.Write(false);
            }
            writer.Flush();
            stream.Position = 0;
            writer.Write(count);
            writer.Flush();
            yield return stream.ToArray();
        }
    }

    #endregion

    #region Client

    // The data an empty tile carries in vanilla's format: no liquids, no flags.
    private static readonly byte[] EmptyTileData = BuildEmptyTileData();

    private static byte[] BuildEmptyTileData()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(byte.MaxValue); // end of liquids
        writer.Write((byte)0);       // flags
        writer.Write(0f);            // reaction modifier
        writer.Write((byte)0);       // wetness inhibition
        writer.Write(0f);            // burn time
        writer.Write(0f);            // radiation
        writer.Flush();
        return stream.ToArray();
    }

    public static void Client_Apply(byte[] data)
    {
        HazmatTileManager manager = Manager;
        if (manager == null || data == null || data.Length < 4)
            return;
        ApplyingHostTiles = true;
        try
        {
            using BinaryReader reader = new(new MemoryStream(data));
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int coord = reader.ReadInt32();
                bool present = reader.ReadBoolean();
                HazmatGridTile tile;
                if (present)
                {
                    tile = manager.GetTileFromCoords(coord, autoCreate: true);
                    if (tile == null)
                        return; // can't read the rest of this tile's data without one
                    ApplyTile(manager, tile, reader);
                }
                else if (manager.TileDictionary.TryGetValue(coord, out tile))
                {
                    using BinaryReader empty = new(new MemoryStream(EmptyTileData));
                    ApplyTile(manager, tile, empty);
                }
            }
            bool radiation = manager.TileDictionary.Values.Any(t => t.ContainsRadiation());
            if (radiation)
                manager.hazmatRadioactiveEffect.UpdateRadiationEffect();
            else
                manager.hazmatRadioactiveEffect.DisableRadiationEffect();
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"Applying the host's hazmat tiles failed: {e.Message}");
        }
        finally
        {
            ApplyingHostTiles = false;
        }
    }

    private static void ApplyTile(HazmatTileManager manager, HazmatGridTile tile, BinaryReader reader)
    {
        bool wasIgnited = tile.IsIgnited, wasCorroded = tile.IsCorroded, wasDefiled = tile.IsDefiled;
        // Vanilla's reader adds liquids rather than replacing them.
        tile.liquidContent.Clear();
        tile.DeserializeData(reader, 3);

        if (!wasIgnited && tile.IsIgnited)
            manager.BurnTerrainTile(tile);
        else if (wasIgnited && !tile.IsIgnited)
        {
            tile.terrainFireEffects?.RemoveEffects();
            manager.IgnitedTileCoords.Remove(tile.gridPosition);
        }
        if (!wasCorroded && tile.IsCorroded)
            manager.CorrodeTerrainTile(tile);
        else if (wasCorroded && !tile.IsCorroded)
            tile.terrainCorrosiveEffects?.RemoveEffects();
        if (!wasDefiled && tile.IsDefiled)
            manager.DefileTile(tile);
        else if (wasDefiled && !tile.IsDefiled)
            tile.terrainBiohazardEffects?.RemoveEffects();
    }

    public static void Client_ForwardIgnite(HazmatGridTile tile, float strength)
    {
        if (tile == null || tile.IsIgnited || tile.currentWeight <= float.Epsilon)
            return;
        float now = Time.realtimeSinceStartup;
        if (lastIgniteForward.TryGetValue(tile.gridPosition, out float last) && now - last < 1f)
            return;
        lastIgniteForward[tile.gridPosition] = now;
        NetworkLifecycle.Instance.Client?.SendHazmatIgnite(tile.gridPosition, strength);
    }

    #endregion
}
