using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NightTech.Core.Imaging;
using static NightTech.Recovery.Windows.NativeMethods;

namespace NightTech.Recovery.Windows;

/// <summary>A physical drive as Windows sees it (\\.\PhysicalDriveN).</summary>
public sealed record DiskInfo(
    int Number,
    string Model,
    string? Serial,
    string Bus,
    long Size,
    int SectorSize,
    bool Removable,
    bool HoldsWindows)
{
    public string Path => $@"\\.\PhysicalDrive{Number}";

    public string Display
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append($"Disk {Number}: {Model}  ({ImagingReport.FormatBytes(Size)}, {Bus})");
            if (!string.IsNullOrWhiteSpace(Serial)) sb.Append($"  S/N {Serial}");
            if (HoldsWindows) sb.Append("  [THIS PC'S WINDOWS DRIVE]");
            return sb.ToString();
        }
    }
}

public static class PhysicalDisks
{
    public static List<DiskInfo> List()
    {
        var windowsDisks = DisksOfVolume(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var list = new List<DiskInfo>();
        for (int n = 0; n < 64; n++)
        {
            using var h = Open($@"\\.\PhysicalDrive{n}", GENERIC_READ);
            if (h.IsInvalid) continue;
            if (!TryGeometry(h, out long size, out int sector) || size <= 0) continue;
            var (model, serial, bus, removable) = QueryDescriptor(h);
            list.Add(new DiskInfo(n, model, serial, bus, size, sector, removable, windowsDisks.Contains(n)));
        }
        return list;
    }

    /// <summary>Physical disk numbers that hold the volume containing <paramref name="path"/>. Empty for network paths or on error.</summary>
    public static HashSet<int> DisksOfVolume(string path)
    {
        var result = new HashSet<int>();
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return result;
            var volume = @"\\.\" + root.TrimEnd('\\');
            using var h = Open(volume, 0);
            if (h.IsInvalid) return result;
            var buf = new byte[1024];
            if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
                return result;
            int count = BitConverter.ToInt32(buf, 0);
            for (int i = 0; i < count && 8 + i * 24 + 4 <= buf.Length; i++)
                result.Add(BitConverter.ToInt32(buf, 8 + i * 24));
        }
        catch { /* treat as unknown */ }
        return result;
    }

    internal static SafeFileHandle Open(string path, uint access) =>
        CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    internal static bool TryGeometry(SafeFileHandle h, out long size, out int sectorSize)
    {
        size = 0; sectorSize = 0;
        var buf = new byte[256];
        if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, buf, (uint)buf.Length, out uint got, IntPtr.Zero) || got < 32)
            return false;
        // DISK_GEOMETRY_EX: DISK_GEOMETRY (Cylinders 8, MediaType 4, TracksPerCylinder 4, SectorsPerTrack 4, BytesPerSector 4) then DiskSize 8.
        sectorSize = BitConverter.ToInt32(buf, 20);
        size = BitConverter.ToInt64(buf, 24);
        if (sectorSize <= 0) sectorSize = 512;
        size -= size % sectorSize;
        return true;
    }

    private static (string Model, string? Serial, string Bus, bool Removable) QueryDescriptor(SafeFileHandle h)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0) }
        var query = new byte[12];
        var buf = new byte[1024];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, (uint)query.Length, buf, (uint)buf.Length, out uint got, IntPtr.Zero) || got < 32)
            return ("Unknown drive", null, "Unknown", false);

        // STORAGE_DEVICE_DESCRIPTOR: Version 0, Size 4, DeviceType 8, Modifier 9, RemovableMedia 10, CommandQueueing 11,
        // VendorIdOffset 12, ProductIdOffset 16, ProductRevisionOffset 20, SerialNumberOffset 24, BusType 28.
        bool removable = buf[10] != 0;
        string? vendor = AsciiAt(buf, BitConverter.ToInt32(buf, 12));
        string? product = AsciiAt(buf, BitConverter.ToInt32(buf, 16));
        string? serial = AsciiAt(buf, BitConverter.ToInt32(buf, 24));
        int busType = BitConverter.ToInt32(buf, 28);

        var model = string.Join(" ", new[] { vendor, product }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (model.Length == 0) model = "Unknown drive";
        return (model, serial, BusName(busType), removable);
    }

    private static string? AsciiAt(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return null;
        int end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        var s = Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
        return s.Length == 0 ? null : s;
    }

    private static string BusName(int t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "FireWire", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD card", 13 => "MMC", 14 => "Virtual", 15 => "Virtual file",
        16 => "Storage Spaces", 17 => "NVMe", 19 => "UFS", _ => "Other",
    };
}

/// <summary>
/// Read-only access to a physical drive. The handle is opened with GENERIC_READ only,
/// so Windows will refuse any write through it.
/// </summary>
public sealed class RawDiskSource : ISourceDevice
{
    private readonly SafeFileHandle _h;
    private readonly DiskInfo _disk;

    public RawDiskSource(DiskInfo disk)
    {
        _disk = disk;
        _h = PhysicalDisks.Open(disk.Path, GENERIC_READ);
        if (_h.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {disk.Path}. Run Night Tech Recovery as administrator.");
        if (!PhysicalDisks.TryGeometry(_h, out long size, out int sector) || size != disk.Size || sector != disk.SectorSize)
        {
            _h.Dispose();
            throw new IOException("The drive's size changed since it was listed. Refresh the drive list.");
        }
    }

    public string Description => $"{_disk.Model} ({_disk.Bus})";
    public string? SerialNumber => _disk.Serial;
    public long Length => _disk.Size;
    public int SectorSize => _disk.SectorSize;

    public void ReadExactly(long offset, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(_h, offset, out _, FILE_BEGIN))
            Fail("seek", offset);
        int done = 0;
        while (done < count)
        {
            // ReadFile into the start of a scratch window when we need to continue a short read.
            byte[] target = done == 0 ? buffer : new byte[count - done];
            if (!ReadFile(_h, target, (uint)(count - done), out uint got, IntPtr.Zero))
                Fail("read", offset + done);
            if (got == 0) throw new IOException($"Unexpected end of drive at byte {offset + done:N0}.");
            if (done != 0) Array.Copy(target, 0, buffer, done, (int)got);
            done += (int)got;
        }
    }

    private static void Fail(string what, long offset)
    {
        int err = Marshal.GetLastWin32Error();
        var inner = new Win32Exception(err);
        if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_NOT_READY or ERROR_DEV_NOT_EXIST
            or ERROR_NO_SUCH_DEVICE or ERROR_DEVICE_NOT_CONNECTED or ERROR_DEVICE_REMOVED or ERROR_INVALID_HANDLE)
            throw new DeviceGoneException($"Drive stopped responding during {what} at byte {offset:N0}: {inner.Message}", inner);
        throw new IOException($"Read error at byte {offset:N0} ({inner.Message}, code {err}).", inner);
    }

    public void Dispose() => _h.Dispose();
}
