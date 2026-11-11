# Turning off Smart App Control for Promin

[Українською](smart-app-control-uk.md)

Promin's programs are not signed yet ([#49](https://github.com/andriykohut/promin/issues/49)). Windows 11 has a protection called **Smart App Control** that blocks unsigned programs, and it offers no way to allow just one program. On every PC that runs Promin, it has to be **Off**, and it has to stay off.

- **On**: Windows blocks Promin.
- **Evaluation**: nothing is blocked yet, but Windows can switch it to On by itself later, and from then on Promin stops starting.
- **Off**: Promin works.

Turning Smart App Control off makes the PC less protected against unknown programs. If someone else manages the PC, agree on it with them first.

## Signs it is on

- The Promin setup opens on a page headed **IMPORTANT: Windows will block Promin on this PC**.
- Windows shows "Part of this app has been blocked … Promin.Agent.exe".
- The agent setup ends with "could not be set up as a service (exit code 4551)".

## Turn it off

1. Open **Start**, type **Windows Security** and open it.
2. Choose **App & browser control**.
3. Under **Smart App Control**, choose **Smart App Control settings**.
4. Choose **Off**. If Windows asks for permission, choose **Yes**.
5. Go back to the Promin setup and choose **Check again**, then **Next** and **Install**.

If the setup already finished with exit code 4551, turn Smart App Control off as above and run the setup again.

## If the switch is greyed out

The PC is managed by an organisation, and only its administrator can change this setting. Ask them to turn Smart App Control off or to allow Promin.

## Turning it back on

On Windows 11 with updates from April 2026 or later, Smart App Control can be turned on again in the same place. On older versions, turning it off is permanent until Windows is reinstalled. While Promin is installed, keep it off: with Smart App Control on, Windows blocks Promin each time it starts.
