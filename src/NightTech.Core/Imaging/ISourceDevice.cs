namespace NightTech.Core.Imaging;

/// <summary>
/// A drive (or test double) that can be read but never written.
/// Implementations must not offer any way to write to the device.
/// </summary>
public interface ISourceDevice : IDisposable
{
    /// <summary>Human-readable name, e.g. "Samsung SSD 870 EVO (USB)".</summary>
    string Description { get; }

    /// <summary>Serial number if the device reports one.</summary>
    string? SerialNumber { get; }

    /// <summary>Total size in bytes. Always a multiple of <see cref="SectorSize"/>.</summary>
    long Length { get; }

    /// <summary>Logical sector size in bytes (usually 512 or 4096).</summary>
    int SectorSize { get; }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes starting at <paramref name="offset"/>.
    /// Offset and count are always multiples of <see cref="SectorSize"/>.
    /// Throws <see cref="IOException"/> on a media error (bad sector) and
    /// <see cref="DeviceGoneException"/> if the device stopped responding or was unplugged.
    /// </summary>
    void ReadExactly(long offset, byte[] buffer, int count);
}

/// <summary>The source device disappeared or stopped responding. Imaging stops and can be resumed later.</summary>
public sealed class DeviceGoneException : IOException
{
    public DeviceGoneException(string message, Exception? inner = null) : base(message, inner) { }
}
