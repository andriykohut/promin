# Promin

Promin shows one PC's screen on every other PC in the room, over the local network. It runs on Windows 11 and needs no server and no internet connection.

The name is Ukrainian: промінь, a beam of light.

## Status

Early. Broadcasting works and has been run between two PCs. It has not yet been tried in a full room of ten or more.

Planned next, none of it built yet: live previews of the other screens, remote control, file transfer and locking screens.

## How it works

There are two apps:

- **The console** (`Promin.Master`) runs on the PC whose screen is shown. It lists the PCs in the room and starts and stops the broadcast.
- **The agent** (`Promin.Agent`) runs on every other PC. It has no window of its own; when a broadcast starts, it covers the display with the picture.

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
3. Start `Promin.Agent.exe`. It shows no window. Allow it through Windows Firewall on both private and public networks.
4. On the console, open **Set up devices**, enter the same code and choose **Pair**.

If the firewall prompt was dismissed, add the rules by hand in an administrator PowerShell:

```
# on the PCs running the agent
New-NetFirewallRule -DisplayName "Promin agent" -Direction Inbound -Protocol UDP -LocalPort 17843 -Action Allow
# on the PC running the console
New-NetFirewallRule -DisplayName "Promin console" -Direction Inbound -Protocol UDP -LocalPort 17844 -Action Allow
```

Other agent commands:

| Command | What it does |
|---|---|
| `Promin.Agent.exe --windowed` | Shows the broadcast in an ordinary window instead of covering the display |
| `Promin.Agent.exe --reset-pairing` | Forgets the paired console, so the PC can be paired again |

Nothing starts the agent with Windows or restarts it if it exits; for now that is up to you.

## Security notes

- Nobody on the network can watch or alter the broadcast, pair without the room code, or recover the code from the traffic.
- The room code is stored in a file that people using that PC can read. Pair each PC straight after setting its code. Until it is paired, anyone who knows the code can pair it to a different console.
- On **Set up devices**, pair only the PCs you can account for. A device that knows the code can announce itself under any name.
- Any device on the same network can disrupt Promin (for example, keep PCs from connecting), though it cannot watch.

## Where things are kept

| What | Where |
|---|---|
| Console data and log | `%LocalAppData%\Promin` |
| Agent data and log | `%LocalAppData%\Promin\Agent` |
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
