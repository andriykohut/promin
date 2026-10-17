<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/promin-lockup-dark.svg">
    <img src="assets/brand/promin-lockup-light.svg" alt="Promin" height="72">
  </picture>
</h1>

Promin shows one PC's screen on every other PC in the room, over the local network. It runs on Windows 11 and needs no server and no internet connection.

The name is Ukrainian: промінь, a beam of light.

## Status

Early. Broadcasting works and has been run between two PCs; the room wall's pictures of each PC's screen have been tried on one PC. Neither has yet been tried in a full room of ten or more.

Planned next, none of it built yet: live previews of the other screens, remote control, file transfer and locking screens.

## How it works

There are two apps:

- **The console** (`Promin.Master`) runs on the PC whose screen is shown. It lists the PCs in the room, starts and stops the broadcast, and shows each PC's screen in its tile, refreshed about once a second.
- **The agent** (`Promin.Agent`) runs on every other PC. It has no window of its own; when a broadcast starts, it covers the display with the picture. It also sends a small picture of its screen to the console about once a second.

Agents announce themselves on the network and the console finds them. A PC joins a room once, by pairing: both sides must know the same six-digit room code, and the code itself never crosses the network. After pairing, each side accepts only the other's certificate. The picture travels as H.264 video over QUIC, encoded and decoded on the graphics card where there is one.

## Requirements

- Windows 11 on every PC (Windows 10 has no QUIC support)
- All PCs on the same local network; wired is better than Wi-Fi
- The [.NET 10 SDK](https://dotnet.microsoft.com/download) to build

## Build and test

```
dotnet build Promin.slnx
dotnet test Promin.slnx
```

Tests that need a real screen or graphics card are skipped unless switched on in the test source.

## Setting up a room

There is no installer yet. The steps below are what was used for the two-PC run.

**On the PC whose screen will be shown:**

```
dotnet run --project src/Promin.Master
```

Windows Firewall asks once whether to allow the console. Allow it on both private and public networks.

**For every other PC**, build a self-contained copy of the agent (no .NET install needed there):

```
dotnet publish src/Promin.Agent -c Release -r win-x64 --self-contained true -o src/Promin.Agent/bin/publish/win-x64
```

Then, on that PC:

1. Copy the `win-x64` folder anywhere, for example `C:\Promin`.
2. In a terminal in that folder, set the room code: `.\Promin.Agent.exe --set-room-code 482913 | Out-Host` (pick your own six digits; `| Out-Host` makes its one line of reply show up).
3. In an **administrator** terminal in that folder, run `.\Promin.Agent.exe --install`. It registers the agent as a Windows service that starts with Windows, opens UDP 17843 through the firewall, and locks the data folder to SYSTEM and administrators. From then on the PC shows the broadcast by itself after each sign-in.
4. On the console, open **Set up devices**, enter the same code and choose **Pair**.

`--install` adds the agent's own firewall rule. The console is still started with `dotnet run`, so if its firewall prompt was dismissed, add its rule by hand in an administrator PowerShell:

```
# on the PC running the console
New-NetFirewallRule -DisplayName "Promin console" -Direction Inbound -Protocol UDP -LocalPort 17844 -Action Allow
```

Other agent commands:

| Command | What it does |
|---|---|
| `Promin.Agent.exe --install` | Registers the agent as a service that starts with Windows, adds its firewall rule, and locks the data folder (administrator terminal) |
| `Promin.Agent.exe --uninstall` | Removes the service and its firewall rule, leaving the pairing and identity files |
| `Promin.Agent.exe --windowed` | Shows the broadcast in an ordinary window instead of covering the display |
| `Promin.Agent.exe --reset-pairing` | Forgets the paired console, so the PC can be paired again |

The service runs the agent under the SYSTEM account, so every PC pairs once more the first time it is installed (the agent's identity moves to that account).

## Security notes

- Nobody on the network can watch or alter the broadcast or the screen pictures, pair without the room code, or recover the code from the traffic.
- The room code is stored in a file that people using that PC can read. Pair each PC straight after setting its code. Until it is paired, anyone who knows the code can pair it to a different console.
- On **Set up devices**, pair only the PCs you can account for. A device that knows the code can announce itself under any name.
- Any device on the same network can disrupt Promin (for example, keep PCs from connecting), though it cannot watch.

## Languages

The console is in English and Ukrainian. It starts in the Windows display language; the globe button in the title bar changes it, from the next start.

Every text the console shows is in `src/Promin.Master.Core/Strings.resx` (English, the source). A translation is a file beside it named for its language, such as `Strings.uk.resx`. Counted texts use the ICU plural format, so each language lists the forms it needs. Logs and the agent's terminal lines stay in English.

## Where things are kept

| What | Where |
|---|---|
| Console data and log | `%LocalAppData%\Promin` |
| Agent data and log | `%ProgramData%\Promin\Agent` |
| Room code on an agent PC | `%ProgramData%\Promin\agent.json` |

## Project layout

| Project | What it holds |
|---|---|
| `src/Promin.Master` | The console app (WinUI 3) |
| `src/Promin.Master.Core` | The console's logic, free of UI code |
| `src/Promin.Agent` | The agent app |
| `src/Promin.Protocol` | Discovery, pairing, transport and the broadcast session |
| `src/Promin.MediaPipeline` | Screen capture, H.264 encoding and decoding |
| `tests/` | One test project per project above, except the console app |

## Licence

[MIT](LICENSE)
