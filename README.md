# UCR-Next

A maintained, from-scratch successor to evilC's **Universal Control Remapper (UCR)**.
It remaps any input (gamepad, joystick, wheel, keyboard, mouse) to any output (keyboard, mouse,
a **virtual Xbox 360 controller** or **vJoy** joysticks), organised in profiles that are saved as JSON.
It can run silently in the background, start with Windows, and be controlled from the command line.

Status: **0.2**. Windows 10/11 only.

## Features

- **Inputs**
  - Xbox-compatible controllers through **XInput** (14 buttons, 4 stick axes, 2 triggers)
  - Other sticks, wheels, HOTAS, generic pads through **DirectInput**: up to 8 axes, 128 buttons,
    4 POV hats each (falls back to the WinMM joystick API if DirectInput is unavailable)
  - Keyboard and mouse buttons through low-level hooks ("any keyboard / any mouse")
  - **Per-device** keyboards and mice through the optional **Interception** driver
- **Outputs**: keyboard and mouse (SendInput, scan codes), virtual **Xbox 360** pad (ViGEmBus),
  **vJoy** devices 1–16 (all configured axes and buttons)
- **Bindings**: any input → any output, with deadzone, sensitivity, invert;
  analog → button past a threshold; button → full axis
- **Blocking**: tick *Block* on a keyboard/mouse binding and the original key/button no longer
  reaches other programs while mapping is on (any device via hooks, one device via Interception)
- **Per-application profiles**: list programs (e.g. `game.exe`) on a profile and it is only active
  while one of them is in the foreground; profiles without programs are always active
- **Background mode**: tray icon (optional), start hidden at Windows logon, optionally as administrator
- **Command line**: `ucrnext.exe status | start | stop | enable <profile> | solo <profile> | …`
- **Safety**: **Ctrl+Alt+Pause** always stops mapping and blocking; keys are never left stuck
  when bindings change, profiles switch, or mapping stops

## Requirements

| What | Needed for | Where |
|---|---|---|
| **.NET 8 SDK** (build) / .NET 8 Desktop Runtime (run) | everything | `winget install Microsoft.DotNet.SDK.8` |
| **ViGEmBus** | virtual Xbox 360 output | https://github.com/nefarius/ViGEmBus/releases |
| **vJoy** | vJoy output | https://github.com/jshafer817/vJoy/releases (configure devices with *Configure vJoy*) |
| **Interception** | per-device keyboard/mouse, blocking one device only | https://github.com/oblitum/Interception/releases |

None of the drivers are bundled; every one is optional. Missing drivers are reported in
*Help → Driver and device status* and in `ucrnext status`, and the rest of the app keeps working.

### Installing Interception (optional)

1. Download `Interception.zip` from the releases page and unzip it.
2. In an **administrator** command prompt: `"command line installer\install-interception.exe" /install`, then **reboot**.
3. Copy `library\x64\interception.dll` next to `UCR-Next.exe`
   (or set `InterceptionDllFolder` in `%APPDATA%\UCR-Next\settings.json`).
4. In UCR-Next: *Settings → Use Interception driver*, then restart UCR-Next.

Interception is a kernel filter driver; while UCR-Next runs with it, every key press passes through
UCR-Next. It is built to always pass input on if anything goes wrong, and if UCR-Next exits or
crashes the driver lets input through again. Known driver limitation: after many hundreds of
device re-plugs without a reboot it can stop seeing new devices — reboot to fix.

## Build and run

```powershell
cd "D:\My AI projects\UCR-Next"
dotnet build
dotnet test
dotnet run --project src\UcrNext.App
```

Release build into one folder (contains `UCR-Next.exe` and `ucrnext.exe`):

```powershell
dotnet publish src\UcrNext.App -c Release -o publish
```

### Startup options (`UCR-Next.exe`)

| Option | Effect |
|---|---|
| `--background` | start without showing the window |
| `--start` | turn mapping on immediately |
| `--no-tray` | no tray icon (control it with `ucrnext.exe`) |
| `--profiles DIR` | use another profile folder |

Only one instance runs per user. Launching it again shows the existing window (or, with `--start`,
turns mapping on) and exits.

## Command line: `ucrnext.exe`

`ucrnext.exe` sits next to `UCR-Next.exe` and talks to the running instance over a per-user named pipe.

```
ucrnext status                 mapping state, active profiles, foreground app, drivers, devices
ucrnext start | stop | toggle  turn mapping on / off
ucrnext profiles               list profiles ([x] = enabled, apps they are limited to)
ucrnext enable <profile>       enable a profile   (case-insensitive, unique prefix is enough,
ucrnext disable <profile>      disable a profile   quote names with spaces)
ucrnext solo <profile>         enable only this profile
ucrnext reload                 reload profiles from disk (after editing the JSON by hand)
ucrnext show | hide            show or hide the window
ucrnext quit                   exit UCR-Next

ucrnext launch [--background] [--start]           start UCR-Next if it is not running
ucrnext autostart [status|off|logon|elevated]     start with Windows
```

Exit code 0 = OK, 1 = error or UCR-Next not running, 2 = usage error. Handy for scripts,
launchers (e.g. HyperSpin/RocketLauncher pre-launch commands) and Stream Deck buttons:
`ucrnext solo "Racing Wheel" && ucrnext start`.

## Running in the background / with Windows

*Settings → Start with Windows (hidden)*, or `ucrnext autostart …`:

- **At logon**: adds `UCR-Next.exe --background --start` to your user's Run key. No admin needed.
- **At logon, as administrator**: creates a Task Scheduler task (logon trigger, highest privileges,
  no time limit, runs on battery). One UAC prompt. Choose this if your games run as administrator —
  Windows does not let a normal program send input to an elevated one. When it runs elevated,
  run `ucrnext` from an elevated prompt as well.

**Why not a Windows service?** Services run in an isolated session (session 0) that has no access to
your keyboard, mouse, or desktop, so a service can neither read your physical input nor send
key presses to your games. The logon task gives the same "always on, no window" behaviour while
running on your desktop where remapping works.

Related settings: *Start mapping when UCR-Next starts*, *Show tray icon*, *Keep running in the tray
when the window is closed*. Settings are stored in `%APPDATA%\UCR-Next\settings.json`.

## Quick start

1. Plug in your controller; it appears under **Detected devices**.
2. **+ Add binding**, press **A** on the pad, choose **Key: Space** as output.
3. **+ Add binding**, push the left stick fully right, choose **Xbox 360: LeftX** (or a vJoy axis).
   Raise *Deadzone* (first slider) to about 0.15 if the stick drifts.
4. Optional: type `game.exe` under *Only active while these programs are in front*, or use
   *Add running program*.
5. **▶ Start mapping**. Check the virtual pad in `joy.cpl`.

## Project layout

```
src/UcrNext.Core      engine (net8.0, no Windows deps): model, axis shaping, MappingEngine
                      (per-app filter, blocking, Xbox/vJoy aggregation), EngineRunner (250 Hz),
                      ProfileStore, AppSettings, CommandProcessor + named-pipe transport
src/UcrNext.Devices   Windows: XInput, DirectInput (Vortice), WinMM fallback, keyboard/mouse hooks,
                      Interception, SendInput, ViGEm, vJoy, foreground watcher, autostart
src/UcrNext.App       WPF UI, tray icon, background mode, single instance, pipe server
src/UcrNext.Cli       ucrnext.exe
tests/UcrNext.Core.Tests  xUnit tests (engine, per-app, blocking, vJoy, commands, storage)
```

## Known limitations

- Blocking applies to keyboard keys and mouse buttons, not to gamepads (that would need HidHide).
- Mouse movement/wheel cannot be used as an input yet.
- Third-party XInput pads may appear twice (XInput and DirectInput); bind the XInput one.
- DirectInput reports only the axes a device exposes to DirectInput, which can be fewer than vJoy's
  own configuration shows.
- No plugin system yet.
