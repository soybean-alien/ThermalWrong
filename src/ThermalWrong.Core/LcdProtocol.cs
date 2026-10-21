// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Buffers.Binary;
using System.Text;

namespace ThermalWrong.Core;

/// <summary>What the panel reports in its handshake reply, mapped the way TRCC's FormCZTVInit does.</summary>
public sealed record PanelInfo(int Pm, int Sub, int Width, int Height, int DefaultRotation, string Model)
{
    public static PanelInfo FromReply(byte[] reply)
    {
        int pm = reply[24], sub = reply[36];
        var (w, h) = pm switch
        {
            63 or 64 => (1600, 720),
            65 or 66 or 69 => (1920, 462),
            68 => (1280, 480),
            9 or 11 => (854, 480),
            10 or 16 => (960, 540),
            12 => (800, 480),
            13 or 17 or 18 => (960, 320),
            15 => (640, 172),
            32 => (320, 320),
            5 => (320, 240),
            7 or 14 => (640, 480),
            1 when sub == 48 => (1600, 720),
            1 when sub == 49 => (1920, 462),
            _ => (480, 480),
        };
        // TRCC's rotation at "direction 0" for the panels where it flips the image (FormCZTV.ImageToJpg).
        int rotation = (w, h) switch
        {
            (960, 540) when sub is 5 or 7 => 180,
            (1920, 462) when sub is < 2 or > 4 => 180,
            (1600, 720) when sub != 3 => 180,
            (854, 480) or (800, 480) when sub == 2 => 180,
            (960, 320) when sub >= 5 => 180,
            _ => 0,
        };
        string model = Encoding.ASCII.GetString(reply, 4, 8).TrimEnd('\0');
        return new PanelInfo(pm, sub, w, h, rotation, model);
    }

    public override string ToString() => $"{Model} {Width}x{Height} (pm {Pm}, sub {Sub})";
}

public static class LcdProtocol
{
    public const int HeaderSize = 64;

    /// <summary>The panel firmware rejects JPEGs this large; TRCC lowers quality until it fits.</summary>
    public const int MaxJpegBytes = 450_000;

    internal static void WriteMagic(Span<byte> b)
    {
        b[0] = 0x12; b[1] = 0x34; b[2] = 0x56; b[3] = 0x78;
    }

    /// <summary>Builds a picture packet: magic, cmd 2, width, height, byte 56 = 2, JPEG length at 60, then the JPEG.</summary>
    public static byte[] BuildPicturePacket(ReadOnlySpan<byte> jpeg, int width, int height)
    {
        var packet = new byte[HeaderSize + jpeg.Length];
        WritePictureHeader(packet, jpeg.Length, width, height);
        jpeg.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>Writes the 64-byte picture header for a JPEG of <paramref name="jpegLength"/> bytes that follows it.</summary>
    public static void WritePictureHeader(Span<byte> h, int jpegLength, int width, int height)
    {
        h[..HeaderSize].Clear();
        WriteMagic(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], height);
        h[56] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(h[60..], jpegLength);
    }

    /// <summary>The JPEG inside a picture packet.</summary>
    public static ReadOnlyMemory<byte> JpegOf(byte[] packet) => packet.AsMemory(HeaderSize);
}
