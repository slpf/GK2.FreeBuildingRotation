using System;
using UnityEngine;

namespace GK2.FreeBuildingRotation;

internal static class GridRotation
{
    internal static Vector3 GetLocalScale(int quarterTurns)
    {
        if (quarterTurns < 0 || quarterTurns > 3)
            throw new ArgumentOutOfRangeException(nameof(quarterTurns));
        if (quarterTurns % 2 == 0)
            return Vector3.one;
        Vector2 cell = BuildConsts.CELL_SIZE;
        if (!(cell.x > 0f) || !(cell.y > 0f) || float.IsInfinity(cell.x) || float.IsInfinity(cell.y))
            throw new InvalidOperationException("Invalid construction grid cell size.");
        return new Vector3(cell.y / cell.x, 1f, cell.x / cell.y);
    }

    internal static Vector3 Rotate(Vector3 value, int quarterTurns)
    {
        value = Vector3.Scale(value, GetLocalScale(quarterTurns));
        return quarterTurns switch
        {
            0 => value,
            1 => new Vector3(value.z, value.y, -value.x),
            2 => new Vector3(-value.x, value.y, -value.z),
            3 => new Vector3(-value.z, value.y, value.x),
            _ => throw new ArgumentOutOfRangeException(nameof(quarterTurns))
        };
    }

    internal static Vector3 RotateSize(Vector3 size, int quarterTurns)
    {
        Vector3 rotated = Rotate(size, quarterTurns);
        return new Vector3(Mathf.Abs(rotated.x), Mathf.Abs(rotated.y), Mathf.Abs(rotated.z));
    }
}
