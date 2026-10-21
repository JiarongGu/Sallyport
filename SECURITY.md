# Security

Sallyport carries people's VPN traffic, keeps their VPN profiles, and runs a service as LocalSystem, so a security
problem in it matters. Thank you for reporting one.

## Reporting a vulnerability

Please do not open a public issue. Report it privately through GitHub: the repository's **Security** tab, then
**Report a vulnerability**. Say which version (Settings → About), what you did and what happened, and how bad you think
it is.

**Never attach your `.ovpn` profile, its keys, or a password** — not even to a private report. A description of the
profile (which options it uses, with every key block removed) is enough, and a capture can be made against a test server.

## What is in scope

- The protocol library (`src/Sallyport`): anything that lets a server, or something on the path, read or change what it
  should not, or that a profile's keys could leak through.
- The router service and the packet driver's use (`apps/Sallyport.Service`, `src/Sallyport.Windows`): anything that lets
  an unprivileged user or program use the service, change what it runs, or reach the network around the routing.
- The client and setup: how profiles, passwords and exports are kept, and the install folder's rights; the window's
  page (anything that lets another page or program drive it).
- Updates: anything that lets a release other than the one its manifest lists be run, or `data\update.json` (where
  updates come from, and so what is run as administrator) be changed by someone who is not an administrator.
- The loopback proxy each connected profile offers: it takes no credentials by design (any program on the computer
  may use it); a way to reach it from another computer is in scope.
- The release itself: a setup, zip or manifest that does not match what the release publishes.

The packet driver is NT Kernel's Windows Packet Filter; a problem in the driver itself belongs to NT Kernel.

## Supported versions

The latest release. A fix ships as a new release.

## How Sallyport keeps your data

See [Privacy and security](README.md#privacy-and-security) in the README.
