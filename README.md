# LightCraft MIDI

A Windows tray app that connects a MIDI controller to [LightCraft](https://github.com/storytold/lightcraft).
Faders and knobs drive develop sliders. Buttons and pads run LightCraft commands.

## Requirements

- Windows 10/11 and the .NET 10 runtime
- LightCraft started with its control server enabled:

  ```
  lightcraft --control 7980
  ```

  You can also set `LIGHTCRAFT_CONTROL_PORT=7980`.

## Build and run

```
dotnet build LightCraftMidi.slnx
dotnet run --project src\LightCraftMidi
dotnet test
```

The app runs in the background with a tray icon. Double-click the icon, or choose **Open configuration**, to open the configuration window.
Closing the window hides it. Use **Exit** in the tray menu to quit.
Launching the app a second time brings the existing instance's window to the front.
The tray menu can also turn mapping on and off and force a reconnect.

Open **Settings** for LightCraft host/port, MIDI input/output and feedback options.
MIDI selections and feedback remain specific to the active profile. Click **Connect**
to save and apply LightCraft connection changes; other settings save immediately.
**Hide window on start** defaults to enabled: the app starts in the tray, including
on first launch. Disable it to open configuration at startup, or pass `--show` for
a one-time override. The icon-only play/pause toggle in the main toolbar enables
or pauses mapping; its tooltip shows the current state.

## Windows installer (MSIX)

### GitHub Actions

The **Build, test and package** workflow runs on pushes to `main`, pull requests,
and manually from the repository's **Actions** tab. It uses a Windows runner with
.NET 10 to build the solution, run all tests, and create the self-contained x64 MSIX.
Packaging and installer upload only run after successful build and tests.

Download the `LightCraftMidi-<version>-win-x64-unsigned` artifact from the completed
run for the MSIX and checksum. Test results are uploaded separately, including on
test failures. Installer artifacts are retained for 30 days; test results for 14 days.
Manual runs accept a four-part package version (default `1.0.0.0`).
Packages remain unsigned; no signing keys or repository secrets are required.

### Local packaging

Build a self-contained Windows x64 package (no separate .NET runtime installation):

```powershell
.\packaging\Build-Msix.ps1
```

Requires the .NET 10 SDK and the Windows SDK's x64 MakeAppx tool.
The output is `artifacts\msix\LightCraftMidi_1.0.0.0_x64.msix`, plus a SHA-256 checksum.
Use `-Version 1.0.1.0` to build an update, and `-Publisher "CN=YourPublisher"` to
match your signing certificate. Keep the identity and publisher stable across updates.

The generated package is **unsigned** and cannot be installed normally until signed.
Sign with the Windows SDK's SignTool using an appropriate code-signing certificate
whose subject exactly matches the package publisher:

```powershell
signtool sign /fd SHA256 /sha1 YOUR_CERTIFICATE_THUMBPRINT artifacts\msix\LightCraftMidi_1.0.0.0_x64.msix
signtool verify /pa /v artifacts\msix\LightCraftMidi_1.0.0.0_x64.msix
```

For production, use a trusted signing service or certificate and timestamp the signature
using your provider's documented timestamp URL. For testing, recipients must explicitly
trust a self-signed certificate before installing. Do not distribute private keys.
Signing changes the package bytes: regenerate the checksum afterward with `Get-FileHash`.
Once signed and trusted, double-click the MSIX to install with Windows App Installer.
It adds a Start menu entry; startup is hidden in the tray by default.
Minimum packaged OS: Windows 10 version 2004 (build 19041).

## Mapping workflow

1. **LightCraft.** In **Settings**, set the host and port, then click **Connect**. The app reconnects automatically. Once connected, the right pane lists every develop control (grouped by panel, with its range) and every command (grouped by menu). Use the search box to filter both lists.
2. **MIDI.** In **Settings**, pick the input device, and the output device if your controller has motor faders or LED rings. Devices are reopened automatically when they are unplugged and plugged back in.
3. **Discover the controller.** Move every knob, fader and button once. The *MIDI controller* list detects each one and classifies it as a button, a fader/knob, or a 14-bit pitch-bend fader. Relative encoders are flagged as unsupported. The *MIDI monitor* shows the raw traffic and which mapping fired.
4. **Map.** Select a detected MIDI control and a target in **Knobs**, **Commands**, or **Profiles**, then click **Add** to create a mapping with both selections. Missing selections can be assigned later; **Learn** captures a MIDI source when needed. The large left-pointing arrow assigns the selected target to an existing mapping. The right-pointing arrow assigns the selected detected MIDI source to the selected mapping (or creates a mapping when none is selected). Double-clicking an individual source or target also assigns it.
5. **Fine-tune** the selected mapping:
   - Channel (or *any*), invert, a custom target range, and per-mapping feedback.
   - For commands: the trigger (on press, or on any change) and params as JSON, for example `{"rating": 4}`.
   - Warnings flag duplicates, unknown targets, and type mismatches.

Changes are saved immediately.

## Behaviour

- Slider updates are coalesced into `develop.set` calls. Each fader move is wrapped in a `develop.beginInteraction` / `develop.endInteraction` pair, so it is a single undo step.
- Centred controls snap to exactly 0 when the fader is at the middle position.
- **Feedback.** Every 300 ms the app polls LightCraft's control values. Any changed values are sent back to the controller, so motor faders and LEDs follow photo changes and edits made with the mouse. A control is skipped while you are moving it.

## Profiles

Profiles and settings are stored as JSON in `%APPDATA%\LightCraftMidi`:

- `settings.json` holds the connection settings and the active profile.
- Each profile is saved as `profiles\<name>.json`.

Each profile remembers its MIDI devices. You can create, duplicate, rename and delete profiles from the Profile bar.

### Global profile buttons

Use the normal mapping editor: select a detected MIDI button and a profile in the
**Profiles** target tab, then click **Add**. Alternatively, assign a profile to an existing
mapping with the left-pointing arrow beside the mapping list.
The mapping automatically moves to the **Global mappings** group at the top of the
mapping list, separated from **Profile mappings** by group headers. Global is not
shown in the profile selector. Learn, source editing, triggers and removal work just like other mappings.
Global is saved as `profiles\Global.json`; older global button assignments in
`settings.json` are migrated automatically.

Global mappings remain visible and active alongside whichever profile is selected.
Editing a global row saves it to Global without changing the selected profile or its
MIDI devices. Global can also contain ordinary slider and command mappings, but it cannot
be renamed, deleted, or used as a profile-switch target. Duplicating a profile copies
only that profile's mappings. Renaming a normal profile updates its switch targets; deleting one
also removes its global switch mappings after confirmation.

Use **OnPress** for ordinary momentary buttons (release is ignored), or **OnAnyChange**
for CC toggle buttons. Global buttons take priority over the same source in a profile,
receive no slider feedback, and continue working while LightCraft mapping is paused
or disconnected. MIDI Learn captures the button without switching profiles.
Conflicting profile-switch sources are flagged; the first assignment wins.

Buttons listen on the active profile's selected MIDI input. Keep the same input in
profiles you want to switch between using one controller.

## Notes

- The app uses NAudio.Midi 2.2.1 (winmm). Version 3.x switched to WinRT-only MIDI.
- MIDI devices cannot describe their own layout. Capability discovery therefore relies on device enumeration plus classifying each control as you move it.
- Relative (endless) encoders and start-with-Windows are not supported in this version.
