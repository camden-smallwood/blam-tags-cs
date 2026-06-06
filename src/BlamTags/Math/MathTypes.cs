namespace BlamTags;

// The canonical Halo composite value types carried by TagFieldData.
// Immutable value types (records) — field order matches the on-wire layout.

/// <summary>A min/max pair.</summary>
public readonly record struct Bounds<T>(T Lower, T Upper);

/// <summary>Packed 8-bit RGB color (one u32).</summary>
public readonly record struct RgbColor(uint Packed);

/// <summary>Packed 8-bit ARGB color (one u32).</summary>
public readonly record struct ArgbColor(uint Packed);

public readonly record struct Point2d(short X, short Y);

public readonly record struct Rectangle2d(short Top, short Left, short Bottom, short Right);

public readonly record struct RealPoint2d(float X, float Y);

public readonly record struct RealPoint3d(float X, float Y, float Z);

public readonly record struct RealVector2d(float I, float J);

public readonly record struct RealVector3d(float I, float J, float K);

public readonly record struct RealQuaternion(float I, float J, float K, float W);

public readonly record struct RealEulerAngles2d(float Yaw, float Pitch);

public readonly record struct RealEulerAngles3d(float Yaw, float Pitch, float Roll);

public readonly record struct RealPlane2d(float I, float J, float D);

public readonly record struct RealPlane3d(float I, float J, float K, float D);

public readonly record struct RealRgbColor(float Red, float Green, float Blue);

public readonly record struct RealArgbColor(float Alpha, float Red, float Green, float Blue);

public readonly record struct RealHsvColor(float Hue, float Saturation, float Value);

public readonly record struct RealAhsvColor(float Alpha, float Hue, float Saturation, float Value);
