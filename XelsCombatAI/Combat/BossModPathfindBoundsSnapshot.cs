using System;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace XelsCombatAI.Combat;

internal sealed class BossModPathfindBoundsSnapshot
{
    private readonly object bounds;
    private readonly ConstructorInfo wdirConstructor;
    private readonly MethodInfo boundsContainsMethod;
    private readonly float centerX;
    private readonly float centerZ;
    private readonly object?[] constructorArguments = new object?[2];
    private readonly object?[] containsArguments = new object?[1];

    private BossModPathfindBoundsSnapshot(
        object bounds,
        ConstructorInfo wdirConstructor,
        MethodInfo boundsContainsMethod,
        float centerX,
        float centerZ)
    {
        this.bounds = bounds;
        this.wdirConstructor = wdirConstructor;
        this.boundsContainsMethod = boundsContainsMethod;
        this.centerX = centerX;
        this.centerZ = centerZ;
    }

    public static bool TryCreate(
        object hints,
        FieldInfo centerField,
        FieldInfo boundsField,
        FieldInfo wposXField,
        FieldInfo wposZField,
        ConstructorInfo wdirConstructor,
        MethodInfo boundsContainsMethod,
        out BossModPathfindBoundsSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            var center = centerField.GetValue(hints);
            var bounds = boundsField.GetValue(hints);
            if (center == null || bounds == null)
            {
                return false;
            }

            snapshot = new(
                bounds,
                wdirConstructor,
                boundsContainsMethod,
                Convert.ToSingle(wposXField.GetValue(center), CultureInfo.InvariantCulture),
                Convert.ToSingle(wposZField.GetValue(center), CultureInfo.InvariantCulture));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Contains(Vector3 candidate)
    {
        return this.Contains(new Vector2(candidate.X, candidate.Z));
    }

    public bool Contains(Vector2 candidate)
    {
        try
        {
            this.constructorArguments[0] = candidate.X - this.centerX;
            this.constructorArguments[1] = candidate.Y - this.centerZ;
            var offset = this.wdirConstructor.Invoke(this.constructorArguments);
            this.containsArguments[0] = offset;
            return this.boundsContainsMethod.Invoke(this.bounds, this.containsArguments) is true;
        }
        catch
        {
            return false;
        }
        finally
        {
            this.constructorArguments[0] = null;
            this.constructorArguments[1] = null;
            this.containsArguments[0] = null;
        }
    }
}
