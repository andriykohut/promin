---
title: Advanced
order: 5
---

## Installing the agent without the setup

Publish a self-contained copy of the agent from the [source code](https://github.com/andriykohut/promin). That PC then needs no .NET install:

```
dotnet publish src/Promin.Agent -c Release -r win-x64 --self-contained true -o src/Promin.Agent/bin/publish/win-x64
```

Copy the folder to the PC and set the room code (`| Out-Host` makes its one line of reply show up):

```
.\Promin.Agent.exe --set-room-code 482913 | Out-Host
```

Then run `.\Promin.Agent.exe --install` in an **administrator** terminal and pair the PC from the console as usual.

## Agent commands

Run `Promin.Agent.exe` in its folder with one of these:

| Switch | What it does |
|---|---|
| `--install` | Registers the agent as a service that starts with Windows, adds its firewall rule, and locks the data folder (administrator terminal) |
| `--uninstall` | Removes the service and its firewall rule, leaving the pairing, identity and lock files |
| `--windowed` | Shows the broadcast in an ordinary window instead of covering the display |
| `--reset-pairing` | Forgets the paired console and removes the lock, so the PC can be paired again |

## Network ports

Both ports are UDP.

| Port | Used for |
|---|---|
| 17843 | The agent accepts the console's connections |
| 17844 | Agents announce themselves, and the console listens for them |

## Where things are kept

| What | Where |
|---|---|
| Console data and log | `%LocalAppData%\Promin` |
| Console settings, including where the control window was last placed | `%LocalAppData%\Promin\settings.json` |
| Agent data and log | `%ProgramData%\Promin\Agent` |
| Lock on an agent PC | `%ProgramData%\Promin\Agent\lock.json` |
| Room code on an agent PC | `%ProgramData%\Promin\agent.json` |
| Files sent to an agent PC | `Promin` on the desktop of the person signed in there |
