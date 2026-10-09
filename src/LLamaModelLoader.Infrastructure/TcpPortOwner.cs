using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LLamaModelLoader.Infrastructure;

internal static class TcpPortOwner
{
    public static bool IsOwnedByJob(int port, OwnedProcess child)
    {
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, 2, 3, 0); // IPv4, OWNER_PID_LISTENER
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var code = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
            if (code != 0) throw new Win32Exception((int)code);
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var localPort = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                if (localPort == port && child.OwnsProcess(Marshal.ReadInt32(row, 20))) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, uint reserved);
}
