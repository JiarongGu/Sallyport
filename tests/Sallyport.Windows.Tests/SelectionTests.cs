using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using Sallyport.Windows.Routing;

namespace Sallyport.Windows.Tests;

public sealed class SelectionTests
{
    [Fact]
    public void A_process_running_a_listed_executable_is_selected()
    {
        var selection = new ProgramSelection([Environment.ProcessPath!]);
        Assert.True(selection.Selects(Environment.ProcessId));
        Assert.Contains(Environment.ProcessId, selection.Selected);
    }

    [Fact]
    public void A_process_running_another_executable_is_not()
    {
        var selection = new ProgramSelection([Path.Combine(Path.GetTempPath(), "not-a-game.exe")]);
        Assert.False(selection.Selects(Environment.ProcessId));
        Assert.Empty(selection.Unreadable);
    }

    [Fact]
    public void System_and_a_process_that_is_not_there_are_never_selected_and_the_second_is_unreadable()
    {
        var selection = new ProgramSelection([Environment.ProcessPath!]);
        Assert.False(selection.Selects(4));
        // PIDs are multiples of 4; one this high is not running.
        const int Gone = 0x7FFFFFF0;
        Assert.False(selection.Selects(Gone));
        Assert.Contains(Gone, selection.Unreadable);
    }

    [Fact]
    public void An_idle_mapping_goes_unless_its_socket_still_holds_it()
    {
        var nat = new Nat(BinaryPrimitives.ReadUInt32LittleEndian(IPAddress.Parse("10.8.0.6").GetAddressBytes()));
        var link = new byte[14];
        link[12] = 0x08;
        nat.Outbound(Udp(50000), link);
        nat.Outbound(Udp(50001), link);
        var later = Environment.TickCount64 + 1;

        var gone = nat.Expire(later, m => m.LocalPort == 50001);

        Assert.Equal(1, gone);
        Assert.Equal(1, nat.Count);
    }

    private static byte[] Udp(ushort port)
    {
        var ip = new byte[28];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), 28);
        ip[9] = Flow.Udp;
        IPAddress.Parse("192.168.1.20").GetAddressBytes().CopyTo(ip, 12);
        IPAddress.Parse("203.0.113.7").GetAddressBytes().CopyTo(ip, 16);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(20), port);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(22), 3478);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(24), 8);
        return ip;
    }
}
