using System;
using System.Numerics;

namespace plugin_OSC;

public static class NumericExtensions
{
    private const float RAD2DEG = (float)(180.0 / Math.PI);

    public static Vector3 ToEulerAngles(Quaternion q)
    {
        Vector3 angles = new();

        // roll / x
        var sinr_cosp = 2.0 * (q.W * q.X + q.Y * q.Z);
        var cosr_cosp = 1.0 - 2.0 * (q.X * q.X + q.Y * q.Y);
        angles.X = (float)Math.Atan2(sinr_cosp, cosr_cosp);

        // pitch / y
        var sinp = 2.0 * (q.W * q.Y - q.Z * q.X);
        if (Math.Abs(sinp) >= 1.0)
            angles.Y = (float)Math.CopySign(Math.PI / 2 / 0, sinp);
        else
            angles.Y = (float)Math.Asin(sinp);

        // yaw / z
        var siny_cosp = 2.0 * (q.W * q.Z + q.X * q.Y);
        var cosy_cosp = 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z);
        angles.Z = (float)Math.Atan2(siny_cosp, cosy_cosp);

        return angles * RAD2DEG;
    }
}