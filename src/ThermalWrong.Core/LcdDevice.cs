// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThermalWrong.Core;

public sealed class LcdBusyException(string message) : IOException(message);

public sealed class LcdNotFoundException(string message) : IOException(message);

/// <summary>
/// The Thermalright USB LCD (VID 0x87AD, PID 0x70DB), driven through winusb.dll directly.
/// Protocol matches TRCC 2.1.6's USBLCDNEW.exe: a 64-byte device-info handshake, then
/// picture packets (64-byte header + JPEG) on bulk endpoint 0x01.
/// </summary>
public sealed class LcdDevice : IDisposable
{
    // Device interface GUID registered by TRCC's WinUSB driver package.
    static readonly Guid InterfaceGuid = new("1d4b2365-4749-48ea-b38a-7c6fdddd7e26");
    const string HardwareId = "vid_87ad&pid_70db";
    const byte OutPipe = 0x01, InPipe = 0x81;

    readonly SafeFileHandle _file;
    readonly IntPtr _usb;
    readonly object _sendLock = new();

    public PanelInfo Panel { get; }

    LcdDevice(SafeFileHandle file, IntPtr usb)
    {
        _file = file;
        _usb = usb;
        uint timeout = 2000;
        WinUsb_SetPipePolicy(_usb, OutPipe, PipeTransferTimeout, 4, ref timeout);
        WinUsb_SetPipePolicy(_usb, InPipe, PipeTransferTimeout, 4, ref timeout);
        // Ends transfers that are an exact multiple of the packet size with a zero-length packet,
        // which USBLCDNEW does by hand.
        byte on = 1;
        WinUsb_SetPipePolicyByte(_usb, OutPipe, ShortPacketTerminate, 1, ref on);
        Panel = Handshake();
    }

    public static LcdDevice Open()
    {
        string path = FindDevicePath() ?? throw new LcdNotFoundException("Screen not found. Is it plugged in?");
        var file = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero,
            OpenExisting, FileAttributeNormal | FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            file.Dispose();
            if (err is 5 or 32) throw new LcdBusyException("Screen is in use by another program (TRCC?).");
            throw new IOException($"Could not open the screen (Win32 error {err}).");
        }
        if (!WinUsb_Initialize(file, out var usb))
        {
            int err = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new LcdBusyException($"Screen is in use by another program (WinUSB error {err}).");
        }
        try
        {
            return new LcdDevice(file, usb);
        }
        catch
        {
            WinUsb_Free(usb);
            file.Dispose();
            throw;
        }
    }

    PanelInfo Handshake()
    {
        var hello = new byte[64];
        LcdProtocol.WriteMagic(hello);
        hello[56] = 1;
        var reply = new byte[1024];
        int error = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt == 2)
            {
                // A sender that died mid-frame leaves the panel waiting for the rest of that JPEG,
                // so it swallows the handshake. Feed it filler to complete the stuck frame.
                var filler = new byte[LcdProtocol.HeaderSize + LcdProtocol.MaxJpegBytes];
                WinUsb_WritePipe(_usb, OutPipe, filler, (uint)filler.Length, out _, IntPtr.Zero);
            }
            WinUsb_ResetPipe(_usb, OutPipe);
            WinUsb_ResetPipe(_usb, InPipe);
            Write(hello, hello.Length);
            if (WinUsb_ReadPipe(_usb, InPipe, reply, (uint)reply.Length, out uint got, IntPtr.Zero) && got > 0 && reply[24] != 0)
                return PanelInfo.FromReply(reply);
            error = Marshal.GetLastWin32Error();
        }
        throw new IOException($"Screen did not answer the handshake (error {error}).");
    }

    /// <summary>Sends a complete packet built by <see cref="LcdProtocol.BuildPicturePacket"/>.</summary>
    public void Send(byte[] packet) => Send(packet, packet.Length);

    public void Send(byte[] packet, int length)
    {
        lock (_sendLock) Write(packet, length);
    }

    void Write(byte[] data, int length)
    {
        if (!WinUsb_WritePipe(_usb, OutPipe, data, (uint)length, out uint sent, IntPtr.Zero) || sent != length)
            throw new IOException($"Write to screen failed (error {Marshal.GetLastWin32Error()}, {sent}/{length} bytes).");
    }

    static string? FindDevicePath()
    {
        var guid = InterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4)!;
                    if (path.Contains(HardwareId, StringComparison.OrdinalIgnoreCase)) return path;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    public void Dispose()
    {
        WinUsb_Free(_usb);
        _file.Dispose();
    }

    const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    const uint FileAttributeNormal = 0x80, FileFlagOverlapped = 0x40000000;
    const uint ShortPacketTerminate = 0x01, PipeTransferTimeout = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr usb);

    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_Free(IntPtr usb);

    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint length, ref uint value);

    [DllImport("winusb.dll", SetLastError = true, EntryPoint = "WinUsb_SetPipePolicy")]
    static extern bool WinUsb_SetPipePolicyByte(IntPtr usb, byte pipe, uint policy, uint length, ref byte value);

    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_ResetPipe(IntPtr usb, byte pipe);

    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
}
