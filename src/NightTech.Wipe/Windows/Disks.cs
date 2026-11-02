using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NightTech.Core.Diagnostics;
using NightTech.Core.Imaging;
using NightTech.Core.Wipe;
using static NightTech.Wipe.Windows.NativeMethods;

namespace NightTech.Wipe.Windows;

/// <summary>A volume (partition Windows can read) that lives on a physical drive.</summary>
public sealed record VolumeOnDisk(string VolumeName, string Paths, string Label, long Size, long Free);

/// <summary>A physical drive (\\.\PhysicalDriveN) and why, if at all, it must not be wiped.</summary>
public sealed record DiskInfo(int Number, string Model, string Serial, string Bus, string MediaType, long Size, int SectorSize,
    bool WriteProtected, IReadOnlyList<VolumeOnDisk> Volumes, IReadOnlyList<string> ProtectedReasons)
{
    public string Path => $@"\\.\PhysicalDrive{Number}";
    public bool IsProtected => ProtectedReasons.Count > 0;

    public string Display
    {
        get
        {
            var sb = new StringBuilder($"Disk {Number}: {Model}  ({Format.Bytes(Size)}, {MediaType}, {Bus})");
            if (!string.IsNullOrWhiteSpace(Serial)) sb.Append($"  S/N {Serial}");
            if (IsProtected) sb.Append("  [PROTECTED: " + string.Join("; ", ProtectedReasons) + "]");
            else if (WriteProtected) sb.Append("  [WRITE-PROTECTED]");
            return sb.ToString();
        }
    }

    public WipeDisk ToWipeDisk() => new()
    {
        Model = Model, Serial = Serial, SizeBytes = Size, SectorSize = SectorSize, Bus = Bus, MediaType = MediaType,
    };
}

public static class Disks
{
    /// <summary>
    /// Every physical drive, with the reasons a drive is protected:
    /// it holds Windows or the boot files, this program, or the folder reports are saved to.
    /// </summary>
    public static List<DiskInfo> List(string reportsFolder)
    {
        var volumes = AllVolumes();
        var protect = new Dictionary<int, List<string>>();
        void Protect(IEnumerable<int> disks, string why)
        {
            foreach (var d in disks)
            {
                if (!protect.TryGetValue(d, out var l)) protect[d] = l = new List<string>();
                if (!l.Contains(why)) l.Add(why);
            }
        }

        var systemRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        Protect(DisksOfPath(systemRoot, volumes), "holds Windows");
        Protect(DisksOfPath(AppContext.BaseDirectory, volumes), "holds this program");
        Protect(DisksOfPath(reportsFolder, volumes), "reports are saved here");
        // This PC's boot partition (EFI or System Reserved) usually has no drive letter.
        foreach (var v in volumes.Where(v => v.IsBootOrSystem)) Protect(v.Disks, "holds this PC's boot files");

        var list = new List<DiskInfo>();
        for (int n = 0; n < 64; n++)
        {
            using var h = Open($@"\\.\PhysicalDrive{n}", GENERIC_READ);
            if (h.IsInvalid) continue;
            if (!TryGeometry(h, out long size, out int sector) || size <= 0) continue;
            var (model, serial, bus) = QueryDescriptor(h);
            var media = QueryMedia(h, bus);
            bool writable = DeviceIoControl(h, IOCTL_DISK_IS_WRITABLE, null, 0, null, 0, out _, IntPtr.Zero)
                            || Marshal.GetLastWin32Error() != ERROR_WRITE_PROTECT;
            var onDisk = volumes.Where(v => v.Disks.Contains(n))
                .Select(v => new VolumeOnDisk(v.Name, v.Paths, v.Label, v.Size, v.Free)).ToList();
            if (volumes.Any(v => v.Disks.Contains(n) && v.Disks.Count > 1))
                Protect(new[] { n }, "part of a volume spread over several drives");
            list.Add(new DiskInfo(n, model, serial, bus, media, size, sector, !writable, onDisk,
                protect.TryGetValue(n, out var why) ? why : new List<string>()));
        }
        return list;
    }

    /// <summary>Re-reads a drive right before wiping and checks it's still the same one (drives can renumber when plugged in or out).</summary>
    public static bool StillTheSame(DiskInfo picked, string reportsFolder, out string problem)
    {
        var now = List(reportsFolder).FirstOrDefault(d => d.Number == picked.Number);
        problem = now is null ? "The drive is no longer connected."
            : now.Model != picked.Model || now.Serial != picked.Serial || now.Size != picked.Size ? "A different drive is now in that position."
            : now.IsProtected ? "The drive is now protected: " + string.Join("; ", now.ProtectedReasons) + "."
            : "";
        return problem.Length == 0;
    }

    // ---------------- Volumes ----------------

    private sealed record Vol(string Name, string Paths, string Label, long Size, long Free, HashSet<int> Disks, bool IsBootOrSystem);

    private static List<Vol> AllVolumes()
    {
        var list = new List<Vol>();
        var systemPartition = SystemPartitionDevice();
        var buf = new char[1024];
        var find = FindFirstVolumeW(buf, (uint)buf.Length);
        if (find == INVALID_HANDLE_VALUE) return list;
        try
        {
            do
            {
                int end = Array.IndexOf(buf, '\0');
                var name = new string(buf, 0, end >= 0 ? end : buf.Length); // \\?\Volume{GUID}\
                var disks = DiskExtents(name.TrimEnd('\\'));
                var paths = PathsOf(name);
                string label = "";
                long size = 0, free = 0;
                var first = paths.Split(", ", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is not null)
                {
                    try
                    {
                        var di = new DriveInfo(first);
                        if (di.IsReady) { label = di.VolumeLabel; size = di.TotalSize; free = di.TotalFreeSpace; }
                    }
                    catch { /* not ready */ }
                }
                bool boot = systemPartition.Length > 0 &&
                            string.Equals(DeviceNameOf(name), systemPartition, StringComparison.OrdinalIgnoreCase);
                list.Add(new Vol(name, paths, label, size, free, disks, boot));
            }
            while (FindNextVolumeW(find, buf, (uint)buf.Length));
        }
        finally { FindVolumeClose(find); }
        return list;
    }

    /// <summary>
    /// The partition this PC boots from (EFI or System Reserved), as Windows records it, for example
    /// \Device\HarddiskVolume1. This is only this computer's own boot partition, never one on a customer drive.
    /// </summary>
    private static string SystemPartitionDevice()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\Setup");
            return key?.GetValue("SystemPartition") as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>\\?\Volume{GUID}\ to its device name, for example \Device\HarddiskVolume3.</summary>
    private static string DeviceNameOf(string volumeName)
    {
        var id = volumeName.StartsWith(@"\\?\", StringComparison.Ordinal) ? volumeName[4..] : volumeName;
        id = id.TrimEnd('\\');
        var buf = new char[1024];
        uint n = QueryDosDeviceW(id, buf, (uint)buf.Length);
        if (n == 0) return "";
        int end = Array.IndexOf(buf, '\0');
        return new string(buf, 0, end >= 0 ? end : (int)n);
    }

    private static string PathsOf(string volumeName)
    {
        var buf = new char[2048];
        if (!GetVolumePathNamesForVolumeNameW(volumeName, buf, (uint)buf.Length, out uint len) || len == 0) return "";
        var parts = new string(buf, 0, (int)len).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(", ", parts);
    }

    private static HashSet<int> DiskExtents(string volumeDevice)
    {
        var result = new HashSet<int>();
        using var h = Open(volumeDevice, 0);
        if (h.IsInvalid) return result;
        var buf = new byte[4096];
        if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, (uint)buf.Length, out _, IntPtr.Zero)) return result;
        int count = BitConverter.ToInt32(buf, 0);
        // VOLUME_DISK_EXTENTS: NumberOfDiskExtents (4) + padding (4), then DISK_EXTENT[] of 24 bytes: DiskNumber (4) + pad (4) + StartingOffset (8) + ExtentLength (8)
        for (int i = 0; i < count && 8 + i * 24 + 4 <= buf.Length; i++) result.Add(BitConverter.ToInt32(buf, 8 + i * 24));
        return result;
    }

    private static HashSet<int> DisksOfPath(string path, List<Vol> volumes)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return new HashSet<int>();
            return DiskExtents(@"\\.\" + root.TrimEnd('\\'));
        }
        catch { return new HashSet<int>(); }
    }

    /// <summary>
    /// Locks and dismounts every volume on the drive so Windows lets us overwrite it and nothing else writes to it meanwhile.
    /// Throws if any volume is in use. Keep the returned object until the wipe is over.
    /// </summary>
    public static VolumeLocks LockVolumes(DiskInfo disk)
    {
        var locks = new VolumeLocks();
        try
        {
            foreach (var v in AllVolumes().Where(v => v.Disks.Contains(disk.Number)))
            {
                var h = CreateFileW(v.Name.TrimEnd('\\'), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid)
                    throw new IOException($"Couldn't open volume {Describe(v)} ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
                locks.Add(h);
                bool locked = false;
                for (int attempt = 0; attempt < 10 && !locked; attempt++)
                {
                    locked = DeviceIoControl(h, FSCTL_LOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
                    if (!locked) Thread.Sleep(300);
                }
                if (!locked)
                    throw new IOException($"Volume {Describe(v)} is in use. Close any windows or programs using it and try again.");
                if (!DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out _, IntPtr.Zero))
                    throw new IOException($"Couldn't dismount volume {Describe(v)} ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
            }
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    private static string Describe(Vol v) => v.Paths.Length > 0 ? v.Paths : v.Name;

    /// <summary>Tells Windows to re-read the (now blank) drive.</summary>
    public static void Refresh(DiskInfo disk)
    {
        using var h = Open(disk.Path, GENERIC_READ | GENERIC_WRITE);
        if (!h.IsInvalid) DeviceIoControl(h, IOCTL_DISK_UPDATE_PROPERTIES, null, 0, null, 0, out _, IntPtr.Zero);
    }

    // ---------------- Drive details ----------------

    internal static SafeFileHandle Open(string path, uint access, uint flags = 0) =>
        CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);

    internal static bool TryGeometry(SafeFileHandle h, out long size, out int sectorSize)
    {
        size = 0; sectorSize = 0;
        var buf = new byte[256];
        if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, buf, (uint)buf.Length, out uint got, IntPtr.Zero) || got < 32)
            return false;
        // DISK_GEOMETRY_EX: DISK_GEOMETRY (Cylinders 8, MediaType 4, TracksPerCylinder 4, SectorsPerTrack 4, BytesPerSector 4), then DiskSize 8.
        sectorSize = BitConverter.ToInt32(buf, 20);
        size = BitConverter.ToInt64(buf, 24);
        if (sectorSize <= 0) sectorSize = 512;
        size -= size % sectorSize;
        return true;
    }

    private static (string Model, string Serial, string Bus) QueryDescriptor(SafeFileHandle h)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0) }
        var query = new byte[12];
        var buf = new byte[1024];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, (uint)query.Length, buf, (uint)buf.Length, out uint got, IntPtr.Zero) || got < 32)
            return ("Unknown drive", "", "Other");
        // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset 12, ProductIdOffset 16, SerialNumberOffset 24, BusType 28.
        var vendor = AsciiAt(buf, BitConverter.ToInt32(buf, 12));
        var product = AsciiAt(buf, BitConverter.ToInt32(buf, 16));
        var serial = AsciiAt(buf, BitConverter.ToInt32(buf, 24));
        var model = string.Join(" ", new[] { vendor, product }.Where(s => s.Length > 0)).Trim();
        return (model.Length == 0 ? "Unknown drive" : model, serial, BusName(BitConverter.ToInt32(buf, 28)));
    }

    /// <summary>HDD or SSD, from whether the drive reports a seek penalty (spinning disks do).</summary>
    private static string QueryMedia(SafeFileHandle h, string bus)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty (7), QueryType = 0 }
        var query = new byte[12];
        BitConverter.GetBytes(7).CopyTo(query, 0);
        var buf = new byte[16];
        // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version 4, Size 4, IncursSeekPenalty (BOOLEAN) at 8.
        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, (uint)query.Length, buf, (uint)buf.Length, out uint got, IntPtr.Zero) && got >= 9)
            return buf[8] != 0 ? "HDD" : "SSD";
        return bus == "NVMe" ? "SSD" : "Unknown";
    }

    private static string AsciiAt(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    private static string BusName(int t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "FireWire", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD card", 13 => "MMC", 14 => "Virtual", 15 => "Virtual file",
        16 => "Storage Spaces", 17 => "NVMe", 19 => "UFS", _ => "Other",
    };
}

/// <summary>Holds volume locks for the length of a wipe. Disposing releases them.</summary>
public sealed class VolumeLocks : IDisposable
{
    private readonly List<SafeFileHandle> _handles = new();
    internal void Add(SafeFileHandle h) => _handles.Add(h);
    public int Count => _handles.Count;

    public void Dispose()
    {
        foreach (var h in _handles)
        {
            try { DeviceIoControl(h, FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero); } catch { }
            h.Dispose();
        }
        _handles.Clear();
    }
}

/// <summary>A physical drive opened for writing. Only created after the technician has confirmed and the volumes are locked.</summary>
public sealed class RawWipeTarget : IWipeTarget
{
    private readonly SafeFileHandle _h;
    private readonly DiskInfo _disk;

    public RawWipeTarget(DiskInfo disk)
    {
        if (disk.IsProtected) throw new InvalidOperationException("This drive is protected and can't be wiped.");
        _disk = disk;
        _h = Disks.Open(disk.Path, GENERIC_READ | GENERIC_WRITE, FILE_FLAG_WRITE_THROUGH);
        if (_h.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't open {disk.Path} for writing. Run Night Tech Wipe as administrator.");
        if (!Disks.TryGeometry(_h, out long size, out int sector) || size != disk.Size || sector != disk.SectorSize)
        {
            _h.Dispose();
            throw new IOException("The drive's size changed since it was listed. Refresh the drive list.");
        }
    }

    public string Description => $"{_disk.Model} ({_disk.Bus})";
    public string? SerialNumber => _disk.Serial;
    public long Length => _disk.Size;
    public int SectorSize => _disk.SectorSize;

    public void WriteExactly(long offset, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(_h, offset, out _, FILE_BEGIN)) Fail("seek", offset);
        if (!WriteFile(_h, buffer, (uint)count, out uint done, IntPtr.Zero)) Fail("write", offset);
        if (done != count) throw new IOException($"Only {done:N0} of {count:N0} bytes were written at byte {offset:N0}.");
    }

    public void ReadExactly(long offset, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(_h, offset, out _, FILE_BEGIN)) Fail("seek", offset);
        if (!ReadFile(_h, buffer, (uint)count, out uint got, IntPtr.Zero)) Fail("read", offset);
        if (got != count) throw new IOException($"Only {got:N0} of {count:N0} bytes were read at byte {offset:N0}.");
    }

    public void Flush()
    {
        if (!FlushFileBuffers(_h)) Fail("flush", 0);
    }

    private static void Fail(string what, long offset)
    {
        int err = Marshal.GetLastWin32Error();
        var inner = new Win32Exception(err);
        if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_NOT_READY or ERROR_DEV_NOT_EXIST or ERROR_NO_SUCH_DEVICE
            or ERROR_DEVICE_NOT_CONNECTED or ERROR_DEVICE_REMOVED or ERROR_INVALID_HANDLE)
            throw new DeviceGoneException($"Drive stopped responding during {what} at byte {offset:N0}: {inner.Message}", inner);
        // Refused writes are not bad sectors: stop rather than mark the whole drive as failed sectors.
        if (err is ERROR_ACCESS_DENIED or ERROR_WRITE_PROTECT)
            throw new UnauthorizedAccessException($"Windows refused the {what} at byte {offset:N0} ({inner.Message}). " +
                                                  "The drive may be write-protected or still in use.", inner);
        throw new IOException($"{char.ToUpperInvariant(what[0])}{what[1..]} error at byte {offset:N0} ({inner.Message}, code {err}).", inner);
    }

    public void Dispose() => _h.Dispose();
}
