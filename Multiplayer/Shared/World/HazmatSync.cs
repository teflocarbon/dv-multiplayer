using System.Collections.Generic;
using System.IO;
using System.Linq;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

namespace Multiplayer.Shared.World;

/// <summary>
/// Hazmat ground tiles (spilled liquids, fire, corrosion, biohazard, radiation on an 8 m grid)
/// are the host's. Every game used to run its own tile simulation, fed by its own copy of the
/// leaks, so fires and spills were in different places on each screen, and a player who joined
/// later saw none of them.
///
/// The host sends tiles whose contents or state changed visibly every half second (and every
/// tile on join), in vanilla's own tile format (<c>Server/World/HazmatServer</c>). Clients don't
/// simulate tiles: they apply the host's and spawn or remove the fire, corrosion and biohazard
/// effects as the tiles change, as vanilla does when loading a save
/// (<c>Client/World/HazmatClient</c>). A client setting a tile alight asks the host to.
///
/// This is the part both use: the tile manager and the packet format.
/// </summary>
public static class HazmatSync
{
    private const int TilesPerPacket = 256;
    // Liquid changes smaller than this (litres) aren't worth sending on their own; a burning
    // tile loses a little every frame.
    private const float LiquidQuantum = 20f;

    public static HazmatTileManager Manager
    {
        get
        {
            HazmatTileManager manager = SingletonBehaviour<HazmatTileManager>.Instance;
            return manager != null && manager.enabled ? manager : null;
        }
    }

    /// <summary>What a tile looks like to players: its flags and its liquids, quantised.</summary>
    public static byte[] VisibleState(HazmatGridTile tile)
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

    /// <summary>
    /// Packet payloads for these tiles and removed tile coordinates: a count, then per tile its
    /// coordinate, whether it's present and, if so, vanilla's serialised tile.
    /// </summary>
    public static IEnumerable<byte[]> Encode(List<(int coord, HazmatGridTile tile)> tiles, List<int> removed)
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

    /// <summary>The data an empty tile carries in vanilla's format: no liquids, no flags.</summary>
    public static readonly byte[] EmptyTileData = BuildEmptyTileData();

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
}
