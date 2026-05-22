using System.IO;
using System.Runtime.InteropServices;

namespace VideoMetadataEditor.Services;

public record DriveProfile(
    string   RootPath,
    bool     IsSsd,
    bool     IsNvme,
    bool     IsNetwork,
    bool     IsRemovable,
    int      RecommendedMetadataWorkers,
    int      RecommendedScanWorkers,
    string   Description);

/// <summary>
/// Detects the type of drive a given path lives on and recommends safe,
/// optimal worker counts for parallel metadata reading.
///
/// Detection layers (in order):
///   1. UNC / network path  → conservative (2 workers)
///   2. DriveType           → CDRom / Removable flagged
///   3. StorageDeviceProperty IOCTL → bus type (NVMe / SATA / USB / ATA)
///   4. SeekPenaltyProperty IOCTL   → HDD vs SSD (works on ALL buses incl. USB)
///   5. CPU count scales workers within per-class safe bands
///
/// Worker bands:
///   NVMe internal    : min(CPUs, 16)   – queue depth ≥1024, near-zero latency
///   SATA/internal SSD: min(CPUs, 12)   – fast random IO
///   USB SSD          : min(CPUs, 6)    – USB bandwidth is the bottleneck
///   Internal HDD     : min(CPUs/4, 4)  – seek penalty; more threads = thrashing
///   USB HDD          : 2               – protect the drive + USB latency
///   Network          : 2               – latency-bound
///   Optical          : 1               – sequential only
///   Unknown          : min(CPUs/2, 6)  – safe conservative default
/// </summary>
public static class DriveCapabilityService
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY      = 0x002D1400;
    private const int  PropertyStandardQuery             = 0;
    private const int  StorageDeviceSeekPenaltyProperty  = 7;
    private const int  StorageDeviceProperty             = 0;

    private const uint BusTypeUsb  = 0x09;
    private const uint BusTypeNvme = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.Bool)]
        public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.Bool)] public bool RemovableMedia;
        [MarshalAs(UnmanagedType.Bool)] public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public uint BusType;
        public uint RawPropertiesLength;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
        uint   dwIoControlCode,
        ref    STORAGE_PROPERTY_QUERY lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out    uint lpBytesReturned, IntPtr lpOverlapped);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DriveProfile> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the <see cref="DriveProfile"/> for the drive containing
    /// <paramref name="path"/>. Results are cached per drive root.
    /// </summary>
    public static DriveProfile GetProfile(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path) ?? path;
            return _cache.GetOrAdd(root, _ => BuildProfile(root, path));
        }
        catch { return SafeDefault(path); }
    }

    private static DriveProfile BuildProfile(string root, string original)
    {
        int cpus = Environment.ProcessorCount;

        // 1. Network / UNC
        if (original.StartsWith(@"\\") || original.StartsWith("//"))
            return MakeDrive(root, isSsd: false, isNvme: false, isNet: true,  isRemovable: false, 2, 2,
                "Network share — conservative (2 workers)");

        // 2. DriveInfo basic type
        DriveType driveType = DriveType.Unknown;
        try { driveType = new DriveInfo(root).DriveType; } catch { }

        if (driveType == DriveType.Network)
            return MakeDrive(root, false, false, true, false, 2, 2, "Network drive (2 workers)");

        if (driveType == DriveType.CDRom)
            return MakeDrive(root, false, false, false, false, 1, 1, "Optical drive (1 worker)");

        // 3. IOCTL: bus type + seek penalty
        var (busType, seekPenalty) = QueryDeviceInfo(root);

        bool isUsb      = busType == BusTypeUsb;
        bool isNvme     = busType == BusTypeNvme;
        bool isRem      = driveType == DriveType.Removable;

        // Fallback: if IOCTL failed on a removable drive, assume spinning (safe)
        bool isHdd = seekPenalty == true
                  || (seekPenalty == null && isRem);
        bool isSsd = seekPenalty == false;

        // 4. Classify

        if (isUsb && isHdd)
        {
            // USB HDD — seek penalty + USB latency + power concerns
            // 2 workers prevents head thrashing; going higher causes audible clicking
            return MakeDrive(root, false, false, false, true, 2, 2,
                "USB HDD — protected, 2 workers (seek-penalty + USB-safe)");
        }

        if (isUsb && (isSsd || !isHdd))
        {
            // USB SSD (Samsung T7, WD My Passport SSD, etc.)
            // USB 3.x bandwidth cap makes more than 6 workers wasteful
            int w = Math.Min(cpus, 6);
            return MakeDrive(root, true, false, false, true, w, w,
                $"USB SSD — USB-bandwidth-capped ({w} workers)");
        }

        if (isRem && isHdd)
        {
            return MakeDrive(root, false, false, false, true, 2, 2,
                "Removable HDD — protected (2 workers)");
        }

        if (isRem && !isHdd)
        {
            int w = Math.Min(cpus, 4);
            return MakeDrive(root, true, false, false, true, w, w,
                $"Removable SSD/Flash ({w} workers)");
        }

        if (isHdd)
        {
            // Internal HDD — every extra thread adds a seek; diminishing returns above 4
            int w = Math.Min(4, Math.Max(2, cpus / 4));
            return MakeDrive(root, false, false, false, false, w, w,
                $"Internal HDD — seek-aware ({w} workers)");
        }

        if (isNvme)
        {
            int w = Math.Min(16, Math.Max(8, cpus));
            return MakeDrive(root, true, true, false, false, w, w,
                $"NVMe SSD — high-parallelism ({w} workers)");
        }

        // SATA SSD or unknown fixed SSD
        {
            int w = Math.Min(12, Math.Max(4, cpus));
            return MakeDrive(root, true, false, false, false, w, w,
                $"SATA/Internal SSD — parallel ({w} workers)");
        }
    }

    private static (uint busType, bool? seekPenalty) QueryDeviceInfo(string root)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (0, null);

        var driveLetter = root.TrimEnd('\\', '/');
        if (driveLetter.Length < 2) return (0, null);
        var devicePath = $@"\\.\{driveLetter[0]}:";

        try
        {
            using var handle = CreateFile(devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return (0, null);

            // Query bus type
            uint busType = 0;
            var q = new STORAGE_PROPERTY_QUERY
            {
                PropertyId           = StorageDeviceProperty,
                QueryType            = PropertyStandardQuery,
                AdditionalParameters = [0]
            };
            int sz = Marshal.SizeOf<STORAGE_DEVICE_DESCRIPTOR>() + 512;
            var pDesc = Marshal.AllocHGlobal(sz);
            try
            {
                if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    ref q, (uint)Marshal.SizeOf(q), pDesc, (uint)sz, out _, IntPtr.Zero))
                    busType = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(pDesc).BusType;
            }
            finally { Marshal.FreeHGlobal(pDesc); }

            // Query seek penalty (works on USB drives too)
            bool? seekPenalty = null;
            var q2 = new STORAGE_PROPERTY_QUERY
            {
                PropertyId           = StorageDeviceSeekPenaltyProperty,
                QueryType            = PropertyStandardQuery,
                AdditionalParameters = [0]
            };
            var penDesc = new DEVICE_SEEK_PENALTY_DESCRIPTOR();
            var pPen = Marshal.AllocHGlobal(Marshal.SizeOf(penDesc));
            try
            {
                if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    ref q2, (uint)Marshal.SizeOf(q2), pPen, (uint)Marshal.SizeOf(penDesc),
                    out _, IntPtr.Zero))
                {
                    penDesc     = Marshal.PtrToStructure<DEVICE_SEEK_PENALTY_DESCRIPTOR>(pPen);
                    seekPenalty = penDesc.IncursSeekPenalty;
                }
            }
            finally { Marshal.FreeHGlobal(pPen); }

            return (busType, seekPenalty);
        }
        catch { return (0, null); }
    }

    private static DriveProfile MakeDrive(
        string root, bool isSsd, bool isNvme, bool isNet, bool isRemovable,
        int meta, int scan, string desc)
        => new(root, isSsd, isNvme, isNet, isRemovable, meta, scan, desc);

    private static DriveProfile SafeDefault(string path)
    {
        int w = Math.Min(6, Math.Max(2, Environment.ProcessorCount / 2));
        return MakeDrive(path, true, false, false, false, w, w,
            $"Unknown drive — safe default ({w} workers)");
    }
}
