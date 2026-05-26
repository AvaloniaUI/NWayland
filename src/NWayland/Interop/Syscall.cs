using System.Runtime.InteropServices;

namespace NWayland.Interop;

internal static class Syscall
{
    internal const int SHUT_RDWR = 2;
    
    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);
    
    [DllImport("libc", SetLastError = true)]
    internal static extern int shutdown(int sockfd, int how);
}
