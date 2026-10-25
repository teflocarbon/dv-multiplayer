using System;
using System.Collections.Generic;
using Multiplayer.Core.Items.Projection;
using UnityEngine;

namespace Multiplayer.Server.Items.Projection;

/// <summary>A world cell of item interest; its size and radii are Core's <see cref="InterestCells"/>.</summary>
internal readonly struct WorldItemCellCoord : IEquatable<WorldItemCellCoord>
{
    public readonly int X;
    public readonly int Z;

    public WorldItemCellCoord(int x, int z)
    {
        X = x;
        Z = z;
    }

    public static WorldItemCellCoord FromAbsolute(Vector3 position) =>
        new(InterestCells.CellOf(position.x), InterestCells.CellOf(position.z));

    public bool Equals(WorldItemCellCoord other) => X == other.X && Z == other.Z;
    public override bool Equals(object obj) => obj is WorldItemCellCoord other && Equals(other);
    public override int GetHashCode() => unchecked((X * 397) ^ Z);
    public override string ToString() => $"{X},{Z}";

    public static HashSet<WorldItemCellCoord> Neighbourhood(WorldItemCellCoord centre, int radius = InterestCells.EnterRadius)
    {
        HashSet<WorldItemCellCoord> cells = new();
        for (int x = centre.X - radius; x <= centre.X + radius; x++)
        for (int z = centre.Z - radius; z <= centre.Z + radius; z++)
            cells.Add(new WorldItemCellCoord(x, z));
        return cells;
    }
}
