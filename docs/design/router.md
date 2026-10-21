# The router — design (2026-10-20)

> **Status (2026-10-21, the code review):** the record of the design as adopted; the router now is `architecture.md`'s
> and the code's. Since the review a TCP connection whose owner was not in the tables at its first packet is looked at
> again, and one later found a selected program's counts as failed coverage (change 2); one never found is not yet
> counted for the window (TASKS §2). A run selects only its client's session's processes.

**Why it exists.** Stage 1 of D18: every packet of a game profile's programs into the tunnel from the first one, and
routing started or stopped in the middle of a game — what Windows' VPN platform cannot do (D17, measured twice). The
owner: *"so if the processer is attached we need to route all trafic"*, *"we should be setup vpn in mid of the game
too"*, *"you can co work with codex with the design"*.

**How it was made.** As the earlier designs were (`client-ui.md`, `game-routing.md`): Codex CLI 0.162.0-alpha.17.2,
`codex exec -s read-only`, on the brief `local/codex/router-brief.md`, with NT Kernel's MIT headers (`Common.h`,
`ndisapi.h`) beside it. A static read: Codex installed, launched and changed nothing. Its answer is below the line.

**Checked by Claude against the code and the driver the same night.** These hold:
- D11 sealed profiles under the *service account's* DPAPI, not machine scope; `game-routing.md` §1 forbids plaintext
  profiles on client IPC and "an elevated tunnel runtime"; §5 names `IGameRoutingController`.
- `TunnelSession` builds a `TunnelNetwork` on every tunnel it brings up, and that network reads `Tunnel.Packets`: a
  router needs the session to build a different consumer per generation, as the design says.
- Nothing reads an MTU from the server: the client announces `tun-mtu 1500` (`Tunnel.cs`), and `PushedConfig` holds no
  MTU. `Ipv4` drops every fragment.
- **The headers in the brief were 3.6.1's** (`NDISRD_VERSION 0x06013000`), as Codex noticed against the driver's
  measured `0x06023000`. The v3.6.2 tag's `Common.h` and `ndisapi.h`, fetched and compared, differ from them only in
  that constant: the interface Codex read is the pinned driver's.
- The interface as the header gives it: every control code `METHOD_BUFFERED` (index 0x830 + n, device 0x8300), yet
  `ETH_M_REQUEST` carries *pointers* to `INTERMEDIATE_BUFFER`s — so packet buffers live in native memory that cannot
  move, never in managed arrays. Under `pack(1)` on x64 an `INTERMEDIATE_BUFFER` is a 16-byte union (handle / list
  links), then `m_dwDeviceFlags` @16, `m_Length` @20, `m_Flags` @24, `m_8021q` @28, `m_FilterID` @32,
  `m_Reserved[4]` @36, the frame @52. The frame's capacity is the driver's build choice (1514, or 9014 with jumbo
  frames) and the header cannot say which: each buffer is allocated at 9014 so either fits, and nothing that strides
  over an array of them (Fast I/O) is used until the capacity is measured.
- What a static filter table does with a packet no rule matches is not in the header. Unmeasured; nothing relies on it
  before it is measured.

**Adopted with three changes, and two from measurement.**

1. **The tunnel stays in the client; only packets cross the pipe.** Codex puts the tunnel in the SYSTEM service and
   hands it the profile's plaintext for one session, amending `game-routing.md` §1. Instead: the client keeps its
   `TunnelSession` (D14 unchanged, OpenVPN and TLS never run as SYSTEM, reconnects and notices as they are), and for a
   game session builds a consumer that pumps IP packets between the tunnel and the service's pipe. The service takes,
   attributes, NATs and injects; it learns the tunnel's address per generation and holds no secret. It injects a
   packet from the client only if it matches one of its own mappings, so whatever speaks on the pipe can reach the
   game's mapped flows and nothing else. Cost: one pipe hop each way per packet — measured in step 2. The tunnel lives
   only as long as the client, which is already the policy (client exit stops routing). The tunnel's outer socket is
   the client's, a program never selected, so attribution passes it direct; a `FILTER_PACKET_PASS` on its exact tuple,
   reported by the client per generation, keeps it out of the capture as well — never a whole server address.
2. **An unattributed packet goes direct and is counted; routing is not aborted.** Codex aborts routing (fail open, all
   of it) when a packet's owner cannot be found in time. That hands every short-lived socket of every program on the
   machine the power to end the game's routing — and the abort sends *all* the game's traffic direct, a larger leak
   than the one it reacts to. Kept from the design: a new tuple is held for a coalesced table refresh (bounded; 50 ms
   proposed, measured); owner selected → routed; owner another program → direct, cached until its row leaves the
   table; no owner found → direct, counted by tuple. Coverage is checked in three outcomes (rule 6): **holds** when no
   such tuple is ever owned by a selected process; **failed**, naming the count, when one later is; **could not be
   checked** while unowned tuples remain unowned. The person sees the last two.
3. **Recovery: one service, a supervisor and a worker, shaped by the first measurement.** The design's independent
   recovery is required, in its cheapest sufficient form: the service runs a supervisor process and a worker that holds
   the driver handle. Step 1 measures whether the driver returns an adapter to pass-through when the handle closes
   (worker killed). If it does, the supervisor's remedy for a worker that stops draining is to kill it. If it does not,
   the supervisor resets the adapters' modes through its own handle — whether a second handle may do that is measured
   in the same step. Neither measured yet; until both are, nothing intercepts without a guard that can undo it.

   **Measured the same night (TASKS item 1, gate 1): both hold.** An interceptor killed mid-stream without a word to
   the driver left its adapter in mode `None` with an empty queue, and a guard probing twice a second saw no failure;
   a second handle read the first's tunnel mode and set it to `None`, and the first's packets stopped at once. So the
   supervisor's remedy for a worker that stops draining is to kill it, and its own handle is the second remedy.

4. **Only what Windows sends is taken (measured, gate 2).** Codex puts the adapter in tunnel mode both ways. A reply
   handed up with `SEND_PACKETS_TO_MSTCP` reached its UDP socket with the receive path not taken, 3 of 3, and taking
   the send path alone cost 844 ms of CPU against 2,984 ms over the same 16 s of a download's load. The router's
   replies come out of the tunnel, so the adapter is put in `SentTunnel` only. What that gives up: the design's
   discarding of late direct packets for a moved flow (§3). They reach Windows unseen — for UDP, the game hears the
   server on both paths for as long as the server keeps sending to the old address, which mid-game attach measures
   (gate 6) rather than hides. The receive path comes back only if a measurement shows it is needed.

5. **Pieces are translated one by one, and a packet too big for the tunnel is sent whole (measured, gate 5).** Codex
   reassembles a selected datagram before NAT and fragments a packet too big for the tunnel that may be fragmented.
   Instead each piece is translated as it comes: the first carries the ports, and its checksum is carried over the
   change (RFC 1624) rather than computed over a datagram not all here; the later ones follow it; nothing waits for a
   whole datagram, so nothing is held but a piece that comes before its first. And a packet too big for the tunnel
   (1448 on a 1500-byte path) without don't-fragment goes into it whole: Windows splits the outer datagram and the
   server joins it, so the far end gets the datagram as the program sent it. Split inside the tunnel, Google's STUN
   server — which answers a whole 1500-byte request — answered 0 of 2; whole, 3 of 3. With don't-fragment,
   "fragmentation needed" as designed; it sets Windows' path MTU for that destination for every program on the
   machine, for about 10 minutes.

Everything else is adopted as written, including LocalSystem, the pipe's ACL and peer checks, tunnel mode on send and
receive, endpoint-independent UDP mappings preserving ports, MSS clamping and ICMP for DF, selected IPv6 blocked, game
traffic dropped (not sent direct) while the tunnel is down, fail open when the router fails, and the build gates of §9.

---

**Choose a LocalSystem router service, installed with the driver under one elevation prompt, with an independent recovery process. Keep D14’s user store and transfer a profile in memory for one routing session.**

There is one blocking qualification: **NDIS packets plus owner-table snapshots cannot guarantee exact process attribution for every possible socket.** They support a useful Stage 1 implementation, but short-lived sockets, UDP port reuse and shared sockets must be explicit acceptance gates. If those gates fail, Stage 1 must report its limitation; Stage 2 supplies ownership at the interception point.

This is a proposed design, not a measurement. I read the repository and interface headers; changed, installed and executed no project code.

**1. Service, lifetime and IPC**

Put native interception, attribution and frame injection in `Sallyport.Windows`; run them from `apps/Sallyport.Service`. Use LocalSystem initially because administrator access to `\\.\NDISRD` is measured; a restricted service account’s access is unmeasured. Use BCL plus native Windows calls, including SCM hosting.

Install a small, separate LocalSystem recovery process with it. Its only responsibilities are liveness monitoring, direct-path probes and disabling Sallyport interception. It receives no profile.

Services start with Windows but leave adapter modes unchanged until requested. Initially permit one game-routing session across the machine. Bind it to one authenticated user/logon session and client-host connection. Closing the window into the tray preserves it; exiting the host, logging off or losing its lease stops routing and restores direct traffic. Never silently resume after reboot.

The named pipe has:

- An explicit DACL allowing SYSTEM and the intended logon SID; no Everyone access. Grant individual read/write rights without `FILE_CREATE_PIPE_INSTANCE`.
- `PIPE_REJECT_REMOTE_CLIENTS` and first-instance protection.
- Mutual peer validation: pipe-reported PID, retained process handle, creation time, token SID/session, and installed executable identity. The client verifies the server against the SCM service PID before sending secrets.
- A four-byte little-endian length followed by a versioned message. Bound control messages to 64 KiB and profile transfer to 1 MiB; reject oversize, unknown versions, malformed messages and timed-out partial frames.
- Request IDs, policy revisions and session generations; acknowledgements and ordered state events. Old generations cannot change current state.

These ACL details matter because generic pipe write permission can also permit creating pipe instances. [Microsoft’s pipe security reference](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

The service accepts routing commands, never arbitrary executable launches, filenames, DLLs, scripts or IOCTLs. It independently validates that admitted processes belong to the requesting user. Same-user malware remains within D14’s existing trust boundary.

**2. Profile ownership**

Choose **client decryption followed by one-session transfer over the authenticated pipe**.

The native client host opens D14’s sealed copy; WebView never receives it. Transfer UTF-8 bytes separately from JSON diagnostics. The service parses only Sallyport-supported profile content, keeps it for reconnects during that session, and discards it on stop. No command line, environment variable, plaintext file, request tracing or secret-bearing exception.

Clear temporary byte buffers. Existing parsed strings and certificate objects mean complete memory erasure cannot be promised; process dumps can contain secrets.

The alternatives cost more:

| Choice | Cost |
|---|---|
| D11 service-owned store | Migration, per-user authorization, deletion/backup semantics and coordination with existing proxy mode. D11 used **service-account DPAPI**, not machine-scope DPAPI. |
| Elevated user helper | Preserves user DPAPI, but ordinarily requires elevation each session. Alternate administrator credentials can select the wrong user/store. |
| Session transfer | Smallest storage change; exposes plaintext to a privileged process and requires carefully secured IPC. |

This explicitly amends the earlier game-routing prohibition on plaintext profile IPC. Proxy mode retains D14 unchanged.

**3. Driver setup and packet movement**

Use native x64 initially: `CreateFileW` and `DeviceIoControl`, never `ndisapi.dll`.

The supplied header uses packing 1, pointer-bearing requests and a build-dependent frame capacity. Its version constant differs from the installed version measurement. **Verify ABI compatibility before interception**, including frame capacity, pointer offsets and returned counts; reject unsupported versions.

Use:

| Operation | IOCTL and structure |
|---|---|
| Enumerate adapters | `IOCTL_NDISRD_GET_TCPIP_INTERFACES`, `TCP_AdapterList` |
| Configure interception | `IOCTL_NDISRD_SET_ADAPTER_MODE`, `ADAPTER_MODE` |
| Packet notification | `IOCTL_NDISRD_SET_EVENT`, intended `ADAPTER_EVENT` layout |
| Read batches | `IOCTL_NDISRD_READ_PACKETS`, `ETH_M_REQUEST` pointing to `INTERMEDIATE_BUFFER`s |
| Forward outbound unchanged | `IOCTL_NDISRD_SEND_PACKETS_TO_ADAPTER` |
| Deliver inbound/reconstructed replies | `IOCTL_NDISRD_SEND_PACKETS_TO_MSTCP` |
| Inspect/clear queues | `IOCTL_NDISRD_ADAPTER_QUEUE_SIZE`, `IOCTL_NDISRD_FLUSH_ADAPTER_QUEUE` |

The event IOCTL’s exact buffer contract needs confirmation: published documentation and the supplied structure are insufficiently consistent to assume it.

Cover every supported TCP/IP-bound egress adapter, including Ethernet, Wi-Fi and relevant virtual adapters. Correlate driver handles with Windows interface identities. Unsupported active paths, adapter-list overflow and new-adapter coverage gaps invalidate readiness.

Initially use `MSTCP_FLAG_SENT_TUNNEL | MSTCP_FLAG_RECV_TUNNEL`, without listen, promiscuous or loopback flags. Tunnel mode withholds originals; listen mode cannot enforce routing. [Driver mode documentation](https://www.ntkernel.com/docs/windows-packet-filter-documentation/c-api/setadaptermode/).

Register events before enabling interception. Start with batches of 64; wake, drain immediately, reset/recheck without losing notifications. Preallocate unmanaged buffers and inspect `dwPacketsSuccess`, including partial writes. Never wait to fill a batch.

Unselected outbound frames go downward unchanged; unselected inbound frames go upward unchanged. Selected outbound originals never reach the physical adapter. While routed, discard physical-path packets belonging to the selected flows, preventing late direct replies from mixing identities.

**4. Attribution and the first packet**

Arm interception before launch. Matching uses normalized executable identity and `(PID, creation time)`, checked by the service; descendants require explicit executable admission.

For TCP, query `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL)` and match family, local address/port and remote address/port. For UDP, query `GetExtendedUdpTable(UDP_TABLE_OWNER_PID)` and match local address/port, considering wildcard binds and every candidate row.

UDP’s PID identifies the process that bound the endpoint; it supplies neither remote endpoints nor a unique socket lifetime identity. [Microsoft’s UDP row contract](https://learn.microsoft.com/en-us/windows/win32/api/udpmib/ns-udpmib-mib_udprow_owner_pid).

On a new tuple, hold its first packet and coalesce an immediate table refresh. Proposed bounds: 50 ms, eight packets per tuple and 4 MiB globally, measured before adoption. Do not block the adapter reader while waiting.

Cache validated established TCP associations. UDP cache entries are advisory: refresh on new peers and bursts, age aggressively, and invalidate on endpoint changes, process exit, identity failure, policy change and adapter/tunnel generation changes. Never cache “unknown means direct.”

PID creation times prevent process reuse mistakes; **they do not prevent socket reuse between snapshots**. A socket can disappear before lookup, a reused UDP tuple can resemble an old binding, and inherited/shared sockets can make the binder differ from the sender. Polling faster or adding ETW does not establish atomic ownership.

Unknown or ambiguous packets remain held briefly. At expiry, declare attribution unavailable and abort routing through controlled fail-open teardown; do not indefinitely drop unidentified machine traffic. This may make Stage 1 impractical under socket churn—that is a measured feasibility question.

**5. NAT and return delivery**

For selected IPv4 TCP/UDP, validate Ethernet/VLAN framing, lengths and IP options, remove the link header, and translate source address to `Tunnel.Config.Address`.

Maintain:

- TCP mappings keyed by original five-tuple plus process and routing generation.
- UDP mappings keyed by process instance and local endpoint, independent of remote destination, preserving one translated port across peers.
- Reverse mappings containing original endpoint, adapter/link metadata and authorized remote peers.

Allocate unique tunnel ports per protocol, preserving original ports where available. Retain UDP mappings while their endpoint remains observable; use a proposed 120-second idle grace after disappearance. Retain TCP mappings through closure, with a proposed 120-second reuse quarantine. Expire on process exit or tunnel-generation retirement; refuse capacity exhaustion rather than evict active mappings.

Recompute IPv4 and transport checksums, including pseudoheaders and UDP zero-checksum semantics. Preserve TCP sequence numbers and options; Windows remains the TCP stack. Call `Tunnel.SendAsync`.

Read replies exclusively from `Tunnel.Packets`, validate mapping and peer, reverse destination translation, rebuild Ethernet using the original local MAC and appropriate recorded gateway/peer MAC, preserve required VLAN metadata, then inject upward through `IOCTL_NDISRD_SEND_PACKETS_TO_MSTCP`.

Measure injection loop avoidance, firewall behavior and socket delivery. Never infer a recipient from an ambiguous port. Unsolicited inbound is disabled initially; hosting requires explicit listener authorization and separately verified server forwarding.

A necessary source change: `TunnelSession` currently always constructs `TunnelNetwork`, which consumes the reader. Introduce a per-generation packet-consumer factory so a game session constructs the router instead. Do not run both readers or share their port spaces.

**6. Packet boundaries**

Implement bounded fragment handling in both directions: key by adapter/generation, source, destination, protocol and IP ID; hold noninitial fragments until classification; reject overlaps, malformed sets and expired/incomplete sets. Reassemble selected datagrams before NAT; release unselected frames unchanged. Existing `Ipv4.TryRead` rejects fragments and needs a separate router parser.

Define an effective inner MTU from outer-path MTU, transport overhead and measured server limits. `PushedConfig` currently exposes no MTU. Clamp TCP MSS in both SYN directions. For oversized DF packets, inject ICMP fragmentation-needed toward the originating socket; fragment DF-clear packets correctly. Mid-game TCP cannot benefit from rewriting an earlier SYN.

Translate ICMP errors using their quoted NAT tuple, including quoted checksums and advertised MTU. Bound and validate quotes. Independent ICMP/raw-protocol attribution is unsupported by these owner tables; do not claim complete protocol coverage.

Loopback remains local. Selected broadcast, multicast and link-local application traffic is blocked initially; OS ARP/DHCP/NDP remains operational. Direct LAN exceptions require explicit policy and coverage disclosure.

**Block selected external IPv6**, including supported extension-header parsing; do not disable IPv6 machine-wide. Unattributable IPv6 invokes the same unavailable/abort policy. System resolver traffic remains direct and outside process coverage.

Exclude the service’s actual outer transport tuple before its first send, reserving that socket while the exclusion exists. Revalidate before reconnect/failover; do not broadly exclude every packet to a VPN-server address.

Offloads, segmentation, jumbo frames and VLAN representation are acceptance gates; never fix them by silently disabling machine-wide features.

**7. Attach, detach and failure**

Attach prepares the tunnel first, then commits a capture-policy generation. Packets already past interception cannot be recalled. From the barrier onward, existing selected flows move immediately.

Their external source tuple changes. Existing TCP normally resets or times out; UDP may continue, reconnect or lose matchmaking/NAT traversal state. Detach changes it again. Show:

> Routing changes now. Your game may disconnect or need to rejoin.

Do not preserve old direct flows behind an “all routed” label. Retire mappings and reject late replies by generation.

| Event | Policy |
|---|---|
| Tunnel loss | Drop attributable selected traffic; pass unrelated traffic. No gameplay backlog. |
| Reconnect | New generation; retire mappings and revalidate transport exclusions. Continuity is unpromised. |
| Service crash/hang | Recovery process restores direct operation: fail open. |
| Driver removal | Routing unavailable; direct path resumes after adapter recovery. |
| Sleep/network change | Quiesce when notified; resume through reconciliation and fresh preparation. Any enforcement gap is disclosed. |
| Client exit/lease loss | Stop routing and restore direct operation. |

D13’s guard runs outside the router, with its existing probe thresholds plus queue-age and packet-processing-progress checks. On failure it stops interception, releases/discards queues according to measured semantics, clears owned filters and verifies direct recovery.

Measure handle-close cleanup and whether another process can reset the owning instance. **Do not ship until crash/hang recovery works.** Neither driver cleanup nor SCM restart alone is an established failsafe.

**8. Cost and replaceable seam**

Static filters use `STATIC_FILTER_TABLE`, `STATIC_FILTER`, `ETH_802_3_FILTER`, `IP_V4_FILTER`/`IP_V6_FILTER` and `TCPUDP_FILTER`, installed with `IOCTL_NDISRD_SET_PACKET_FILTERS`.

Use `FILTER_PACKET_PASS` for invariant infrastructure/transport exclusions and `FILTER_PACKET_REDIRECT` for candidates. These filters contain no PID. Passing only already-known selected ports would leak new sockets; cached direct-port rules can misclassify reused ports. Initially retain broad candidate interception. Driver rule ordering and cache updates are documented, but must be verified against the pinned version. [Static-filter behavior](https://www.ntkernel.com/docs/windows-packet-filter-documentation/managing-individual-static-filters/).

Measure direct baseline, unchanged reinjection and routed traffic: packet rate, throughput, CPU, allocations/GC, IOCTLs, queue peaks, drops and p50/p95/p99 added latency under idle and saturation. Optimize inbound interception and shared-memory Fast I/O only after evidence justifies them.

Keep `IGameRoutingController`: prepare, start/attach, apply policy, stop/detach, recover and observe. Inputs remain game selection plus profile ID. Internal IPC carries revisions and ephemeral secret transfer.

Backend capabilities separately report selection scope, first-packet evidence, families, live attachment, return delivery and inbound reachability. Every check is `holds`, `failed` or `unavailable`. D15’s host authors all display state. Replacing `PacketFilterBackend` with `WfpProcessBackend` changes nothing above this boundary.

**9. Build gates and refusals**

Each record names Windows/driver/game builds, revision, sent/returned metadata, counters and cleanup.

| Step | Send/exercise → required return |
|---|---|
| ABI and recovery | Version/enumeration/events; kill and hang a bounded interceptor → correct structures, independent direct recovery. |
| Unchanged forwarding | TCP/UDP, both families, bursts, adapters → unchanged successful exchanges; measured overhead and loss. |
| Attribution | Bind/send/close immediately, reused/shared ports, children and PID reuse → correct attribution or explicit unavailable; no guessed ownership. |
| One selected program | First TCP SYN and UDP datagram to unpredictable controlled endpoints through the real VPN → VPN source and replies delivered; concurrent unselected program stays direct. |
| Packet boundaries | Large DF/non-DF datagrams, fragments, ICMP errors and offloads → valid delivery or specified errors; selected IPv6 blocked. |
| Zenless | Login, HTTPS bursts, gameplay and relaunch → actual game/server path evidence and replies, with direct control traffic. |
| Attach/failure | Attach/detach during TCP/UDP; interrupt tunnel, remove driver, sleep → observed source changes, session effects and recovery matching policy. |
| Peer harness | Home-NAT peer and VPN peer exchange first packets, rendezvous, migrate peers and idle → mappings and replies verified; unsolicited hosting tested separately. |

I would refuse an unconditional “all traffic from the first packet” claim based solely on owner-table snapshots; silent direct fallback; seamless migration claims; ambiguous inbound delivery; broad destination exemptions; machine-wide DNS/MTU changes; anti-cheat bypasses; or commercial driver distribution beyond D18.

The principal feasibility risks are attribution ambiguity, reinjection/firewall compatibility, offload handling, server NAT/filtering and reliable independent recovery. A failed gate remains failed; missing remote visibility remains unavailable.