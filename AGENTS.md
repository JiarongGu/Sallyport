# Sallyport — the brief every agent works from (Claude Code imports it through CLAUDE.md; Codex reads it as is)

An app-scoped, OpenVPN-compatible client in pure .NET (README for what it does, CONTRIBUTING for how it is built). These
rules are binding.

1. **Clean-room. Never read, port or translate OpenVPN's source** (OpenVPN 2 is GPLv2, OpenVPN 3 is AGPLv3; this is MIT).
   Work from the protocol's public description, the server's own behaviour, and packet captures. If a detail is only
   learnable from their code, it is measured against a real server instead — and the measurement is written down.
2. **No secret ever enters the repository.** A profile (`.ovpn`) holds a private key and a static key: it is read from
   where its owner keeps it, never copied here, never logged, never printed — not even in a test failure. Tests use keys
   they generate. A public address of the owner's (home, VPN server) is written masked, `A.B.C.x`.
3. **Every phase is measured against a real server before it is called done**, and the measurement goes in TASKS/the
   commit: what was sent, what came back. A unit test proves the code agrees with itself; the server proves it agrees
   with OpenVPN.
4. **No runtime dependencies beyond the .NET base library.** Cryptography and TLS are the BCL's (`AesGcm`, `Aes`,
   `HMACSHA256`, `SslStream`). Adding a package is a decision in `docs/DECISIONS.md`.
5. **Comments say why, never what.** Where the wire format is the reason, name the field and its size.
6. **Three outcomes where something is checked**: it holds, it failed, or it could not be checked — never collapsed.

Layout: `src/Sallyport` (the protocol library) and `src/Sallyport.Windows` (the driver, the router, the service's
protocol, the install layout); `apps/` the applications — `Sallyport.Client` (Sallyport's one program, D30: the window
and tray, its page in `web/`, hosting the next two), `Sallyport.Service` (the router, LocalSystem; `--service`),
`Sallyport.Setup` (install and removal; the release's name or `/uninstall`; `package.ps1`, `version.ps1`),
`Sallyport.Uninstall` (the small uninstaller setup writes beside the program, setup's removal compiled ahead of time, D32),
`Sallyport.Host` (one profile's tunnel and proxy, headless, for another app); `Sallyport.Observer` and `Sallyport.Vpn`
are the driverless route, kept but not in the product; `tools/Sallyport.Probe` and `tools/Sallyport.Driver` drive a phase
against a real server; `tests/` one project per area. How the parts fit now: `docs/design/architecture.md`.
Decisions: `docs/DECISIONS.md`; designs, each the record of how it was made: `docs/design/`. Work
left: `TASKS.md` (open items only), and what was finished, with how it was measured, `docs/TASKS-DONE.md`; what each
version brings: `CHANGELOG.md`, under `## Unreleased` until it ships.

Build and check: `dotnet build Sallyport.slnx`, `dotnet test Sallyport.slnx` (all must pass; look for "Failed!"), and
`npm run build` in `apps/Sallyport.Client/web` after changing the page. Every word a person reads goes through `L.T` (C#)
or `t()` (the page), with its Simplified Chinese in `Chinese.cs` or `web/src/i18n.ts` (`LanguageTests` fails a missing
one); no letter-spacing on Chinese. A test listens on loopback only, and makes and removes its own temporary folder.

## Working together (Claude Code and Codex)

As in the owner's other repositories: Codex runs as `codex exec` (`%LOCALAPPDATA%\OpenAI\Codex\bin\…\codex.exe`, not on
PATH) on a written brief in `local/codex/` (gitignored), `-s read-only` for a design or an audit and
`-s workspace-write` for one implementation step. A brief names its files, its rules and how it is checked; a step
never commits. Whoever did not write a result checks it against the code before it is relied on, and the record says
how it was made (`docs/design/client-ui.md` is the first, `docs/design/profile-flow.md` the latest).

Rule 2 binds every agent the same way: never open a `.ovpn`, `.key`, `.pem`, `.p12` or `.pfx`, nor anything under the
owner's profile folders, `%LOCALAPPDATA%\Sallyport\` or an installed copy's `data` folder — the client does that, not an
agent. Names, existence and times may be looked at; contents never. Nothing that can cut the machine off its network
runs without `Sallyport.Driver guard` outside it (D13).
