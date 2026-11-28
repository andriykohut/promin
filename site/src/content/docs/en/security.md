---
title: Security
order: 4
---

## On the network

- Nobody on the network can watch or alter the broadcast or the screen pictures, pair without the room code, or recover the code from the traffic.
- The room code itself never crosses the network. After pairing, each side accepts only the other's certificate.
- Any device on the same network can disrupt Promin, for example by keeping PCs from connecting, though it cannot watch.

## The room code

- The room code is stored in a file that people using that PC can read. Pair each PC straight after setting its code. Until it is paired, anyone who knows the code can pair it to a different console.
- On **Set up devices**, pair only the PCs you can account for. A device that knows the code can announce itself under any name.

## What only the paired console can do

- Only the paired console can unlock a PC.
- Only the paired console can shut down or restart a PC, and never the console's own PC. Unsaved work on it is lost.
- Only the paired console can view a PC's screen, and never the console's own PC. Nothing shows on the PC while it is viewed; the agent's log on it records each viewing.
- Only the paired console can update a PC, and only to a newer Promin release signed by the project. The agent checks the signature itself before it installs anything.
- Only the paired console can take control of a PC, and never the console's own PC. The PC shows a tab for as long as it is controlled. Keys pressed and clipboard text are never written to any log.

## The clipboard while you control a PC

While the control picture has focus, text copied on the console PC goes to the controlled PC, and so does the text already on the console PC's clipboard when you click into the picture. Copy something harmless first if it holds a password.

## Files

A program sent to a PC is saved but never run, even when **Open them on each PC when they arrive** is ticked.

## If the console is lost or a PC is stuck

Only the paired console can unlock a PC. If the console is lost or a PC is stuck, an administrator starts that PC in Safe Mode, opens a terminal with **Run as administrator**, goes to the agent's folder (`C:\Program Files\Promin\Agent`) and runs:

```
.\Promin.Agent.exe --reset-pairing | Out-Host
```

This also removes the lock. Restart the PC normally, then pair it again.
