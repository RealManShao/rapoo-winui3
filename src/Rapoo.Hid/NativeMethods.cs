using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rapoo.Hid;

internal static class NativeMethods
{
    internal const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    internal const uint ShareReadWrite = 3, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    internal const int HidSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInterfaceData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes
    {
        public int Size;
        public ushort VendorId, ProductId, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Caps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort length, IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int type, IntPtr caps, ref ushort length, IntPtr data);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] report, int length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] report, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid classGuid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint detailSize, out uint required, IntPtr device);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
