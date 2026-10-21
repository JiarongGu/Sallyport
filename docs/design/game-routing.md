# Routing a game — design (2026-10-19)

> **Superseded (2026-10-21):** the driverless route designed here (D16) is not the product's; a game is routed by the
> packet driver (D18, `router.md`, `architecture.md`). Kept as the driverless route's record and for learning a game's
> endpoints. The names it proposes (`IGameRoutingController`, the backends) were never built, and profiles are kept in
> the install folder's data (D25), not `%LOCALAPPDATA%`.

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

---

## Amendment 1 — host-based and peer-to-peer games (2026-10-19)

**Why.** The owner: *"even this game is stable connection but for game like helldiver its depending on the host (but we
cannot run helldiver because it got conflict with bun) so we need to incount of this but use zenless as a test ground
for now"*, then *"helldiver can even change the host during the game if the host leaves"*.

**How it was made.** Codex, `codex exec -s read-only`, on `local/codex/p2p-brief.md`, which carried the Zenless Zone Zero
observation below. Its answer follows these notes.

**Measured the same day, for it.**
- Zenless Zone Zero, CN server, 45 s of a live session, a private session on `Microsoft-Windows-Kernel-Network`
  attributed by payload PID: gameplay is UDP to one endpoint (38 sent, 33 received) that the TCP table never shows; its
  TCP is HTTPS to a few gateways plus loopback to local helpers. A dedicated-server game, and the test ground.
- The VPN server's NAT, through the tunnel (`Sallyport.Probe --nat`, STUN binding requests to three servers of two
  operators from one tunnel port): one public address and the same public port for all three, and that port equal to
  the tunnel's own; the next tunnel port mapped one higher. So its **mapping is endpoint-independent and preserves
  ports**, the behaviour peer-to-peer traversal needs. Its **filtering** — whether a peer never sent to may send in, which
  hosting needs — could not be checked: no public STUN server answers from a second address. Its mapping lifetime was
  not measured.

**Adopted, with the owner's second point made explicit.** A host that leaves mid-match hands the session to another
player: new peer addresses appear *during* a session, not only between sessions. A learned-destination backend sends
them direct until a session boundary — exactly the moment that matters — so for host-migrating games only first-packet
selection by process (the driver backend) covers play. The driverless backend labels such a game "Services routed;
peers may be direct" and never more.

Amendment to `docs/design/game-routing.md`, 2026-10-19. Proposed design; no files changed or tests run. Measurements below are the owner’s supplied evidence.

**1. Extend §§1–2: classify observed behaviour, not the game’s name.**

The Zenless measurement supports the learned-prefix approach: in 45 seconds, gameplay exchanged 38 outbound and 33 inbound UDP packets with `47.123.84.37:20501`, invisible to the TCP table. HTTPS gateways and loopback helpers were separate traffic. This establishes one stable gameplay destination in that interval; it does not establish coverage of future sessions. `HYP.exe` remains a launch target, not automatic routing membership.

For each attributed process instance and local UDP port, record distinct remote endpoints, simultaneous remotes, first/last activity and changes between explicit learning sessions. Compare destination reuse across sessions. One port exchanging with several changing residential-network addresses suggests peers; repeatedly reused hosting-network destinations suggest dedicated servers or relays. Matchmaking HTTPS followed by changing UDP remotes strengthens the peer hypothesis.

These are clues, not identification. Dedicated infrastructure can churn; peers can use cloud hosts; relays can hide peers behind stable destinations. Address-ownership enrichment is optional supporting evidence, with source/date and “unavailable” when absent. Never expand routes from ownership information.

Show **“Stable destinations observed,” “Changing peers suspected,” “Mixed traffic,” or “Insufficient evidence”**, with session counts and reasons. Separately show observation gaps. Permit the person to select “I join” or “I host”; packet observation alone cannot reliably establish the game’s role. Residential peer addresses should remain session-scoped by default, with explicit retention for cross-session comparison.

**2. Amend §2: driverless coverage is conditional.**

`LearnedPrefixVpnBackend` can route validated, previously observed matchmaking or relay destinations. Label that **“Learned services routed; new peers may connect directly.”** Routing matchmaking does not route the peers returned by matchmaking.

A game’s existing relay mode may make stable-destination routing useful, but Sallyport cannot force that mode merely by routing addresses. Measure actual gameplay through the relay, destination stability and direct fallback before describing coverage. Shared platform services need separate executable admission and expose the existing all-instances/shared-destination limitations.

Learning a new peer occurs after its first packet. Even a successful live profile update cannot recover that packet; changing the path can change the source address/port visible to the peer and invalidate traversal or session state. Live updates remain unmeasured. Test their actual packet paths and session effects, but do not make them the peer-routing strategy.

Do not automatically apply newly learned peer routes during a session. Offer application at a deliberate session boundary, while explaining that the next session may choose entirely different peers. Driverless mode cannot promise complete peer coverage, VPN-only gameplay, or hosting reachability. Known-address exceptions require explicit selection and measured results.

**3. Expand §5: `WfpProcessBackend` needs connection ownership and bidirectional forwarding.**

The driver backend must select every supported external destination of admitted processes before the first packet escapes, including previously unseen UDP peers, matchmaking, discovery and relays. Destination learning becomes diagnostic. Prepare enforcement before launch; an already-running game requires a fresh session for a first-packet guarantee.

Process identity, socket/flow ownership and policy must remain associated across asynchronous traffic, process exit and PID reuse. Until ownership or tunnel readiness is established, hold within bounded limits or refuse/drop selected traffic; never silently send it directly. Preserve explicit loopback/local-network policy and transport-loop exclusions. Unsupported address families prevent a complete-coverage claim. System DNS and shared platform processes retain their separately disclosed scope.

NAT discovery, rendezvous and hole-punch traffic owned by admitted processes must traverse the same VPN path as gameplay. The externally discovered address/port must correspond to the mapping peers actually reach. Exact WFP interception, socket semantics, packet reinjection and traversal compatibility are implementation questions requiring measurement, not capabilities established by naming this backend.

Returned tunnel packets need validated reverse-flow delivery. Hosting also needs delivery to an authorized bound socket where no outbound peer flow exists, with explicit listener policy and firewall integration. Do not infer a recipient from an ambiguous port match.

The VPN server must forward the required traffic and provide usable return paths. Outbound NAT mappings may support replies or hole punching; they do not establish unsolicited inbound reachability. Hosting may require an assigned reachable address, explicit port forwarding, or a compatible relay. Server NAT/filtering behaviour, mapping lifetime and port preservation must be measured. Sallyport cannot create unavailable server-side reachability.

Keep traversal mappings and the external identity stable during a session. Tunnel reconnection, exit changes or lost mappings require recovery/rejoining; transparent migration is not promised.

**4. Extend §4’s contract.**

Add host-authored topology assessment, supporting evidence, observation completeness, requested role, coverage scope and inbound reachability. Coverage distinguishes `learnedDestinations`, `servicesOnly` and `processTraffic`; it never derives from topology alone.

Backend capabilities separately describe first-packet enforcement, supported families, return delivery, unsolicited inbound delivery and server forwarding. Each capability/readiness check uses `holds`, `failed` or `unavailable`, with evidence and scope. An inferred topology is not a successful reachability check.

The row must distinguish **“Learned destinations routed,” “Services routed; peers may be direct,”** and **“Process traffic routed.”** Hosting adds **“Inbound verified,” “Inbound failed,”** or **“Inbound not checked.”** “Process traffic routed” requires measured enforcement and does not itself mean hosting works. `partial`, `refused` and recovery states retain concrete reasons. Actions include choosing role, preparing before launch and testing reachability.

**5. Extend §6 and refusals.**

Keep Zenless as the real-game test ground for observation and stable-destination routing. Repeat login, gameplay and relaunch measurements; correlate its actual traffic with tunnel/server evidence. Do not treat it as a peer-hosting test.

Without Helldivers 2, build a BCL-only diagnostic peer application with generated test data, controlled rendezvous/relay fixtures and cooperating external peers behind different NATs. Against a real OpenVPN server, measure:

- Fresh, unpredictable destinations across sessions and multiple remotes sharing one local UDP port; verify the first packet’s source and path.
- Traversal, replies, idle mapping expiry, peer changes and relay fallback.
- Hosting with unsolicited inbound traffic, separately from replies after outbound traffic; exercise permitted and blocked server forwarding.
- Driverless route updates mid-flow: direct first packets, resulting external tuples, continuity, interruption and rollback.
- Tunnel loss, process reuse and IPv4/IPv6 boundaries; distinguish intentional refusal from missing observation.

Record server configuration, sent/returned metadata, external mappings and cleanup. Missing remote visibility means verification unavailable. Harness success establishes mechanisms; a later runnable peer game must establish game compatibility.

Refuse claims of universal peer support, guaranteed hosting, automatic relay selection, seamless source changes or Helldivers compatibility without measurement. Refuse broad prefix expansion, machine-wide routing/DNS changes, anti-cheat bypasses and unrelated platform-process admission as shortcuts. Helldivers remains untested here; its conflict with bun is not justification to bypass it.

---

## Amendment 2 — game profiles, and running a game with a VPN profile (2026-10-19)

**The owner.** *"so if the processer is attached we need to route all trafic, and we should be able to scan a game
folder for all of its possible executables"*, then *"and we should be able to configubale a game profile (so include
exetuables or exclude) and when run the vpn we can select a profile of game and a profile of vpn to run"*.

**A game profile** (`Sallyport.Runtime.GameProfile`, kept by `GameProfileStore` beside the VPN profiles) is a name, the
game's folder, and every executable under that folder — each included or left out by the person. `GameFolder.Scan`
finds them all, subfolders included: a game's helpers sit beside it (Zenless Zone Zero's in-game browser
`ZFGameBrowser.exe` lives in `ZenlessZoneZero_Data\Plugins\x86_64`), and Windows' app filter names programs by exact
path, never by folder (D16), so each must be listed. Programs that install, remove, redistribute or report crashes are
listed but left out by default, with the reason; the person's choice overrides either way. A rescan after a game
update keeps every choice for a program still there, takes the default for a new one, and drops one that is gone. A
program from elsewhere (a launcher) can be added. Measured on Zenless Zone Zero's folder: seven programs; the game, its
in-game browser and a launcher plug-in included; four crash reporters left out.

**Running** is a pair: one game profile and one VPN profile. The window offers both choices and a Run; the bridge is
given the game profile's included executables (`--app`, or `--folder` for the same scan) and the VPN profile.

**"Route all traffic of an attached process."** On Windows' VPN platform the selection is the program AND a
destination (D17, measured twice): the driverless backend can route every destination the game's programs have been
seen using — their learned destinations — but not one they reach for the first time, and Zenless Zone Zero's HTTPS
endpoints churn between sessions (session 2 shared three of about fifteen with session 1). All of a process's traffic
from its first packet needs the driver backend (§5): a WFP driver of our own, or an existing signed packet driver —
Windows Packet Filter (`ndisrd.sys`, free for personal use, a paid licence to redistribute commercially; its user-mode
library is MIT; ProxiFyre routes per process on it). The owner chooses which; until then a game profile runs on the
driverless backend and says it routes learned destinations only.

