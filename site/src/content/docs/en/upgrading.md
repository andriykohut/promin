---
title: Upgrading and removing
order: 3
---

## Upgrading

When a newer Promin is out, the console says so in the bar above the PCs. Click **Update**: this PC updates first, after Windows asks for permission, then every connected agent PC. Each PC drops off for about a minute while it updates, and nothing appears on its screen. An upgrade keeps the room code and the pairing. **Update** is unavailable while you broadcast, send files, view or control a PC, or have a PC locked.

A PC that was off, or did not update, gets an orange arrow after its name, and the bar names it. Click **Update** again once it is on. The console keeps the setup it downloaded, so this works without internet.

Each time it opens, the console asks GitHub whether a newer release is out. Nothing about your room is sent.

### By hand

Download the newer setups from the [latest release](https://github.com/andriykohut/promin/releases/latest) and run them over the installed version. The agent setup does not open the agent window. Upgrade the console and every agent to the same version.

This is the way to upgrade agent PCs that run Promin 0.3 or earlier. It is also the way back for a PC whose update stopped part-way and left it without a running agent: restart it, and if it still does not appear in the console, run the agent setup on it.

If an agent PC was set up by hand (a copied folder and `--install`, see [Advanced](../advanced/)), running `Promin-Agent-Setup.exe` takes over that service. You can delete the old folder afterwards.

## Removing Promin

Uninstall Promin from **Settings**, **Apps**, **Installed apps**.

Uninstalling leaves the data behind:

- The agent keeps its room code, identity, pairing and any lock in `C:\ProgramData\Promin`.
- The console keeps its data in `%LocalAppData%\Promin`.

So a reinstall needs no new pairing, and a reinstall on a locked PC locks it again. To pair a PC with a different console, reset it as described in [Advanced](../advanced/).
