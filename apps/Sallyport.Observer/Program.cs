using System.ComponentModel;
using System.Text.Json;

namespace Sallyport.Observer;

internal static class Program
{
    private sealed class Counters
    {
        public long ProcessPolls { get; set; }
        public long IdentityUnavailableChecks { get; set; }
        public long AccessDeniedChecks { get; set; }
        public long AttributionUnavailable { get; set; }
        public long TcpPolls { get; set; }
    }

    public static async Task<int> Main(string[] args)
    {
        if (args is ["scan", var folder])
        {
            // What a game profile made from this folder would route, and what it would leave out and why.
            try
            {
                foreach (var found in Sallyport.Windows.Apps.GameFolder.Scan(folder))
                    Console.WriteLine($"{(found.Included ? "include" : "leave out")}  {found.Path}{(found.Reason is { } why ? $"  ({why})" : "")}");
                return 0;
            }
            catch (DirectoryNotFoundException error)
            {
                Console.Error.WriteLine(error.Message);
                return 2;
            }
        }
        LearnOptions options;
        try { options = LearnOptions.Parse(args); }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Learning unavailable: Windows is required.");
            return 1;
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        long deadline = started.AddSeconds(options.Seconds).ToFileTime();
        var tracker = new ProcessTracker(options.Executables);
        var endpoints = new EndpointAggregator();
        var counters = new Counters();
        List<string> errors = [];
        bool snapshotAvailable = false;
        bool? elevated = null;
        bool warnedIdentityUnavailable = false;
        Dictionary<uint, (IdentityResult Result, long ValidatedAt)> identityCache = [];
        int cancelledByUser = 0;
        EtwObserver? etw = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Exchange(ref cancelledByUser, 1);
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;

        void PollProcesses()
        {
            snapshotAvailable = false;
            long before = DateTime.UtcNow.ToFileTimeUtc();
            IReadOnlyList<ProcessSample> samples = WindowsProcesses.Snapshot();
            tracker.Update(samples, before);
            snapshotAvailable = true;
            counters.ProcessPolls++;
            int unavailable = samples.Count(p => p.Result.State == IdentityState.Unavailable);
            counters.IdentityUnavailableChecks += unavailable;
            counters.AccessDeniedChecks += samples.Count(p => p.Result.State == IdentityState.Unavailable && p.Result.Error == 5);
            if (unavailable > 0 && !warnedIdentityUnavailable)
            {
                Console.Error.WriteLine("Some process identities are unavailable (including protected/access-denied processes); they are not counted as absent.");
                warnedIdentityUnavailable = true;
            }
        }

        void Record(NetworkObservation observation)
        {
            if (observation.TimestampFileTime < started.ToFileTime() || observation.TimestampFileTime > deadline ||
                !tracker.KnowsPid(observation.Pid)) return;
            if (!identityCache.TryGetValue(observation.Pid, out var validation) || observation.TimestampFileTime > validation.ValidatedAt)
            {
                // The validation time precedes the handle query, keeping the proved lifetime conservative.
                long validatedAt = DateTime.UtcNow.ToFileTimeUtc();
                validation = (WindowsProcesses.Query(observation.Pid), validatedAt);
                identityCache[observation.Pid] = validation;
            }
            ProcessIdentity? identity = tracker.Attribute(observation.Pid, observation.TimestampFileTime, validation.Result,
                validation.ValidatedAt);
            if (identity is null)
            {
                counters.AttributionUnavailable++;
                return;
            }
            (bool isNew, EndpointEvidence evidence) = endpoints.Add(observation, identity);
            if (isNew) Console.WriteLine($"{Label(evidence)} source={observation.Source} {evidence.Classification}");
        }

        void Drain(bool final = false)
        {
            if (etw is null) return;
            identityCache.Clear();
            // A busy sender must not keep this loop alive beyond cancellation or the next process poll.
            for (int i = 0; i < 8192 && (final || !cancellation.IsCancellationRequested) &&
                etw.TryRead(out NetworkObservation? observation); i++)
                if (observation is not null) Record(observation);
        }

        try
        {
            PollProcesses();
            elevated = WindowsProcesses.IsElevated();
            if (!elevated.Value) Console.Error.WriteLine("UDP learning unavailable: not elevated");
            else if (!cancellation.IsCancellationRequested)
            {
                etw = new(tracker, deadline);
                etw.Start();
                Console.Error.WriteLine("UDP and TCP learning: private real-time ETW session started.");
            }
            Console.Error.WriteLine($"Learning for up to {options.Seconds} seconds; TCP tables are sampled every 250 ms and can miss short connections.");
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                cancellation.Token.ThrowIfCancellationRequested();
                identityCache.Clear();
                long tableTime = DateTime.UtcNow.ToFileTimeUtc();
                foreach (NetworkObservation observation in TcpTable.Read(tableTime)) Record(observation);
                counters.TcpPolls++;
                Drain();
                if (etw?.Failure is { } failure) throw new InvalidOperationException("ETW learning failed", failure);
                if (!await timer.WaitForNextTickAsync(cancellation.Token)) break;
                PollProcesses();
            } while (true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            errors.Add(Describe(error));
            Console.Error.WriteLine("Learning failed: " + errors[^1]);
        }
        finally
        {
            try
            {
                etw?.Dispose();
                Drain(final: true);
                if (etw?.Failure is { } failure)
                {
                    string description = Describe(failure);
                    if (!errors.Contains(description)) errors.Add(description);
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or ArgumentException)
            {
                errors.Add("Cleanup/attribution failed: " + Describe(error));
            }
            finally { Console.CancelKeyPress -= cancel; }
        }

        DateTimeOffset finished = DateTimeOffset.UtcNow;
        string outcome = errors.Count > 0 ? "failed" : Volatile.Read(ref cancelledByUser) != 0 ? "cancelled" : "deadline";
        IReadOnlyList<EndpointEvidence> evidenceList = endpoints.Snapshot();
        Console.WriteLine($"Summary: {evidenceList.Count} endpoints; outcome={outcome}");
        foreach (EndpointEvidence evidence in evidenceList)
            Console.WriteLine($"{Label(evidence)} {evidence.Classification} first={evidence.FirstSeen:O} last={evidence.LastSeen:O} " +
                $"seen={evidence.TimesSeen} sources={string.Join(',', evidence.Sources.Select(s => $"{s.Key}:{s.Value}"))}");
        var selected = options.Executables.Select(path => new { path, state = tracker.SelectedPathState(path, snapshotAvailable) }).ToArray();
        foreach (var path in selected) Console.WriteLine($"Process {path.path}: {path.state}");
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        if (etw is not null) Console.WriteLine("ETW: " + JsonSerializer.Serialize(etw.Statistics));
        else Console.WriteLine("ETW loss statistics unavailable: " + (elevated == false ? "not elevated" : "ETW did not start"));
        Console.WriteLine($"Checks: identity-unavailable={counters.IdentityUnavailableChecks}, access-denied={counters.AccessDeniedChecks}, " +
            $"attribution-unavailable={counters.AttributionUnavailable}, TCP polls={counters.TcpPolls}");
        foreach (string error in errors) Console.Error.WriteLine(error);

        if (options.JsonPath is not null)
        {
            var report = new { schemaVersion = 1, sessionId = Guid.NewGuid(), startedAt = started, finishedAt = finished,
                outcome, elevated, selectedExecutables = selected, counters, etw = etw?.Statistics,
                errors, endpoints = evidenceList };
            try { await File.WriteAllTextAsync(options.JsonPath, JsonSerializer.Serialize(report, jsonOptions)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("JSON output failed: " + Describe(error));
                return 1;
            }
        }
        return errors.Count > 0 ? 1 : 0;
    }

    private static string Label(EndpointEvidence evidence)
    {
        string remote = evidence.Address.Contains(':') ? $"[{evidence.Address}]:{evidence.Port}" : $"{evidence.Address}:{evidence.Port}";
        return $"{evidence.Protocol} {remote} {Path.GetFileName(evidence.Process.ImagePath)} (PID {evidence.Process.Instance.Pid}, " +
            $"created {DateTimeOffset.FromFileTime(evidence.Process.Instance.CreationTimeFileTime).ToUniversalTime():O})";
    }

    private static string Describe(Exception error) => error is Win32Exception native
        ? $"{native.Message} (Win32 {native.NativeErrorCode})"
        : error.Message + (error.InnerException is null ? "" : ": " + Describe(error.InnerException));
}
