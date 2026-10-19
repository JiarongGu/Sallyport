using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace Sallyport.Observer;

internal static class TcpTable
{
    public static IReadOnlyList<NetworkObservation> Read(long timestamp)
    {
        uint size = 0;
        uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (status is not (0 or 122)) throw new Win32Exception((int)status, "TCP table unavailable");
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                status = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (status == 122) continue;
                if (status != 0) throw new Win32Exception((int)status, "TCP table unavailable");
                int count = Marshal.ReadInt32(buffer);
                // MIB_TCPROW_OWNER_PID is six DWORDs (24 bytes); the port DWORD's first two bytes are network order.
                if (count < 0 || 4L + count * 24L > size) throw new InvalidDataException("Invalid TCP table size.");
                List<NetworkObservation> observations = [];
                byte[] remote = new byte[4];
                byte[] port = new byte[2];
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buffer + 4 + i * 24;
                    if (Marshal.ReadInt32(row) == 2) continue;
                    Marshal.Copy(row + 12, remote, 0, 4);
                    if (remote.All(b => b == 0)) continue;
                    Marshal.Copy(row + 16, port, 0, 2);
                    observations.Add(new("TCP", new IPAddress(remote), BinaryPrimitives.ReadUInt16BigEndian(port),
                        unchecked((uint)Marshal.ReadInt32(row + 20)), timestamp, "tcp-table"));
                }
                return observations;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new Win32Exception(122, "TCP table changed too quickly to read");
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint addressFamily, int tableClass, uint reserved);
}
