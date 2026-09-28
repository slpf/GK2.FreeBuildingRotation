using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GK2.FreeBuildingRotation;

internal sealed class ConveyorRotation
{
    private readonly List<(Component Component, FieldInfo Field, Direction Direction)> connectors = new();
    private readonly List<(ConveyorCellAutoBuilder Builder, int Index)> builders = new();
    private static readonly FieldInfo AutoIndex = AccessTools.Field(typeof(ConveyorCellAutoBuilder), "rotationIndex");

    internal static bool Supports(ConveyorElementType type)
    {
        return type == ConveyorElementType.None || type == ConveyorElementType.Workbench ||
            type == ConveyorElementType.Pallet || type == ConveyorElementType.Chest || type == ConveyorElementType.PowerSource;
    }

    internal static void Validate(WgoPart part)
    {
        foreach (BuildConnector connector in part.GetComponentsInChildren<BuildConnector>(true))
            if (connector.GetType() != typeof(BuildConnector) && connector is not ConveyorWorkbenchBuildConnector &&
                connector is not ConveyorChestBuildConnector && connector is not ConveyorPalletBuildConnector)
                throw new InvalidOperationException("Unsupported conveyor connector: " + connector.GetType().Name);
    }

    internal ConveyorRotation(WgoPart part)
    {
        foreach (BuildConnector connector in part.GetComponentsInChildren<BuildConnector>(true))
        {
            FieldInfo? direction = AccessTools.Field(connector.GetType(), "direction");
            if (direction?.FieldType == typeof(Direction))
                connectors.Add((connector, direction, (Direction)direction.GetValue(connector)));
        }
        foreach (ConveyorCellAutoBuilder builder in part.GetComponentsInChildren<ConveyorCellAutoBuilder>(true))
            builders.Add((builder, (int)AutoIndex.GetValue(builder)));
    }

    internal void Apply(int quarterTurns)
    {
        foreach (var connector in connectors)
            if (connector.Component != null)
                connector.Field.SetValue(connector.Component, BakedRotation.RotateDirection(connector.Direction, quarterTurns));
        foreach (var builder in builders)
            if (builder.Builder != null)
                AutoIndex.SetValue(builder.Builder, (builder.Index + quarterTurns) % 4);
    }
}
