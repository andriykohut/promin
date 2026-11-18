---
title: Setting up a room
order: 1
---

Promin has two apps. The **console** runs on the PC whose screen you show. The **agent** runs on every other PC in the room. You install each one once, then add the PCs to the room with a six-digit room code.

## What you need

- Windows 11 on every PC. Windows 10 has no QUIC support, which Promin's connections need.
- All the PCs on the same local network. A cable is better than Wi-Fi. Guest Wi-Fi and separate VLANs do not work.
- Smart App Control turned off on every PC, and kept off. Promin's programs are not signed yet, so Windows blocks them while it is on or in evaluation. The setups check this and do not install until it is off. [How to turn it off](smart-app-control/)

Windows also shows "Windows protected your PC" for the setups, because they are not signed. Choose **More info**, then **Run anyway**. If an IT policy blocks unsigned apps, only the PC's administrator can allow Promin.

## Download the setups

Download both setups from the [latest release](https://github.com/andriykohut/promin/releases/latest) and copy them to a USB stick:

- `Promin-Console-Setup.exe` for the PC whose screen you show
- `Promin-Agent-Setup.exe` for every other PC

## Install the console

Run `Promin-Console-Setup.exe`, then open **Promin** from the Start menu.

The setup installs the console to `C:\Program Files\Promin\Console` and adds a Windows Firewall rule named `Promin console`, which lets the console hear the other PCs on UDP port 17844.

## Install the agent on every other PC

Run `Promin-Agent-Setup.exe`. It asks for nothing but one **Install** click.

The setup installs the agent to `C:\Program Files\Promin\Agent`, registers it as a Windows service that starts with Windows, opens the agent's port in Windows Firewall, then opens the **Promin agent** window.

## Add the PCs to the room

1. In the **Promin agent** window, type the room code and choose **Save**. Pick any six digits, and use the same code on every PC in the room.
2. On the console, open **Set up devices**, enter the same code and choose **Pair**.

You only do this once. From then on the PCs reconnect by themselves.

The agent window shows one of three statuses: "No room code yet", "Not paired yet" or "Paired". To open it again, find **Promin agent** in the Start menu. It needs administrator rights.

Pair each PC straight after setting its code. Until it is paired, anyone who knows the code can pair it to a different console. On **Set up devices**, pair only the PCs you can account for. More in [Security](security/).

## If a PC is missing

If a PC does not appear in **Set up devices**:

- Check that it is switched on. The agent starts with Windows, so a restart is enough.
- Check that it is on the same network as the console PC. Guest Wi-Fi and separate VLANs do not work.
- Check its firewall. The agent needs UDP port 17843, and the console PC needs UDP port 17844. The setups add both rules.

If the console says **Wrong room code**, that PC was set up with a different code. Enter the code it was given.
