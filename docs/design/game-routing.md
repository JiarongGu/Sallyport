# Routing a game — design (2026-10-19)

**Why it exists.** The owner, 2026-10-19: *"I thought we do monitor for process instead check on range of address"*,
*"you can check how vpn like wtfast does"*, then, after D17: *"you can co work with codex with the design lets
continue"*. The decision this designs: driverless now — Windows' VPN platform (D16) with each game's destinations
learned from the game process itself — and a seam where a WFP driver of our own could later select by process (D17).

**How it was made.** As the client's window was (`client-ui.md`): Codex CLI 0.162.0-alpha.17.2, `codex exec -s
read-only`, on the brief `local/codex/games-brief.md`. A static read: Codex launched, installed and changed nothing.
Its answer is below the line.

**Checked by Claude against the code and the machine the same day.** These hold, measured:
- A live `VpnChannel` has no method that changes its routes (its whole surface listed from Windows' metadata): routes are
  given only at `Start*`. Whether `UpdateProfileFromObjectAsync` changes a connected profile's routes is not measured,
  so the restart path is the baseline, as the design says.
- UDP destinations need administrator. A private session on `Microsoft-Windows-Kernel-Network` (keywords 0x30) started
  under a basic-user token (`runas /trustlevel:0x20000`) was refused, "Access is denied". Elevated, it recorded
  nslookup's query as event 42 "Data sent over UDP" with the payload's `PID` = nslookup's, `daddr` 216.239.32.10 and
  `dport` 53. The reply, event 43, carried the same payload `PID` while its header said process 4. That is the
  design's rule: attribute by the payload's PID, not the header's.
- TCP remotes with their owning PID are in `GetExtendedTcpTable` unelevated; UDP's table has local endpoints only.
- The relay and the plug-in carry IPv4 only (`RelayPlugin.Decapsulate`, `Encapsulate`). Exclusion routes do not keep an
  overlapping destination off the VPN (D17).

Not yet measured, and assumed by the design: that the packaged bridge reads D14's store at `%LOCALAPPDATA%\Sallyport`
(a packaged process sees a virtualised AppData); and how many routes a channel accepts (the proposed limit is 256).

**Adopted with one change.** The observer uses a private real-time session on the manifest provider
`Microsoft-Windows-Kernel-Network` (measured above), not the classic kernel logger's `EVENT_TRACE_FLAG_NETWORK_TCPIP`
the design names. That keeps to the design's own rule: never commandeer another application's tracing session — the
system loggers are few and shared.

---

Sallyport should offer **“learn this game, then route its learned destinations through this profile.”** Process monitoring supplies the addresses; Windows’ VPN platform still selects traffic by executable path **and destination**. This cannot promise that every packet from the game uses the VPN.

This design builds on D12–D17 and the current bridge, plug-in and display-only client contract. It contains proposed behaviour and measurement gates, not new measurements. Nothing was changed or run against a game or VPN.

**1. Learning starts with a game identity and a separate launch target.** Store the selected game’s executable path, optional launcher path and arguments, approved companion executables, and profile ID. A launcher starts the game; that does not automatically make all the launcher’s traffic game traffic.

Unelevated discovery uses `CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS)`, `Process32FirstW` and `Process32NextW` for PIDs and parent PIDs. Use `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `QueryFullProcessImageNameW` and `GetProcessTimes` to validate accessible processes. Identify a process instance by PID **and creation time**, preventing PID reuse from attaching observations to another program. Access denied means “identity unavailable,” never “not running.” [Microsoft’s process snapshot structure](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w), [image-path API](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew).

Monitor before launch. Follow descendants as discovery candidates; retain their identities after the launcher exits. Associate an already-running launcher explicitly. Games started through an existing launcher or service may have no useful ancestry: allow selecting the observed game executable and remembering that association. Never infer ownership merely because two processes started together.

Review discovered executable paths before admitting them to the routing set. Browsers, updaters and anti-cheat services are not automatically companions. Windows’ current filters name executable paths: descendants need their own filters, and **all instances of an admitted path** can match. This backend cannot promise selection of just one PID.

For TCP, poll `GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_ALL`, separately for `AF_INET` and `AF_INET6`, initially every 250 ms during learning. Rows supply local/remote endpoints, state and owning PID. Ignore listeners and unspecified remotes. Snapshot polling can miss short connections; record that limitation. `GetExtendedUdpTable(..., UDP_TABLE_OWNER_PID, ...)` supplies local endpoints and ownership, **not UDP destinations**. [TCP tables](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable), [UDP tables](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable).

For useful TCP-and-UDP learning, use an **elevated, session-scoped ETW observer**:

- `StartTraceW` starts a dedicated real-time system-logger session with network and process events; `OpenTraceW`/`ProcessTrace` consume it; `ControlTraceW` queries and stops it.
- `EVENT_TRACE_FLAG_NETWORK_TCPIP` enables the classic TCP/IP and UDP/IP events. UDP IPv4 send/receive events are types 10/11; IPv6 types 26/27. Their payload contains PID, addresses and ports.
- Attribute using the **payload PID**, not blindly `EventHeader.ProcessId`: network events can be emitted by another thread. Decode by provider/version/opcode, using TDH metadata where applicable.
- Collect no packet payloads or stacks. Discard unrelated events before IPC or persistence. Report event loss, unsupported schemas and attribution gaps.

These schemas are documented; their attribution and completeness on Sallyport’s supported Windows builds still require measurement. Kernel trace control requires administrator privileges; ordinary ETW permissions do not remove that requirement. Never commandeer another application’s tracing session. [UDP event documentation](https://learn.microsoft.com/en-us/windows/win32/etw/udpip), [payload fields](https://learn.microsoft.com/en-us/windows/win32/etw/udpip-typegroup1), [trace permissions](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew).

Without elevation, offer **“TCP observations only; UDP learning unavailable.”** Cached learned routes remain usable, with that limitation displayed. Firewall auditing, WFP event subscriptions and packet capture are possible research alternatives, but none is an established unelevated replacement here. Do not inject a socket hook or infer UDP remotes from local-port ownership.

Each observation becomes an exact IPv4 `/32`; IPv6 observations are retained for future `/128` support. Never expand automatically to `/24`, ASN, CDN ownership or reverse-DNS neighbourhoods: that would refuse unrelated programs’ access to many more servers.

Persist per-game endpoint evidence: address, protocol/ports, executable identity, first/last observation, learning session, source, and accepted/excluded/pinned status. Keep endpoints independent of the chosen VPN profile; validate them against each profile when connecting. No payloads, command-line captures or automatic telemetry uploads.

Suggested ageing: endpoints unseen for 30 days become stale and leave the **next** plan unless pinned. Never withdraw an active route because its age crossed a threshold. Details allow exclusion, restoration, pinning and forgetting; advanced prefix widening requires explicit editing and validation. Ordinary use requires no address entry. Start with a proposed 256-route limit, measured before adoption; exceeding it refuses the plan instead of widening silently.

**2. A routing session has distinct learning, preparation and active generations.** Initial learning happens directly, with the VPN disconnected. The person exercises login, matchmaking, gameplay and voice, then finishes learning. “Learned” means “these destinations were observed,” not “all future destinations are known.”

The normal first route requires the game’s current session to end. Prepare the VPN before launching it again. Existing TCP connections cannot be transferred transparently to a different source address; UDP sessions can also break when their source changes. Never terminate or suspend the game automatically.

Preparation validates the executable set, fresh endpoint plan, profile support, direct connectivity, loopback exemption and address-family support. Reject routes covering:

- Any active or eligible failover VPN-server address.
- Guard probes and required direct infrastructure, including configured DNS resolvers.
- Loopback, multicast, broadcast, link-local and local-network destinations in the initial implementation.
- Default routes, or conflicting active Sallyport packet sessions.

Resolve tunnel remotes before planning and validate the **actual transport peer**, not merely a previous DNS answer. Revalidate on failover and network change. Exclusion routes do not rescue an overlapping plan: D17 disproves relying on them here.

The bridge connects the tunnel, starts its relay and guard, then creates the split VPN channel with explicit TCP and UDP executable filters. Launch only after platform connection and relay readiness are confirmed. Initially allow one packet-routing session; refuse simultaneous proxy and packet connections using the same profile until duplicate-session behaviour or tunnel sharing is measured.

An unknown destination is initially **outside the routes and therefore direct**, under the measured split policy. Observation occurs after activity begins; the learner cannot hold its first packet. Queue it for the next plan and display “New destination used directly.” Neither TCP nor UDP flow migration is assumed.

Maintain `desiredRevision` separately from `activeRevision`:

| Update capability | Behaviour and cost |
|---|---|
| Live update measured to work | Apply a validated revision under the guard, coordinating platform routes, executable filters and the plug-in’s own destination allowlist. Preserve the previous revision for rollback. Existing flows still need separate migration testing. |
| Restart required or unknown | Accumulate changes; show “Restart routing to apply.” Disconnect the channel, confirm withdrawal, configure the new revision and reconnect. Expect interruption and game reconnection. |

`StartWithTrafficFilter` receives routes at channel creation. `UpdateProfileFromObjectAsync` documents profile updates, **not a guarantee that an active channel changes**. Mutating a collection or receiving `Ok` is insufficient evidence. Keep the restart path as the baseline; do not introduce privileged route-table edits to bypass this gate. [Channel creation](https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnchannel.startwithtrafficfilter?view=winrt-26100), [profile update API](https://learn.microsoft.com/en-us/uwp/api/windows.networking.vpn.vpnmanagementagent.updateprofilefromobjectasync?view=winrt-26100).

Re-learning can continue alongside routing when ETW is available, but route changes default to application between game sessions. Coalesce discoveries instead of restarting per packet.

A launcher or companion requiring the same routed servers must either join the executable filter set or operate after routing stops. Adding it grants access to **all prefixes in that session**. Other unlisted programs remain refused at those prefixes. Do not automatically admit every affected process.

**3. The client remains unelevated; the packaged bridge owns packet routing.** Keep proxy-mode runtime ownership unchanged. For games:

| Component | Responsibility |
|---|---|
| Client host | Game configuration, process discovery, learning policy, view-model construction, intents and bridge supervision. |
| Packaged full-trust bridge | User-scoped profile loading, `TunnelSession`, relay, VPN management, route validation and direct-path guard. |
| Elevated observer | ETW lifecycle, process-instance attribution and filtered endpoint metadata only. |
| Plug-in | Packet relay and validation; no tunnel, profile keys, learning or independent safety timers. |

The observer accepts bounded observation requests, not commands to launch programs, read arbitrary files, manage routes or open profiles. Stop tracing on cancellation, client loss or its session deadline. Installer elevation separately establishes D16’s package loopback exemption.

Profiles stay where D14 puts them, sealed for the current user. The bridge runs as that user and opens a stored profile by validated ID. It must use the existing DPAPI purpose string. No plaintext profile crosses client IPC; no profile reaches the elevated observer or plug-in. Introducing an observer does not introduce an elevated tunnel runtime.

Use versioned, length-bounded named-pipe messages between client, bridge and observer. Restrict pipes to the intended user/logon session, reject remote clients, validate peer identity and process lifetime, and reject unknown commands. Initially support same-user elevation. Bridge messages contain profile IDs, approved executable identities, plans, revisions and sanitized outcomes. Internal generations reject late events; the page needs no session identifiers.

Keep the measured token-authenticated loopback relay, rotating its token per connection generation and never logging it. Any live-update control messages need explicit framing and acknowledgements.

The bridge runs D13’s guard outside the selected executable set: preflight direct probes; 500 ms probe deadline; 250 ms spacing; trip after three consecutive failures or four of six. Teardown disconnects, confirms down, removes the owned profile, then checks direct recovery for up to ten seconds. Unknown platform status is never treated as disconnected.

The client supervises bridge liveness and invokes a packaged recovery command on bridge failure. The bridge tears down on client-pipe loss. Recovery operates only on identified Sallyport-owned profiles. Startup reconciles stale sessions before connecting. Simultaneous supervisor failure remains a limitation, not a promised machine-wide kill switch.

Do **not** move the client into MSIX for this feature. That adds packaging and data-location migration without removing ETW elevation or the need for supervision. Reconsider later as a distribution decision.

**4. Games belong in Apps, using the existing host-authored contract.** Add `AppKind = 'game'`; preserve proxy kinds. Replace the blanket “games are not routed yet” note with the host’s current boundary explanation.

The row shows game, profile, process status, routing status and one permitted primary action. Details show executable companions, learned destinations, pending changes, shared-server warning and checks.

Proposed additions, using the existing `Action`, `Fact`, editor and confirmation conventions:

```ts
type GamePhase =
  | 'notLearned' | 'learning' | 'learned' | 'preparing'
  | 'routed' | 'partial' | 'updatePending' | 'updating'
  | 'restartRequired' | 'relearning' | 'reconnecting'
  | 'stopping' | 'refused' | 'guardTripped'
  | 'failed' | 'recoveryUnknown';

interface CheckView {
  phase: 'unchecked' | 'checking' | 'checked';
  result: 'holds' | 'failed' | 'unavailable' | null;
  text: string;
  checkedAt: string | null;
}

interface GameView {
  phase: GamePhase;
  tone: Tone;
  statusText: string;
  detailText: string | null;
  processText: string;
  backendText: string;
  coverageText: string;
  activeRevision: number | null;
  desiredRevision: number;
  processes: GameProcessView[];
  destinations: GameDestinationView[];
  checks: Record<string, CheckView>;
  facts: Fact[];
  actions: Record<string, Action>;
}
```

`GameProcessView` supplies path, role, admission and identity-check result. `GameDestinationView` supplies prefix, evidence, timestamps, stale/pinned/excluded status, active/pending status and host-authored actions.

Add intents for learning start/finish/cancel, process selection, companion approval/removal, destination editing, route preparation, applying changes, stopping, retrying and emergency recovery. Reuse editor/confirmation tokens. Native executable selection remains a host operation.

The host alone decides transitions, labels, action availability and notices. “Routed” says **“Learned destinations routed”**; observer loss says **“Routing continues; learning unavailable.”** New direct traffic becomes `partial`. Guard teardown retains `guardTripped`; teardown that cannot be verified becomes `recoveryUnknown`. Missing executables/profiles, unsupported IPv6 and denied elevation receive explicit reasons. A deliberate stop returns to `learned` or `notLearned`.

**5. The driver seam belongs below the game controller.** The client host talks to `IGameRoutingController`: prepare, start, stop, apply pending policy, recover and observe state. Its input is a game selection plus profile ID—not a mandatory prefix list.

Its replaceable backend reports capabilities such as destination learning required, live policy update, supported address families and selection scope. Today, `LearnedPrefixVpnBackend` combines the learner and platform bridge. Later, `WfpProcessBackend` selects processes directly and can make learning optional.

Below that, a selected-packet interface connects outbound IP packets and returned packets to the tunnel. A future driver must implement flow attribution, exclusions, packet return/injection and loop prevention; that work is not hidden behind the word “redirect.” Keep native driver code and signing lifecycle outside `src/Sallyport`. Window and intent semantics remain unchanged.

**6. Build and measure in these increments.** Each record names Windows build, game build, executable set, active revision, what was sent, what returned and cleanup result. Use endpoint metadata and counters, never profile contents or game payload dumps.

| Step | Send/exercise | Required evidence |
|---|---|---|
| Observer | Known TCP/UDP IPv4/IPv6 traffic, short-lived children, launcher handoff and a real game session; baseline direct traffic to a controlled endpoint beyond the VPN server | Correct endpoints and process instances; access denial/loss distinguished from absence. Controlled endpoint sees direct source. |
| Stored plan | Learn a real game across login, match and voice; persist/reload its plan; connect the real VPN tunnel without installing routes | Exact-prefix evidence survives; unsafe overlaps rejected; tunnel request/reply succeeds independently. |
| Fixed routing | Fresh game session plus listed/unlisted diagnostic executables, TCP and UDP through the real server | Listed/routed traffic exits through VPN; unlisted/routed is refused; unrouted stays direct; game exchanges continue. |
| Updates | Add/remove a destination and companion on an active channel, then test the restart branch | Observe actual packet paths and plug-in acceptance, existing-flow behaviour, interruption and rollback—not just API return codes. |
| Recovery | Kill observer, client and bridge separately; interrupt tunnel/direct path; sleep/resume and change network | Intended teardown, route withdrawal and direct recovery; failed/unavailable checks remain distinct. |
| Client | Exercise the same game through every visible lifecycle state | Window/tray agree with backend evidence; duplicate or stale intents do not launch late or apply obsolete plans. |

For the game itself, correlate bridge counters with real VPN-server egress/reply metadata. If server-side visibility is unavailable, mark exit verification unavailable; a diagnostic curl result alone does not prove the game’s path. Record measurements in TASKS/the implementation record before calling a step done.

**7. Compatibility and exclusions constrain the release.** Anti-cheat compatibility is measured per game/build. Sallyport uses ordinary process metadata and network observation: no injection, memory inspection, debugging, suspension or game-file changes. That reduces intrusion but does not establish universal compatibility.

DNS remains direct initially. System DNS-service traffic cannot safely be attributed to the requesting game from endpoint ownership alone. Do not route the resolver service or alter machine-wide DNS to compensate. DNS answers are hints; actual process traffic supplies destinations.

The current relay is IPv4-only. Observe IPv6, but refuse default routing preparation for a game observed using it until IPv6 forwarding is implemented and measured. Newly observed IPv6 makes coverage partial; never disable IPv6 globally.

Captive portals or failed direct preflight prevent connection; direct-path loss triggers teardown without claiming the VPN caused it. Network changes invalidate readiness checks.

CDN and anycast destinations can change without notice, and the VPN exit may receive different matchmaking answers from the direct path. Re-learning must remain available while routed. Exact routes limit collateral refusal but cannot guarantee stable coverage or improved latency. Never chase churn by routing an ASN, accepting a default route, or concealing direct fallback.
