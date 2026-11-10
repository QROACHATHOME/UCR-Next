# UCR-Next

A maintained, from-scratch successor to evilC's **Universal Control Remapper (UCR)**. Great thanks to evilC and the work he put into making the original UCR, without it the emulation scene wouldn't be the same!
It remaps any input (gamepad, joystick, wheel, keyboard, mouse) to any output (keyboard, mouse,
a **virtual Xbox 360 controller** or **vJoy** joysticks), organised in profiles that are saved as JSON.
It can run silently in the background, start with Windows, and be controlled from the command line.

Status: **0.5**. Windows 10/11 only.

**Download:** grab `UCR-Next-<version>-win-x64.zip` from the
[Releases](https://github.com/QROACHATHOME/UCR-Next/releases) page, unzip anywhere and run
`UCR-Next.exe` — no .NET install needed. Then install the drivers you need (see Requirements).

## Features

- **Inputs**
  - Xbox-compatible controllers through **XInput** (14 buttons, 4 stick axes, 2 triggers)
  - Other sticks, wheels, HOTAS, generic pads through **DirectInput**: up to 8 axes, 128 buttons,
    4 POV hats each (falls back to the WinMM joystick API if DirectInput is unavailable)
  - Keyboard and mouse buttons through low-level hooks ("any keyboard / any mouse")
  - **Mouse / trackball / spinner movement and the mouse wheel** through Raw Input — no driver
    needed, any mouse or one specific mouse (`rawmouse:VID:PID#n`)
  - **Per-keyboard keys** through Raw Input (`rawkbd:VID:PID#n`) — no driver needed, but read-only:
    the original key still reaches other programs. To block keys on one keyboard only, use Interception.
  - **Per-device** keyboards and mice through the optional **Interception** driver
- **Outputs**: keyboard, mouse buttons, mouse movement and **mouse wheel** (one notch per button press,
  or continuous scrolling from an axis; SendInput, scan codes), up to **four virtual Xbox 360** pads
  (P1–P4, ViGEmBus; plugged in in order so games number them correctly), **vJoy** devices 1–16
- **Bindings**: any input → any output, with deadzone, anti-deadzone, sensitivity (linear or UCR's
  curve), invert; analog → button past a threshold; button → axis with pressed/released values;
  **Toggle** (press for on, again for off) and **Pulse** (a fixed-length press on each press or release) for buttons
- **Binding modes** (*Mode* on the binding's second line, *Settings…* for all options):

  | Mode | What it does | UCR plugin |
  |---|---|---|
  | Direct | one input → one output (optionally only the high/low half of an axis) | Button/Axis to Button/Axis, Axis Splitter |
  | ButtonsToAxis | two buttons → one axis (+1 / -1 / centre) | Buttons to Axis |
  | StickPair | two axes as one stick: circular deadzone, rotation | Axes to Axes, Axes to Axes (Rotation) |
  | StickTrim | stick pair + trim button / reset trim | Axes to Axes (Trim) |
  | AxesRange | button while both axes are inside a box | Axes Range to Button |
  | DeltaToAxis | mouse/trackball movement → axis, absolute (returns to centre) or relative | Delta to Axis |
  | DeltaToButtons | movement → button per direction, with centre timeout and debounce | Delta to Buttons |
  | LongPress | short press and long press of one button → two outputs | Button to Buttons (Long Press) |
  | Increment | axis moves the output gradually | Axis to Axis Increment |
  | AxisMerger | two axes → one: average, greatest or sum (e.g. two pedals on one axis) | Axis Merger |
  | AxisToDelta | stick → mouse movement, Min–Max pixels per 10 ms | Axis to Delta |
  | DefaultValue | no input: an output axis starts at this value until something moves it | Axis Initializer |
  | FpvMix | roll and yaw mixed by a camera angle | FPV Angle Mix |
  | ReversibleThrottle | throttle drives half an axis; a button flips forward / reverse | Reversible Throttle |
  | SetState | a button (or an axis entering a range) switches a named state on, off or toggles it | Button / Axis to Filter |
- **States (shift layers)**: a *SetState* binding switches a named state such as `shift`; any binding can be
  limited to *Only while states* `shift` (or `!shift` = while off) in its *Settings*. One button can then do
  different things with and without a shift button held, or with a "menu" state toggled on. Each profile
  has its own states; a binding that is switched off by a state releases its output.
- **Blocking**: tick *Block* on a keyboard/mouse binding and the original key/button no longer
  reaches other programs while mapping is on (any device via hooks, one device via Interception)
- **Per-application profiles**: list programs (e.g. `game.exe`) on a profile and it is only active
  while one of them is in the foreground; profiles without programs are always active
- **Skip while a device is connected**: e.g. a "pad as steering wheel" profile stays off — and creates
  no virtual controller — while your real wheel is plugged in
- **Games list per profile**, plus `ucrnext game "<name>"` to switch to the right profile from a launcher
- **UCR import**: *File → Import UCR profiles* reads UCR's `context.xml` (see below)
- **Background mode**: tray icon (optional), start hidden at Windows logon, optionally as administrator
- **Command line**: `ucrnext.exe status | start | stop | enable <profile> | solo <profile> | …`
- **Save, Revert, Undo**: edits take effect at once so you can try them, but are only written to disk
  when you **Save** (Ctrl+S). **Revert** goes back to the saved profiles; **Undo / Redo** (Ctrl+Z / Ctrl+Y)
  step through your edits (a Revert or a deleted profile can be undone too). Closing with unsaved edits asks
  first. Switching a profile on or off is not an edit: it applies and is saved at once.
- **Copy, paste, duplicate, delete profiles**: the buttons above the profile list, its right-click menu,
  or Ctrl+C / Ctrl+V / Ctrl+D / Del. Profiles and bindings are copied as text, so you can also paste them
  into a message to share them, or use *File → Export / Import profile*. Copies start switched off.
  Deleted profiles are moved to the profiles folder's `deleted` subfolder when you save.
- **Groups**: sort a profile's bindings into named, coloured groups (e.g. *Player 1 pad*, *Keyboard*).
  *+ Group* makes one, each binding's *Group* box moves it, *Group by controller* sorts every ungrouped
  binding by its input device. Groups fold away, can be reordered, and copied into other profiles.
- **Recognise your devices**: keyboards and mice show the product name they report (e.g. *I-PAC 2*)
  instead of only VID:PID. A device lights up in *Detected devices* when you press something on it.
  Double-click a device to give it your own name, notes and a picture (choose a file or paste one);
  the name and picture also appear on bindings and in *Group by controller*. Devices you never use
  can be hidden. Stored in `settings.json`; pictures are copied to `%APPDATA%\UCR-Next\device-images`.
- **Light and dark themes**: *View* menu — follow Windows (default), light or dark; uses the Windows accent colour
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

### Making a release

Bump `<Version>` in `Directory.Build.props`, commit, then tag and push the tag:

```powershell
git tag v0.5.0
git push origin v0.5.0
```

GitHub Actions (`.github/workflows/release.yml`) runs the tests, builds the self-contained app and
publishes it as `UCR-Next-0.5.0-win-x64.zip` on a new release with notes generated from the commits.

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
ucrnext games                  list games and the profiles they use
ucrnext game <game> [--start]  enable only the profile(s) listing this game (exact, or unique partial name)
ucrnext import <context.xml> [--per-device]   import UCR profiles (works even when UCR-Next is closed)
ucrnext save                   save edits made in the window
ucrnext reload                 reload profiles from disk (after editing the JSON by hand; drops unsaved edits)
ucrnext show | hide            show or hide the window
ucrnext quit [--save|--discard]  exit UCR-Next (with unsaved edits it refuses unless told what to do with them)

ucrnext launch [--background] [--start]           start UCR-Next if it is not running
ucrnext autostart [status|off|logon|elevated]     start with Windows
```

Exit code 0 = OK, 1 = error or UCR-Next not running, 2 = usage error. Handy for scripts,
launchers (e.g. HyperSpin/RocketLauncher pre-launch commands) and Stream Deck buttons:
`ucrnext solo "Racing Wheel" && ucrnext start`.

## Profile settings: programs, games, devices

Expand **Profile settings** above the bindings list:

- **Only active while these programs are in front** — `game.exe, emulator.exe`; empty = everywhere.
- **Games that use this profile** — one per line, free text. Shown in the profile list and used by
  `ucrnext game "Daytona USA" --start`, which enables exactly the profile(s) listing that game.
  Handy as a launcher pre-launch command (HyperSpin/RocketLauncher, LaunchBox, Playnite…).
- **Skip this profile while connected** — *Add connected device* picks a plugged-in controller.
  While any listed device is connected the profile is inactive and no virtual controller is created for
  it. Devices are matched by id (`dinput:VID:PID#n`, so moving the wheel to another USB port is fine)
  or by name. `ucrnext status` shows "Skipped (device connected)".

## Importing UCR profiles

UCR keeps everything in `context.xml` in the folder where `UCR.exe` is. Copy that file to this PC and
use *File → Import UCR profiles…*, or `ucrnext import path\to\context.xml`.

- Every UCR profile becomes a UCR-Next profile; child profiles are flattened into
  "Parent › Child" with the parent's mappings (child mappings with the same name override them).
- Imported profiles start **disabled** (UCR ran one profile at a time) — tick the ones you want,
  or use `ucrnext solo <profile>` / `ucrnext game <game>`.
- Re-importing the same file updates the bindings and keeps the games, programs, device rules and
  enabled state you added in UCR-Next.
- An import report lists everything that was approximated or skipped.

| UCR | UCR-Next |
|---|---|
| Inputs: XInput, DirectInput (VID/PID + instance), Interception keyboard keys, mouse buttons, mouse movement and wheel | same devices; keyboard/mouse become "any keyboard/mouse" unless you choose per-device — then Interception ids if Interception is running (can block), otherwise Raw Input ids (no driver, can't block; the report lists bindings that blocked in UCR) |
| Outputs: ViGEm Xbox 360 pads 1–4, vJoy axes/buttons, Interception keyboard keys / mouse buttons / mouse movement | virtual Xbox 360 pads P1–P4, vJoy, SendInput |
| Plugins: Button to Button, Axis to Axis, Axes to Axes, Axis to Button, Button to Axis, Buttons to Axis, Axis Splitter, Axis Merger, Axis Initializer, Button to Event, Button to Filter, Axis to Filter, and HidWizards/UCR-Plugins: Axes to Axes (Rotation), Axes to Axes (Trim), Axes Range to Button, Axis to Axis Increment, Button to Buttons (Long Press), Delta to Axis, Delta to Buttons, Axis to Delta, Event to Button, FPV Angle Mix, Reversible Throttle | converted to the binding modes above with the same maths as UCR (deadzones incl. circular, anti-deadzone, UCR's sensitivity curve, full-range triggers, timers) |
| Block flag on inputs | *Block* on keyboard/mouse bindings |
| Plugin filters | *Only while states* on the bindings; Button/Axis to Filter become *SetState* bindings. Unlike UCR, a binding switched off by a state releases its output instead of freezing it |
| Events | mouse wheel up/down/left/right: Button to Event → wheel output (one notch per press/release), Event to Button → a pulse of the hold time |
| Not converted (reported) | shadow devices, DS4 output, vJoy POV output |

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
2. **+ Binding**, press **A** on the pad, choose **Key: Space** as output.
3. **+ Binding**, push the left stick fully right, choose **Xbox 360: LeftX** (or a vJoy axis).
   Raise *Deadzone* (first slider) to about 0.15 if the stick drifts.
4. Optional: type `game.exe` under *Only active while these programs are in front*, or use
   *Add running program*.
5. **Start mapping** and try it. Check the virtual pad in `joy.cpl`.
6. **Save** (Ctrl+S) when you're happy, or **Revert** to go back to what was saved.

## Project layout

```
src/UcrNext.Core      engine (net8.0, no Windows deps): model, axis shaping, MappingEngine
                      (per-app filter, blocking, Xbox/vJoy aggregation), EngineRunner (250 Hz),
                      ProfileStore, AppSettings, CommandProcessor + named-pipe transport
src/UcrNext.Devices   Windows: XInput, DirectInput (Vortice), WinMM fallback, keyboard/mouse hooks,
                      Interception, SendInput, ViGEm, vJoy, foreground watcher, autostart
src/UcrNext.App       WPF UI (Fluent look via WPF-UI), tray icon, background mode, single instance, pipe server
src/UcrNext.Cli       ucrnext.exe
tests/UcrNext.Core.Tests  xUnit tests (engine, per-app, blocking, vJoy, commands, storage)
```

## Known limitations

- Blocking applies to keyboard keys and mouse buttons, not to gamepads (that would need HidHide).
- "Any keyboard" key mappings don't fire while UCR-Next's own window has focus (Windows stops
  feeding the keyboard hook to a program that registered Raw Input keyboards while it is in front).
  Press-to-bind is not affected, and games/other programs in front work normally.
- Press-to-bind writes what it saw to `%LOCALAPPDATA%\UCR-Next\bind-debug.log` — attach it when
  reporting a binding problem.
- Third-party XInput pads may appear twice (XInput and DirectInput); bind the XInput one.
- DirectInput reports only the axes a device exposes to DirectInput, which can be fewer than vJoy's
  own configuration shows.
- No plugin system yet.

## License

MIT — see [LICENSE](LICENSE). UCR-Next contains no code from UCR; UCR's open-source code was only read to understand its `context.xml` format and plugin behaviour so profiles import faithfully. Third-party drivers (ViGEmBus, vJoy, Interception) are not included and keep their own licenses.
