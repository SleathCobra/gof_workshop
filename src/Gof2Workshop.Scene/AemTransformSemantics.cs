using System.Numerics;

namespace Gof2Workshop.Scene;

public static class AemTransformSemantics
{
    public static Vector3 ConvertScalarTranslation(Vector3 stored)
    {
        // AEM scalar translation curves are consumed by the engine as (X, Z, -Y).
        // Vector-packed curves are a separate storage form and are not remapped here.
        return new Vector3(stored.X, stored.Z, -stored.Y);
    }

    public static Quaternion CreateEngineRotation(Vector3 eulerRadians)
    {
        float halfX = eulerRadians.X * 0.5f;
        float halfY = eulerRadians.Y * 0.5f;
        float halfZ = eulerRadians.Z * 0.5f;
        float sinX = MathF.Sin(halfX);
        float sinY = MathF.Sin(halfY);
        float sinZ = MathF.Sin(halfZ);
        float cosX = MathF.Cos(halfX);
        float cosY = MathF.Cos(halfY);
        float cosZ = MathF.Cos(halfZ);

        Quaternion rotation = new(
            (sinX * cosZ * cosY) - (sinZ * sinY * cosX),
            (-sinY * cosZ * cosX) - (sinX * sinZ * cosY),
            (sinZ * cosY * cosX) - (sinX * sinY * cosZ),
            (cosZ * cosY * cosX) + (sinZ * sinY * sinX));
        return NormalizeOrIdentity(rotation);
    }

    public static Quaternion InterpolateEngineRotation(
        Quaternion previous,
        Quaternion next,
        float amount)
    {
        float clamped = Math.Clamp(amount, 0, 1);
        Quaternion blended = new(
            previous.X + ((next.X - previous.X) * clamped),
            previous.Y + ((next.Y - previous.Y) * clamped),
            previous.Z + ((next.Z - previous.Z) * clamped),
            previous.W + ((next.W - previous.W) * clamped));
        return NormalizeOrIdentity(blended);
    }

    private static Quaternion NormalizeOrIdentity(Quaternion value)
    {
        float lengthSquared = value.LengthSquared();
        return !float.IsFinite(lengthSquared) || lengthSquared < 1e-12f
            ? Quaternion.Identity
            : Quaternion.Normalize(value);
    }
}
