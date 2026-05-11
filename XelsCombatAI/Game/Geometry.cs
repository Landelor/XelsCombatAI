using System;
using System.Numerics;

namespace XelsCombatAI.Game;

internal static class Geometry
{
    public static bool TryCalculateTargetDashDestination(Vector3 playerPosition, Vector3 targetPosition, float distanceToHitbox, out Vector3 destination)
    {
        var direction = targetPosition - playerPosition;
        direction.Y = 0;
        if (direction.LengthSquared() <= 0.0001f)
        {
            destination = default;
            return false;
        }

        direction = Vector3.Normalize(direction);
        destination = playerPosition + direction * Math.Max(0f, distanceToHitbox);
        return true;
    }

    public static float DistanceToHitbox(Vector3 from, float fromHitboxRadius, Vector3 to, float toHitboxRadius)
    {
        return Distance2D(from, to) - fromHitboxRadius - toHitboxRadius;
    }

    public static float Distance2D(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        delta.Y = 0;
        return delta.Length();
    }

    public static Vector3 RotationToDirection(float rotation)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return new Vector3(sin, 0f, cos);
    }

    public static float DirectionToRotation(Vector3 direction)
    {
        direction.Y = 0f;
        return direction.LengthSquared() <= 0.0001f
            ? 0f
            : NormalizeRadians(MathF.Atan2(direction.X, direction.Z));
    }

    public static float NormalizeRadians(float radians)
    {
        while (radians > MathF.PI)
        {
            radians -= MathF.Tau;
        }

        while (radians < -MathF.PI)
        {
            radians += MathF.Tau;
        }

        return radians;
    }

    public static float AbsAngleDelta(float from, float to)
    {
        return MathF.Abs(NormalizeRadians(to - from));
    }
}
