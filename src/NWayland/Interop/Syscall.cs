using System.Runtime.InteropServices;

namespace NWayland.Interop;

internal static class Syscall
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);
}
