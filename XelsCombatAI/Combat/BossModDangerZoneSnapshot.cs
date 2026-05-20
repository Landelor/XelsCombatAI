using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

namespace XelsCombatAI.Combat;

internal sealed class BossModDangerZoneSnapshot
{
    private readonly ShapeEntry[] shapes;
    private readonly ConstructorInfo wposConstructor;
    private readonly object?[] constructorArguments = new object?[2];
    private readonly object?[] containsArguments = new object?[1];

    private BossModDangerZoneSnapshot(ShapeEntry[] shapes, ConstructorInfo wposConstructor)
    {
        this.shapes = shapes;
        this.wposConstructor = wposConstructor;
    }

    public bool HasDangerZones => this.shapes.Length > 0;

    public static bool TryCreate(
        object hints,
        FieldInfo forbiddenZonesField,
        FieldInfo temporaryObstaclesField,
        ConstructorInfo wposConstructor,
        out BossModDangerZoneSnapshot? snapshot)
    {
        snapshot = null;
        var wposType = wposConstructor.DeclaringType;
        if (wposType == null)
        {
            return false;
        }

        var shapes = new List<ShapeEntry>();
        CollectForbiddenZoneShapes(forbiddenZonesField.GetValue(hints), wposType, shapes);
        CollectShapeDistances(temporaryObstaclesField.GetValue(hints), wposType, shapes);
        snapshot = new(shapes.ToArray(), wposConstructor);
        return true;
    }

    public bool Contains(Vector2 candidate)
    {
        if (this.shapes.Length == 0)
        {
            return false;
        }

        try
        {
            this.constructorArguments[0] = candidate.X;
            this.constructorArguments[1] = candidate.Y;
            var wpos = this.wposConstructor.Invoke(this.constructorArguments);
            foreach (var shape in this.shapes)
            {
                this.containsArguments[0] = wpos;
                if (shape.ContainsMethod.Invoke(shape.ShapeDistance, this.containsArguments) is true)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
        finally
        {
            this.constructorArguments[0] = null;
            this.constructorArguments[1] = null;
            this.containsArguments[0] = null;
        }
    }

    private static void CollectForbiddenZoneShapes(object? zones, Type wposType, ICollection<ShapeEntry> shapes)
    {
        if (zones is not IEnumerable enumerable)
        {
            return;
        }

        foreach (var zone in enumerable)
        {
            var shape = zone?.GetType().GetField("Item1", BindingFlags.Instance | BindingFlags.Public)?.GetValue(zone);
            TryAddShape(shape, wposType, shapes);
        }
    }

    private static void CollectShapeDistances(object? shapeDistances, Type wposType, ICollection<ShapeEntry> shapes)
    {
        if (shapeDistances is not IEnumerable enumerable)
        {
            return;
        }

        foreach (var shape in enumerable)
        {
            TryAddShape(shape, wposType, shapes);
        }
    }

    private static void TryAddShape(object? shapeDistance, Type wposType, ICollection<ShapeEntry> shapes)
    {
        if (shapeDistance == null)
        {
            return;
        }

        var containsMethod = ResolveContainsMethod(shapeDistance.GetType(), wposType);
        if (containsMethod != null)
        {
            shapes.Add(new(shapeDistance, containsMethod));
        }
    }

    private static MethodInfo? ResolveContainsMethod(Type shapeType, Type wposType)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var current = shapeType; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMethods(Flags))
            {
                if (method.Name != "Contains" || method.ReturnType != typeof(bool))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }

                var parameterType = parameters[0].ParameterType;
                if (parameterType == wposType || parameterType.IsByRef && parameterType.GetElementType() == wposType)
                {
                    return method;
                }
            }
        }

        return null;
    }

    private readonly record struct ShapeEntry(object ShapeDistance, MethodInfo ContainsMethod);
}
