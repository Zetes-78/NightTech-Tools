using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NightTech.Recovery.Windows;

internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_BEGIN = 0;

    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    // Win32 errors that mean "the device is gone", not "this sector is bad".
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_DEV_NOT_EXIST = 55;
    public const int ERROR_NO_SUCH_DEVICE = 433;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const int ERROR_DEVICE_REMOVED = 1617;
    public const int ERROR_INVALID_HANDLE = 6;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize, byte[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
