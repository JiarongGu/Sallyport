using System.ComponentModel;

namespace Sallyport.Windows.PacketFilter;

/// <summary>
/// One adapter's packets taken from the driver: drained, and passed on unchanged except those a caller keeps. Taking
/// only what Windows sends (<c>receivePath: false</c>) is what the router does — a reply it hands up still reaches its
/// socket (measured, docs/design/router.md change 4). Stopping, or this process ending however it ends, gives the
/// adapter back (measured: a closed handle leaves it in mode None).
/// </summary>
public sealed class Interception : IDisposable
{
    private readonly PacketDriver _driver;
    private readonly EventWaitHandle _signal = new(false, EventResetMode.ManualReset);
    private readonly PacketBatch _batch;
    private readonly int[] _up, _down;
    private readonly Dictionary<int, int> _readErrors = [];
    private bool _stopped;

    public Interception(PacketDriver driver, ulong adapter, int batchSize, bool receivePath)
    {
        _driver = driver;
        Adapter = adapter;
        _batch = new PacketBatch(batchSize);
        _up = new int[batchSize];
        _down = new int[batchSize];
        driver.SetPacketEvent(adapter, _signal);
        driver.SetMode(adapter, AdapterMode.SentTunnel | (receivePath ? AdapterMode.ReceiveTunnel : AdapterMode.None));
    }

    public ulong Adapter { get; }

    public long Sent { get; private set; }
    public long SentBytes { get; private set; }
    public long Received { get; private set; }
    public long ReceivedBytes { get; private set; }
    public long Kept { get; private set; }
    public long Reads { get; private set; }
    public long EmptyReads { get; private set; }
    public long Unknown { get; private set; }
    /// <summary>Passed on with the driver's own count of what it took.</summary>
    public long Confirmed { get; private set; }
    /// <summary>Passed on with the driver silent — what it answers for a send (measured).</summary>
    public long Unconfirmed { get; private set; }
    public long Refused { get; private set; }
    public int MaxBatch { get; private set; }
    public int LargestOut { get; private set; }
    public int LargestIn { get; private set; }
    public IReadOnlyDictionary<int, int> ReadErrors => _readErrors;

    /// <summary>Waits for the driver's signal, at most <paramref name="milliseconds"/>.</summary>
    public void Wait(int milliseconds)
    {
        _signal.WaitOne(milliseconds);
        // Reset before draining: a packet queued after the reset signals again, so none is left waiting.
        _signal.Reset();
    }

    /// <summary>Everything queued, passed on — except packets <paramref name="keep"/> answers true for, which go no
    /// further. A packet seen by <paramref name="keep"/> is valid only until it returns.</summary>
    /// <summary>When the batch now being handed to <c>keep</c> came out of the driver (a Stopwatch timestamp): every
    /// packet in it had been sent by then.</summary>
    public long BatchTakenAt { get; private set; }

    public void Drain(Func<DriverPacket, bool>? keep = null)
    {
        while (_driver.Read(Adapter, _batch) is var count && count > 0)
        {
            BatchTakenAt = System.Diagnostics.Stopwatch.GetTimestamp();
            Reads++;
            MaxBatch = Math.Max(MaxBatch, count);
            int ups = 0, downs = 0;
            for (var i = 0; i < count; i++)
            {
                var packet = _batch[i];
                if (keep?.Invoke(packet) == true) { Kept++; continue; }
                switch (packet.Direction)
                {
                    case PacketDirection.Send:
                        _down[downs++] = i; Sent++; SentBytes += packet.Length; LargestOut = Math.Max(LargestOut, packet.Length);
                        break;
                    case PacketDirection.Receive:
                        _up[ups++] = i; Received++; ReceivedBytes += packet.Length; LargestIn = Math.Max(LargestIn, packet.Length);
                        break;
                    default: Unknown++; break;
                }
            }
            PassOn(downs, toNetwork: true);
            PassOn(ups, toNetwork: false);
        }
        if (_driver.LastReadError == PacketDriver.EmptyQueue) EmptyReads++;
        else if (_driver.LastReadError != 0) _readErrors[_driver.LastReadError] = _readErrors.GetValueOrDefault(_driver.LastReadError) + 1;
    }

    // Three answers a send can have (rule 6): the driver's own count, its silence, or its refusal.
    private void PassOn(int count, bool toNetwork)
    {
        if (count == 0) return;
        try
        {
            if (_driver.Send(Adapter, _batch, (toNetwork ? _down : _up).AsSpan(0, count), toNetwork) is { } took)
            {
                Confirmed += took;
                Refused += count - took;
            }
            else Unconfirmed += count;
        }
        catch (Win32Exception) { Refused += count; }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        // New packets bypass this program first; then what was already queued goes on.
        _driver.SetMode(Adapter, AdapterMode.None);
        Drain();
        _driver.SetPacketEvent(Adapter, null);
    }

    public void Dispose()
    {
        Stop();
        _batch.Dispose();
        _signal.Dispose();
    }

    public override string ToString() =>
        $"out {Sent} ({SentBytes / 1024} KiB)  in {Received} ({ReceivedBytes / 1024} KiB)  kept {Kept}  reads {Reads} (+{EmptyReads} empty)"
        + $"  max batch {MaxBatch}  passed on: confirmed {Confirmed}, unconfirmed {Unconfirmed}, refused {Refused}  unknown {Unknown}"
        + $"  largest frame out {LargestOut} in {LargestIn}"
        + (_readErrors.Count > 0 ? $"  read errors {string.Join(",", _readErrors.Select(e => $"{e.Key}x{e.Value}"))}" : "");
}
