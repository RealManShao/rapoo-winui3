using System.ComponentModel;
using System.Runtime.InteropServices;
using Rapoo.Core;
using static Rapoo.Hid.NativeMethods;

namespace Rapoo.Hid;

public sealed record HidDeviceInfo(string Path, ushort UsagePage, ushort Usage,
    int InputLength, int OutputLength, IReadOnlySet<byte> InputIds, IReadOnlySet<byte> OutputIds)
{
    public bool IsConfiguration => OutputLength == RapooProtocol.ReportLength && OutputIds.Contains(0xBA);
    public bool IsTelemetry => InputLength >= 7 && InputIds.Contains(0xBB);
    public string Label => $"Usage {UsagePage:X4}:{Usage:X4} · IN {InputLength} / OUT {OutputLength}";

    public static IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HID 通信仅支持 Windows。");
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        List<HidDeviceInfo> devices = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size < 8) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    // Filter before opening any unrelated HID collections.
                    if (!path.Contains("vid_24ae&pid_1234", StringComparison.OrdinalIgnoreCase)) continue;
                    using var handle = CreateFile(path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != RapooProtocol.VendorId ||
                        attributes.ProductId != RapooProtocol.ProductId || !HidD_GetPreparsedData(handle, out var parsed)) continue;
                    try
                    {
                        if (HidP_GetCaps(parsed, out var caps) != HidSuccess) continue;
                        var input = GetIds(parsed, 0, caps.NumberInputValueCaps, caps.NumberInputButtonCaps);
                        var output = GetIds(parsed, 1, caps.NumberOutputValueCaps, caps.NumberOutputButtonCaps);
                        devices.Add(new(path, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, input, output));
                    }
                    finally { HidD_FreePreparsedData(parsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    private static HashSet<byte> GetIds(IntPtr parsed, int type, ushort valueCount, ushort buttonCount)
    {
        HashSet<byte> ids = [];
        // Both HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS are 72 bytes; ReportID is at offset 2.
        foreach (var buttons in new[] { false, true })
        {
            var count = buttons ? buttonCount : valueCount;
            if (count == 0) continue;
            var buffer = Marshal.AllocHGlobal(count * 72);
            try
            {
                var status = buttons ? HidP_GetButtonCaps(type, buffer, ref count, parsed) : HidP_GetValueCaps(type, buffer, ref count, parsed);
                if (status == HidSuccess)
                    for (var i = 0; i < count; i++) ids.Add(Marshal.ReadByte(buffer, i * 72 + 2));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return ids;
    }
}
