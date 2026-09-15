using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VisionWorkbench.Cameras.Hikvision;

/// <summary>
/// MVS C API 的最小稳定互操作层。运行时解析 MvCameraControl.dll，避免未安装 MVS 时
/// 主程序因静态依赖厂商 DLL 而无法启动。
/// </summary>
internal static class MvsNative
{
    private const string LibraryName = "MvCameraControl.dll";
    private static IntPtr _library;
    private static int _loadAttempted;

    static MvsNative() => NativeLibrary.SetDllImportResolver(typeof(MvsNative).Assembly, Resolve);

    public const int Ok = 0;
    public const uint GigeDevice = 0x00000001;
    public const uint UsbDevice = 0x00000004;
    public const uint AccessExclusive = 1;
    public const uint AcquisitionContinuous = 2;
    public const uint TriggerOff = 0;
    public const uint TriggerOn = 1;
    public const uint TriggerSourceSoftware = 7;
    public const uint TriggerSourceLine0 = 0;

    public static bool IsAvailable => TryEnsureLoaded();

    public static IReadOnlyList<string> SearchPaths
    {
        get
        {
            var paths = new List<string>();
            var configured = Environment.GetEnvironmentVariable("VISIONWORKBENCH_MVS_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                paths.Add(Path.Combine(configured, LibraryName));
            }
            paths.Add(Path.Combine(AppContext.BaseDirectory, LibraryName));
            paths.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "mvs", "Win64_x64", LibraryName));
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                paths.Add(Path.Combine(root, "Common Files", "MVS", "Runtime", "Win64_x64", LibraryName));
                paths.Add(Path.Combine(root, "MVS", "Runtime", "Win64_x64", LibraryName));
                paths.Add(Path.Combine(root, "MVS", "Runtime", "Win64", LibraryName));
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public static string? LoadedPath { get; private set; }

    public static bool TryEnsureLoaded()
    {
        if (_library != IntPtr.Zero) return true;
        if (Interlocked.Exchange(ref _loadAttempted, 1) == 1) return false;
        foreach (var path in SearchPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                _library = NativeLibrary.Load(path);
                LoadedPath = path;
                return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        return TryEnsureLoaded() ? _library : IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceList
    {
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public IntPtr[]? Devices;

        public static DeviceList Create() => new() { Devices = new IntPtr[256] };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameInfoEx
    {
        public ushort Width;
        public ushort Height;
        public uint PixelType;
        public uint FrameNumber;
        public uint DeviceTimestampHigh;
        public uint DeviceTimestampLow;
        public uint Reserved0;
        public long HostTimestamp;
        public uint FrameLength;
        public uint SecondCount;
        public uint CycleCount;
        public uint CycleOffset;
        public float Gain;
        public float ExposureTime;
        public uint AverageBrightness;
        public uint Red;
        public uint Green;
        public uint Blue;
        public uint FrameCounter;
        public uint TriggerIndex;
        public uint Input;
        public uint Output;
        public ushort OffsetX;
        public ushort OffsetY;
        public ushort ChunkWidth;
        public ushort ChunkHeight;
        public uint LostPacket;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 39)] public uint[]? Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IntValue
    {
        public uint Current;
        public uint Max;
        public uint Min;
        public uint Increment;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[]? Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FloatValue
    {
        public float Current;
        public float Max;
        public float Min;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[]? Reserved;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_EnumDevices(uint layerType, ref DeviceList deviceList);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_CreateHandle(out IntPtr handle, IntPtr deviceInfo);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_DestroyHandle(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_OpenDevice(IntPtr handle, uint accessMode, ushort switchOverKey);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_CloseDevice(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_StartGrabbing(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_StopGrabbing(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetImageForBGR(
        IntPtr handle, IntPtr data, uint dataSize, ref FrameInfoEx frameInfo, int timeoutMs);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetImageNodeNum(IntPtr handle, uint nodeCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetIntValue(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string key, ref IntValue value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetIntValue(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string key, uint value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetFloatValue(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string key, ref FloatValue value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetFloatValue(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string key, float value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetEnumValue(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string key, uint value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetCommandValue(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string key);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_TriggerSoftwareExecute(IntPtr handle);

    public static string ReadAscii(IntPtr ptr, int offset, int length)
    {
        if (ptr == IntPtr.Zero || length <= 0) return "";
        var data = new byte[length];
        Marshal.Copy(IntPtr.Add(ptr, offset), data, 0, length);
        var end = Array.IndexOf(data, (byte)0);
        return Encoding.ASCII.GetString(data, 0, end >= 0 ? end : data.Length).Trim();
    }

    public static string ReadDeviceSerial(IntPtr ptr, uint layerType) =>
        ReadAscii(ptr, 32 + (layerType == GigeDevice ? 164 : 396), layerType == GigeDevice ? 16 : 64);

    public static string ReadDeviceModel(IntPtr ptr, uint layerType) =>
        ReadAscii(ptr, 32 + (layerType == GigeDevice ? 52 : 140), layerType == GigeDevice ? 32 : 64);

    public static string ReadDeviceUserName(IntPtr ptr, uint layerType) =>
        ReadAscii(ptr, 32 + (layerType == GigeDevice ? 180 : 460), layerType == GigeDevice ? 16 : 64);

    public static string ReadDeviceIp(IntPtr ptr)
    {
        var ip = unchecked((uint)Marshal.ReadInt32(ptr, 32 + 8));
        return string.Join('.', (ip >> 24) & 0xff, (ip >> 16) & 0xff, (ip >> 8) & 0xff, ip & 0xff);
    }

    public static uint ReadLayerType(IntPtr ptr) => unchecked((uint)Marshal.ReadInt32(ptr, 12));

    public static string Error(int code) => $"MVS 错误 0x{unchecked((uint)code):X8}";
}
