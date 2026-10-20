using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.World;
using UnityEngine;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.World;

/// <summary>
/// A client's side of hazmat tiles (see <see cref="HazmatSync"/>): applies the host's tiles with
/// their effects, and asks the host to set a tile alight.
/// </summary>
public static class HazmatClient
{
    // When each tile's ignition was last forwarded, so a lighter held over a spill doesn't send
    // every frame.
    private static readonly Dictionary<int, float> lastIgniteForward = new();

    /// <summary>True while the host's tiles are being applied: vanilla's own ignition runs then.</summary>
    public static bool ApplyingHostTiles { get; private set; }

    public static void Apply(byte[] data)
    {
        HazmatTileManager manager = HazmatSync.Manager;
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
                    using BinaryReader empty = new(new MemoryStream(HazmatSync.EmptyTileData));
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

    public static void ForwardIgnite(HazmatGridTile tile, float strength)
    {
        if (tile == null || tile.IsIgnited || tile.currentWeight <= float.Epsilon)
            return;
        float now = Time.realtimeSinceStartup;
        if (lastIgniteForward.TryGetValue(tile.gridPosition, out float last) && now - last < 1f)
            return;
        lastIgniteForward[tile.gridPosition] = now;
        NetworkClient.Instance?.SendHazmatIgnite(tile.gridPosition, strength);
    }
}
