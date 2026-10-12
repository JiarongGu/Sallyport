# TASKS.md — open work only

A finished item is deleted (git is the archive); a phase is done when it is measured against a real server.

## Phase 3 — what is left
- [ ] Unit tests for the TCP state machine against a scripted peer (handshake, refusal, SYN timeout, in-order and
      out-of-order data with SACK blocks, retransmission, fast retransmit, FIN both ways, reset, zero-window probe).
      The live runs exercise it against Linux and Cloudflare; nothing yet pins each path down.
- [ ] Many concurrent connections — a browser page opens dozens. Only one or two at a time have been measured.

## Then Kobo adopts it
Kobo hosts Sallyport in-process in place of the WSL `start`/`stop` commands; the route file's `proxy` and `hosts` stay.
Measured by a FANZA page in Kobo's browser with WSL's OpenVPN stopped.

## Phase 4 — the long run
Reconnect, fallback across a profile's remotes, renegotiation over hours (`ControlStream` refuses a soft reset today —
the server's default is hourly), status and byte counts as events for a host (Kobo's Network page), `auth-user-pass`,
`tls-auth`, `tls-crypt-v2` and `P_DATA_V1` profiles. Uploads at volume (the send side has no SACK of its own). Then release.

**Written but never run against a server:** the TCP transport (the test profile's second remote is `tcp/443`); the
keepalive over minutes; whether the server acts on the exit notices (`explicit-exit-notify`, `EXIT`).
