# Sallyport.Vpn — per-app routing on Windows' own VPN platform (D12, D13)

For programs that ignore proxy settings — games first. Windows creates the VPN interface and lets only the listed
programs' packets into it, for the listed destinations; everything else on the machine stays direct.

- `Plugin/` — the VPN plug-in, a C#/WinRT component the platform activates as a `vpnClient` task. A relay, not a
  tunnel: each IP packet goes as one datagram to the bridge on loopback, and back. It holds no key and no profile; the
  platform suspends it between events, so it could not keep a tunnel alive anyway (D12).
- `PluginHost/` — the task's custom host: a .NET executable running `CoreApplication`.
- `Bridge/` — a full-trust console in the same package, run through its execution alias `sallyport-vpn`: it connects
  the OpenVPN tunnel (the `Sallyport` library), relays packets between it and the plug-in, adds and connects the VPN
  profile, and guards the direct path.
- `build.ps1` — publishes all three, packs with `makeappx` and signs with a test certificate. The tools
  (Microsoft.Windows.SDK.BuildTools) and the certificate live outside the repository.

```
sallyport-vpn route <profile.ovpn> --app <program.exe>... --prefix <a.b.c.d/n>... --for <1..600 s> [--probe <ip:port>]
                    [--policy split|force] [--default-route allow]   # the last only to re-measure D17, under the guard
sallyport-vpn list | log [clear] | panic
```

**Safety, not optional (D13).** A VPN that takes traffic and drops it takes the machine's network with it. So: a
default route is refused (by the bridge and by the plug-in); a prefix that covers the VPN server or the guard's probe
is refused; and while connected the bridge probes the direct path every 250 ms from outside the VPN and tears
everything down on failure. If anything looks wrong: `sallyport-vpn panic`.

Under the app filter, an unlisted program's traffic to a routed prefix is refused, not sent direct (measured, D12):
while a game's prefixes are routed, only the game reaches them.

## Setting up, and taking it down

The plug-in reaches the bridge over loopback, which Windows refuses an AppContainer unless the package is exempted
(measured, D16). Once, as administrator, after installing:

```
CheckNetIsolation LoopbackExempt -a -n=<package family name>     # Get-AppxPackage Sallyport.Vpn | % PackageFamilyName
```

For development the package is signed with a test certificate trusted in `LocalMachine\TrustedPeople`. To take all of
it off a machine: `sallyport-vpn panic`; `Remove-AppxPackage` the package; `CheckNetIsolation LoopbackExempt -d -n=<family>`
(administrator); remove the certificate from `LocalMachine\TrustedPeople` and `CurrentUser\My` (administrator for the
first).

