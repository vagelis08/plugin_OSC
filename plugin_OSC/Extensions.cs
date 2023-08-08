using System.Net.Sockets;
using System.Numerics;
using BuildSoft.OscCore;

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

public class OscClientPlus : OscClient
{
    /// <summary>Send a message with a string and a bool</summary>
    public void Send(string address, string message, bool value)
    {
        var boolTag = value ? "T" : "F";
        Writer.Reset();
        Writer.Write(address);
        var typeTags = $",s{boolTag}";
        Writer.Write(typeTags);
        Writer.Write(message);
        Socket.Send(Writer.Buffer, Writer.Length, SocketFlags.None);
    }

    public OscClientPlus(string ipAddress, int port) : base(ipAddress, port)
    {
    }
}