using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

internal readonly struct WorldItemCellCoord : IEquatable<WorldItemCellCoord>
{
    public const float Size = 128f;
    public readonly int X;
    public readonly int Z;

    public WorldItemCellCoord(int x, int z)
    {
        X = x;
        Z = z;
    }

    public static WorldItemCellCoord FromAbsolute(Vector3 position) =>
        new(Mathf.FloorToInt(position.x / Size), Mathf.FloorToInt(position.z / Size));

    public bool Equals(WorldItemCellCoord other) => X == other.X && Z == other.Z;
    public override bool Equals(object obj) => obj is WorldItemCellCoord other && Equals(other);
    public override int GetHashCode() => unchecked((X * 397) ^ Z);
    public override string ToString() => $"{X},{Z}";

    public static HashSet<WorldItemCellCoord> Neighbourhood(WorldItemCellCoord centre)
    {
        HashSet<WorldItemCellCoord> cells = new();
        for (int x = centre.X - 1; x <= centre.X + 1; x++)
        for (int z = centre.Z - 1; z <= centre.Z + 1; z++)
            cells.Add(new WorldItemCellCoord(x, z));
        return cells;
    }
}
