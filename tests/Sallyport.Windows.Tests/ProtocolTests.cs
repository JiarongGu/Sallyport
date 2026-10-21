using System.Buffers.Binary;
using Sallyport.Windows.PacketFilter;
using System.Security.Principal;
using Sallyport.Windows.Routing;
using Sallyport.Windows.Service;

namespace Sallyport.Windows.Tests;

/// <summary>The router service's frames (RouterProtocol): what one side writes, the other reads back the same.</summary>
public sealed class ProtocolTests
{
    private static async Task<FrameStream> WrittenAsync(Func<FrameStream, Task> write)
    {
        var buffer = new MemoryStream();
        await write(new FrameStream(buffer));
        return new FrameStream(new MemoryStream(buffer.ToArray()));
    }

    [Fact]
    public async Task A_control_message_reads_back_as_written()
    {
        var reader = await WrittenAsync(async f =>
        {
            await f.SendAsync(new PrepareRun([new(@"E:\Games\Zenless\ZenlessZoneZero.exe", LossMode.Hold, 15), new(@"C:\Tools\launcher.exe", LossMode.Direct)]));
            await f.SendAsync(new SelectPrograms([new(@"C:\Tools\launcher.exe", LossMode.Stop)]));
        });

        var prepare = Assert.IsType<PrepareRun>((await reader.ReadAsync(CancellationToken.None))!.Value.Message);
        var select = Assert.IsType<SelectPrograms>((await reader.ReadAsync(CancellationToken.None))!.Value.Message);

        // Each program with its own loss policy (D23), and the selection changed while the run lasts.
        Assert.Equal([new ProgramRule(@"E:\Games\Zenless\ZenlessZoneZero.exe", LossMode.Hold, 15), new ProgramRule(@"C:\Tools\launcher.exe", LossMode.Direct)],
            prepare.Programs);
        Assert.Equal(new ProgramRule(@"C:\Tools\launcher.exe", LossMode.Stop), Assert.Single(select.Programs));
        Assert.Equal(TimeSpan.FromSeconds(15), prepare.Programs[0].Policy.DirectAfter);
    }

    [Fact]
    public void Nothing_of_sallyports_own_folder_and_nothing_relative_is_routable()
    {
        var routable = RouterProtocol.Routable(
            [new(@"D:\Program Files\Sallyport\Sallyport.Client.exe"), new(@"game.exe"), new(@"E:\Games\ZZZ\ZenlessZoneZero.exe")],
            @"D:\Program Files\Sallyport\");

        Assert.Equal(@"E:\Games\ZZZ\ZenlessZoneZero.exe", Assert.Single(routable).Path);
    }

    [Fact]
    public void Sallyports_own_folder_is_known_however_its_path_is_written_and_what_passes_is_made_whole()
    {
        var routable = RouterProtocol.Routable(
            [new(@"D:\Program Files\Sallyport\data\..\Sallyport.Client.exe"), new(@"D:\PROGRAM FILES\sallyport\.\Sallyport.Client.exe"),
             new("E:\\Games\\a\0b.exe"), new(@"E:\Games\ZZZ\..\ZZZ\ZenlessZoneZero.exe")],
            @"D:\Program Files\Sallyport");

        Assert.Equal(@"E:\Games\ZZZ\ZenlessZoneZero.exe", Assert.Single(routable).Path);
    }

    [Fact]
    public async Task A_packet_frame_too_short_for_its_generation_is_refused_where_it_is_read()
    {
        var head = new byte[] { 3, 0, 0, 0, RouterProtocol.PacketFrame, 1, 2 };
        var reader = new FrameStream(new MemoryStream(head));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_packet_keeps_its_generation_and_bytes()
    {
        var packet = Enumerable.Range(0, 1500).Select(i => (byte)i).ToArray();
        var reader = await WrittenAsync(async f =>
        {
            await f.SendPacketAsync(7, packet);
            await f.SendAsync(new RetireGeneration(7));
        });

        var first = (await reader.ReadAsync(CancellationToken.None))!.Value;
        var second = (await reader.ReadAsync(CancellationToken.None))!.Value;

        Assert.Equal(RouterProtocol.PacketFrame, first.Kind);
        Assert.Equal(7, first.Generation);
        Assert.Equal(packet, first.Packet);
        Assert.Null(first.Message);
        Assert.Equal(7, Assert.IsType<RetireGeneration>(second.Message).Generation);
    }

    [Fact]
    public async Task Packets_written_in_batches_read_back_one_frame_each_in_order()
    {
        var queue = System.Threading.Channels.Channel.CreateUnbounded<(int, byte[])>();
        var packets = Enumerable.Range(0, 500).Select(i => (Generation: i % 3 + 1, Packet: Enumerable.Repeat((byte)i, 40 + i * 3 % 1460).ToArray())).ToList();
        foreach (var p in packets) queue.Writer.TryWrite(p);
        queue.Writer.Complete();
        var buffer = new MemoryStream();

        await new FrameStream(buffer).PumpPacketsAsync(queue.Reader, CancellationToken.None);

        var reader = new FrameStream(new MemoryStream(buffer.ToArray()));
        foreach (var (generation, packet) in packets)
        {
            var frame = (await reader.ReadAsync(CancellationToken.None))!.Value;
            Assert.Equal(generation, frame.Generation);
            Assert.Equal(packet, frame.Packet);
        }
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_other_end_closing_between_frames_is_the_end_and_inside_one_is_an_error()
    {
        var empty = new FrameStream(new MemoryStream());
        Assert.Null(await empty.ReadAsync(CancellationToken.None));

        var buffer = new MemoryStream();
        await new FrameStream(buffer).SendAsync(new Heartbeat());
        var cut = new FrameStream(new MemoryStream(buffer.ToArray()[..^2]));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await cut.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RouterProtocol.MostFrame + 1)]
    [InlineData(-5)]
    public async Task A_frame_length_outside_its_bounds_is_refused_before_it_is_read(int length)
    {
        var head = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(head, length);
        head[4] = RouterProtocol.ControlFrame;
        var reader = new FrameStream(new MemoryStream(head));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Sallyport\Sallyport.Client.exe", @"C:\Program Files\Sallyport\", true)]
    [InlineData(@"C:\PROGRAM FILES\SALLYPORT\Sallyport.Client.exe", @"C:\Program Files\Sallyport", true)]
    [InlineData(@"C:\Program Files\Sallyport\sub\Sallyport.Client.exe", @"C:\Program Files\Sallyport\", false)]
    [InlineData(@"C:\Users\x\Downloads\Sallyport.Client.exe", @"C:\Program Files\Sallyport\", false)]
    public void A_peer_runs_from_a_folder_only_directly_in_it(string image, string folder, bool expected)
    {
        Assert.Equal(expected, new PipePeer(1, 1, null, image).RunsFrom(folder));
    }

    private const string Folder = @"C:\Program Files\Sallyport";
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Person = new("S-1-5-21-1-2-3-1001");
    private static readonly RouterClient.ServiceRecord Service = new(ServiceState.Running, 40, "LocalSystem", Folder + @"\Sallyport.Service.exe");

    [Fact]
    public void A_service_the_person_cannot_open_is_believed_on_the_service_managers_word()
    {
        Assert.Null(RouterClient.Distrust(new PipePeer(40, 0, null, null), Folder, Service));
    }

    [Fact]
    public void A_service_the_client_can_open_must_agree_with_the_manager()
    {
        Assert.Null(RouterClient.Distrust(new PipePeer(40, 0, LocalSystem, Folder + @"\Sallyport.Service.exe"), Folder, Service));
        Assert.Contains("not running as the system", RouterClient.Distrust(new PipePeer(40, 0, Person, Folder + @"\Sallyport.Service.exe"), Folder, Service));
        Assert.Contains("does not run from", RouterClient.Distrust(new PipePeer(40, 0, LocalSystem, @"C:\Temp\Sallyport.Service.exe"), Folder, Service));
    }

    [Fact]
    public void What_answers_the_routers_pipe_is_refused_unless_the_manager_says_it_runs_the_service()
    {
        Assert.Contains("not the Sallyport service", RouterClient.Distrust(new PipePeer(41, 0, null, null), Folder, Service));
        Assert.Contains("not the Sallyport service", RouterClient.Distrust(new PipePeer(40, 0, null, null), Folder, Service with { State = ServiceState.Stopping }));
        Assert.Contains("not set to run as the system", RouterClient.Distrust(new PipePeer(40, 0, null, null), Folder, Service with { Account = @"NT AUTHORITY\LocalService" }));
        Assert.Contains("not set to run as the system", RouterClient.Distrust(new PipePeer(40, 0, null, null), Folder, Service with { Account = null }));
        Assert.Contains("does not run from", RouterClient.Distrust(new PipePeer(40, 0, null, null), Folder, Service with { Executable = @"C:\Temp\Sallyport.Service.exe" }));
        Assert.Contains("does not run from", RouterClient.Distrust(new PipePeer(40, 0, null, null), Folder, Service with { Executable = null }));
    }

    [Fact]
    public void A_runs_worker_is_the_process_the_service_named()
    {
        Assert.Null(RouterClient.Distrust(new PipePeer(77, 0, null, null), Folder, worker: 77));
        Assert.Contains("not the router the service started", RouterClient.Distrust(new PipePeer(78, 0, null, null), Folder, worker: 77));
        Assert.Contains("not running as the system", RouterClient.Distrust(new PipePeer(77, 0, Person, null), Folder, worker: 77));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Sallyport\Sallyport.Service.exe""", @"C:\Program Files\Sallyport\Sallyport.Service.exe")]
    [InlineData(@"""C:\Program Files\Sallyport\Sallyport.Service.exe"" --flag", @"C:\Program Files\Sallyport\Sallyport.Service.exe")]
    [InlineData(@"C:\Windows\system32\svchost.exe -k LocalService -p", @"C:\Windows\system32\svchost.exe")]
    [InlineData(@"C:\Tools\thing", @"C:\Tools\thing")]
    public void A_services_executable_is_read_from_its_command_line(string line, string expected)
    {
        Assert.Equal(expected, ServiceControl.Executable(line));
    }

    [Fact]
    public void The_service_managers_record_is_read_for_a_service_every_Windows_has()
    {
        var log = ServiceControl.Config("EventLog");
        Assert.NotNull(log);
        Assert.EndsWith("svchost.exe", log.Value.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LocalService", log.Value.Account, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ServiceControl.Config("Sallyport.NoSuchService"));
    }
}
