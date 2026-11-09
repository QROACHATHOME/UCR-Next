# UCR-Next

A maintained, from-scratch successor to evilC's **Universal Control Remapper (UCR)**.
It remaps any input (gamepad, joystick, keyboard, mouse) to any output (keyboard, mouse, or a
**virtual Xbox 360 controller**), organised in profiles that are saved as JSON.

Status: **0.1 MVP**. Windows 10/11 only.

## Features (MVP)

- **Device detection** (refreshes every 2 s)
  - Xbox-compatible controllers through **XInput** (14 buttons, 4 stick axes, 2 triggers)
  - Other gamepads, flight sticks, wheels through **DirectInput/HID** (WinMM joystick API:
    up to 16 devices, 6 axes, 32 buttons, POV hat as 4 buttons)
  - Keyboard and mouse buttons through low-level hooks (observe only, no blocking)
- **Bindings**: any input → any output
  - button → key, button → mouse button, button → Xbox 360 button
  - axis → Xbox 360 stick/trigger, axis → mouse movement, axis → button (past a threshold)
  - per-binding **deadzone**, **sensitivity** and **invert**
- **Outputs**: keyboard (SendInput, scan codes for game compatibility), mouse, and a
  virtual **Xbox 360 controller via ViGEmBus**
- **Profiles**: create/rename/delete, enable/disable individually (all enabled profiles run
  together), auto-saved to `%APPDATA%\UCR-Next\profiles\*.json`
- **Press to bind**: click *Bind…*, then press the button / move the stick you want
- State-based engine at 250 Hz: keys are never left stuck when you stop mapping or delete a binding

## Requirements

| What | Why | Where |
|---|---|---|
| **.NET 8 SDK** (to build) or .NET 8 Desktop Runtime (to run a build) | the app is C#/.NET 8 + WPF | `winget install Microsoft.DotNet.SDK.8` or https://dotnet.microsoft.com/download/dotnet/8.0 |
| **ViGEmBus** driver (optional) | only needed for the virtual Xbox 360 controller output | https://github.com/nefarius/ViGEmBus/releases (install the latest `ViGEmBus_*_x64_x86_arm64.exe`, reboot if asked) |

ViGEmBus is not bundled. Its author has retired the project but the last release still works on
Windows 10/11. Without it the app runs normally and shows *"ViGEmBus not available"*;
keyboard and mouse outputs still work.

## Build and run

```powershell
cd "D:\My AI projects\UCR-Next"
dotnet build
dotnet test
dotnet run --project src\UcrNext.App
```

Release build into a folder you can copy anywhere:

```powershell
dotnet publish src\UcrNext.App -c Release -o publish
.\publish\UCR-Next.exe
```

Use a different profile folder (handy for testing): `UCR-Next.exe --profiles D:\some\folder`.

## Quick start: a button to a key, a stick to a virtual Xbox stick

1. Plug in your controller; it shows up under **Detected devices** on the right.
2. In the middle pane click **+ Add binding**. The *Press to bind* box opens: press **A** on the
   pad. Choose **Key: Space** in the *Output* drop-down (type "K" to jump to the keys).
3. **+ Add binding** again, push the **left stick fully right**, and pick
   **Xbox 360: LeftX** as output. Do the same pushing the stick **up** → **Xbox 360: LeftY**.
   Raise *Deadzone* (first slider) if the stick drifts.
4. Click **▶ Start mapping**. Pressing A now types a space, and a new virtual Xbox 360 controller
   follows your stick (check with `joy.cpl` → *Controller (XBOX 360 For Windows)* → Properties).
5. Close and reopen the app: the profile is reloaded from `%APPDATA%\UCR-Next\profiles`.

## Project layout

```
UcrNext.sln
src/UcrNext.Core      engine (net8.0, no Windows deps): model, axis shaping, MappingEngine,
                      EngineRunner (250 Hz loop), ProfileStore (JSON)
src/UcrNext.Devices   Windows providers/outputs: XInputProvider, WinMmJoystickProvider,
                      KeyboardMouseHookProvider, SendInputOutput, ViGEmPadOutput
src/UcrNext.App       WPF UI: profiles, bindings, devices, press-to-bind dialog
tests/UcrNext.Core.Tests  xUnit tests for axis shaping, engine and profile storage
```

Adding a device type = implement `IInputProvider` and register it in `AppServices`.
Adding an output type = extend `OutputTarget`/`OutputRef` and handle it in `MappingEngine`.

## Known limitations (planned next)

- Keyboard/mouse inputs are **not blocked** (the original key still reaches the game) and cannot
  be told apart per device. Planned: optional **Interception** driver support.
- No vJoy output, no plugin system, no per-application profile switching yet.
- DirectInput devices go through WinMM: max 6 axes / 32 buttons / 1 POV. A full DirectInput or
  Raw Input provider is planned for devices with more controls.
- Third-party XInput pads may appear twice (XInput and DirectInput); bind the XInput one.
- Sending input to programs running **as administrator** requires running UCR-Next as administrator too.
