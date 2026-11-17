---
title: Upgrading and removing
order: 3
---

## Upgrading

Download the newer setups from the [latest release](https://github.com/andriykohut/promin/releases/latest) and run them over the installed version. An upgrade keeps the room code and the pairing, and the agent setup does not open the agent window.

Upgrade the console and every agent to the same version.

If an agent PC was set up by hand (a copied folder and `--install`, see [Advanced](../advanced/)), running `Promin-Agent-Setup.exe` takes over that service. You can delete the old folder afterwards.

## Removing Promin

Uninstall Promin from **Settings**, **Apps**, **Installed apps**.

Uninstalling leaves the data behind:

- The agent keeps its room code, identity, pairing and any lock in `C:\ProgramData\Promin`.
- The console keeps its data in `%LocalAppData%\Promin`.

So a reinstall needs no new pairing, and a reinstall on a locked PC locks it again. To pair a PC with a different console, reset it as described in [Advanced](../advanced/).
