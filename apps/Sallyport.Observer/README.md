# Sallyport.Observer

> **Not in the product.** Part of the driverless route (D16), kept for if the packet driver were ever refused (D18,
> TASKS §2); Sallyport routes games through the driver (`docs/design/architecture.md`).

A Windows, BCL-only endpoint learner. It reads process identity and endpoint metadata; it never reads packet payloads,
profiles, keys, command lines or game files. It does not launch a game, install routes or start a VPN.

```powershell
dotnet run --project apps/Sallyport.Observer -- learn --exe C:\Windows\System32\curl.exe --for 10 --json observations.json
```

Supply one or more full executable paths after `--exe`, or repeat `--exe`. `--for` is a whole number of seconds from
1 to 3600. Start learning before exercising the selected program. Ctrl+C ends learning and writes the summary and
optional JSON. Exit codes: 0 for deadline/Ctrl+C, 1 for a failed check or output, 2 for invalid arguments.

Processes and IPv4 TCP owner tables are polled every 250 ms. Paths match case-insensitively after `Path.GetFullPath`;
PID plus creation FILETIME identifies an instance. Descendants remain watched after their parent exits. A snapshot
PID recycled before its handle is opened cannot borrow the old process's parent. Access denied, failed snapshots,
and unprovable ancestry/lifetime are unavailable checks, never evidence of absence. An exited parent's last validated
lifetime can prove an older child's ancestry; children born after that bound require an accessible current parent.

Unelevated, the console says `UDP learning unavailable: not elevated` once and continues with IPv4 TCP tables.
Elevated, it additionally creates its own uniquely named real-time session on `Microsoft-Windows-Kernel-Network`
(`7DD42A49-5329-4832-8DFD-43D979153A88`), keywords `0x30`. It never attaches to or stops another application's session.
The session is stopped in `finally`, including startup failure, Ctrl+C, deadline and consumer failure; the dedicated
`ProcessTrace` thread is closed and joined. No ETL file is written. Hard process termination cannot run a `finally`.

The following **version 0 manifest events** supply data endpoints (task 10 = TCPIP, 11 = UDPIP):

| Protocol | IPv4 send / receive | IPv6 send / receive | Opcode send / receive |
|---|---|---|---|
| TCP | 10 / 11 | 26 / 27 | 10 / 11 |
| UDP | 42 / 43 | 58 / 59 | 42 / 43 |

Established on Windows build 26300 with
`wevtutil gp Microsoft-Windows-Kernel-Network /ge:true /gm:true /f:xml`: the installed manifest gives event IDs,
versions, TCPIP/UDPIP tasks, send/receive opcodes, IPv4/IPv6 keywords and messages. These are manifest IDs, not the
classic kernel logger's event types. Runtime checks verify version/task/opcode/family keyword. Other control events
are ignored; unknown data events and incompatible properties increment `unsupportedSchemas`.

TDH retrieves properties **by name** using `TdhGetPropertySize` and `TdhGetProperty`: payload `PID`, and `daddr`/`dport`
for sends or `saddr`/`sport` for receives. Attribution never uses the event header's PID. Addresses and two-byte ports
are network order. The session requests system timestamps and `ProcessTrace` supplies FILETIME timestamps. Events
must fall within a watched instance's validated lifetime; buffered events may belong to an earlier instance of a PID.

New endpoints print once per protocol/address/port/instance, with their first source (`tcp-table` or `etw`). The final
summary merges both sources and reports UTC first/last times, total observations, source counts and classification.
`TimesSeen` counts table rows sampled and ETW send/receive events, **not packets, connections or bytes**.

Classification: `routable`, `loopback`, RFC1918/shared-address `private`, `link-local`, `multicast`, limited `broadcast`,
`ipv6-unsupported`, `unspecified` or `reserved`. Every IPv6 address, including mapped IPv4, remains
`ipv6-unsupported`. `routable` is an address classification, not route approval: directed broadcasts on a particular
subnet, VPN peer overlap and other local-network exclusions still require the future routing planner's checks.

## A game's folder

`Sallyport.Observer scan <game folder>` lists what a game profile made from that folder would route and what it leaves
out, and why (`Sallyport.Runtime.GameFolder`). `learn --folder <game folder>` watches every program that scan includes.

## JSON schema (version 1)

`--json` writes endpoint metadata only, replacing the destination file. No directory is created automatically.

- `schemaVersion`, `sessionId`, `startedAt`, `finishedAt`, `outcome` (`deadline`, `cancelled`, `failed`), `elevated`
  (null if the token could not be checked).
- `selectedExecutables`: `{ path, state }`; state is `running`, `not-running`, or `identity-unavailable` at the last
  successful process poll. If any inaccessible process could be the missing selected path, its state is unavailable.
- `counters`: `processPolls`, `identityUnavailableChecks`, `accessDeniedChecks`, `attributionUnavailable`, `tcpPolls`.
  Identity counters count checks across polls; attribution counts unprovable observations for known watched PIDs.
- `etw`: null when ETW was not started; otherwise `sessionName`, session `cleanup`, `statisticsAvailable`, `eventsLost`,
  `logBuffersLost`, `realTimeBuffersLost`, `bufferCallbacks`, `callbackEventsLost`, `callbackBuffersLost`, `queueDrops`,
  `unsupportedSchemas`. Missing controller statistics are null; callback loss values are null until a callback ran.
  Controller event/buffer loss comes from `ControlTraceW` stop statistics; buffer callbacks retain header loss counters
  (`EVENT_TRACE_LOGFILE.EventsLost` itself is documented as unused). Event loss uses the maximum available cumulative
  counter, not their sum. The bounded 8192-observation queue has a separate drop counter.
- `errors`: check/cleanup failures. Loss and unsupported schemas describe incomplete coverage even if `outcome` is
  `deadline`; they are not a promise of complete learning.
- `endpoints`: `{ protocol, address, port, classification, process, sources, firstSeen, lastSeen, timesSeen }`.
  `process` is `{ instance: { pid, creationTimeFileTime }, imagePath }`; FILETIME is the exact Windows creation value
  in 100 ns units since 1601 (use a 64-bit integer). `sources` maps each source name to its observation count.
  Dates are ISO 8601 UTC. IPv6 addresses are retained as strings; console endpoint labels use brackets.

## Coverage and measurement

Polling can miss short connections and processes, descendants created and lost between polls, and launcher/service
handoffs without observable ancestry. TCP tables are IPv4-only in this step; IPv6 learning requires elevated ETW.
Creation-time validation deliberately drops events in an unproved exit/reuse gap. It cannot infer which program
requested service-owned DNS traffic. ETW permissions, schemas, loss and attribution gaps are reported separately.

Implemented by Codex (`local/codex/observer-brief.md`); reviewed and measured by Claude the same day (below).

Measured 2026-10-19, Windows 10.0.26300, unelevated: a five-second learner watched
`C:\Windows\System32\curl.exe` (PID 10228, creation `2026-10-19T11:32:36.4548307Z`). curl connected directly to a local
test listener at `127.0.0.1:9125`; the listener held the connection for 1.2 seconds and returned an empty HTTP success
response. The learner printed that one endpoint once, classified it `loopback`, and summarized five `tcp-table`
observations with UTC first/last times. JSON schema 1 matched the address, port and instance. Both processes exited 0;
the learner ended at its deadline (`11:32:36.0674831Z` to `11:32:41.1748752Z`). UDP was explicitly unavailable,
unreadable identities were reported, attribution gaps were zero, and no ETW session was started or left to clean up.
This verifies local TCP metadata and deadline cleanup, not remote routing, UDP, IPv6, Ctrl+C or elevated cleanup.

**Review, 2026-10-19.** One defect, measured and fixed: every TCP event names the REMOTE end `daddr`, whatever its
direction — curl to 34.117.59.81:443 recorded sent (10), received (11) and connection attempted (12) all with that
address as `daddr` and this machine as `saddr` — while a UDP receive (43) names the remote `saddr`. The manifest's own
messages say the opposite for TCP. Reading `saddr` for a TCP receive had learned this machine's address as a
destination; the schema now names the remote property per event (`EtwSchema`), connection attempts (12, 28) are read
too — an unanswered connect is seen only there — and a test pins the measured fields. A watched program seen during
the run and gone at its end now reports "seen, since exited", unless a watched PID is still present but unreadable.
Open: a killed observer leaves its private session running; recovery must find and stop `Sallyport-Observer-*`
sessions whose process is gone.

**Measured elevated, 2026-10-19, Windows 10.0.26300.**
- Controlled: watching a copy of curl and a copy of nslookup for 12 s while curl fetched ipinfo.io over HTTPS and
  nslookup asked ns1.google.com over UDP. Two endpoints and nothing else: TCP 34.117.59.81:443 from both sources
  (tcp-table 2, etw 8), UDP 216.239.32.10:53 from etw alone (3). Session stopped; 0 events and 0 buffers lost.
- Zenless Zone Zero (CN server) for 60 s, watching the game and its launcher `HYP.exe`: 35 endpoints. The game's
  gameplay was UDP 47.123.84.37:20501 (70 etw events), the same server a hand-made trace saw half an hour earlier, and
  in no TCP table; its TCP was HTTPS to five addresses plus two loopback helpers. `HYPHelper.exe`, started by the
  launcher, was followed as its descendant; the launcher's own endpoints (CDNs, APIs, the local resolver) are separate.
  Session stopped; 0 events and 0 buffers lost.

Build and tests here: `dotnet build Sallyport.slnx` clean; `dotnet test Sallyport.slnx` passes observer 60, client 21,
library 161. The 25 library failures Codex saw were its sandbox's certificate store (`TestPki.Persisted`), not code.

Microsoft API references: [TDH property retrieval](https://learn.microsoft.com/en-us/windows/win32/api/tdh/nf-tdh-tdhgetproperty),
[consumer layout and timestamp conversion](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew),
[session statistics](https://learn.microsoft.com/en-us/windows/win32/etw/event-trace-properties),
[session stop](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-controltracew).
