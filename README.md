# vibranceGUI

VibranceGUI is a Windows Utility written in C# that automates NVIDIAs Digitial Vibrance Control and AMDs Saturation for Games, e.g. Counter-Strike: Global Offensive by utilizing native graphic card driver APIs.

This is a fork of [juv/vibranceGUI](https://github.com/juv/vibranceGUI). Almost all of the code is the original author's work. Upstream's master branch has had no new commits since December 2024, so the changes below are published here instead.

## Download

**[Latest release: v2.10.3](https://github.com/SwatX18/vibranceGUI/releases)** - now in two flavours, **x64 and x86**. Each is a zip with two files, no installer: unzip anywhere and run `vibrance.GUI.exe`. Take the x64 build unless you are on 32-bit Windows; the title bar tells you which one you are running.

The download at vibrancegui.com is the original author's build and contains none of the changes below.

## Before you download

This fork has now been played with, rather than only tested: **vibrance applies when a game takes focus and goes back to the normal Windows level on exit, across several Counter-Strike 2 sessions on one machine.** That is the app's core behaviour, and the part most people use.

It is also the *only* part anyone has watched work on real hardware, and it was watched on an **x86** build. Still unexercised outside automated checks: **resolution switching** (including the restore-on-exit added in v2.8.0), the **colour settings** (gamma, brightness, contrast - off by default), the **separate HDR vibrance level**, where an open question remains over whether NVIDIA's DVC does anything at all while a display is in HDR, and the **apply-on-startup** behaviour.

The **x64 build**, added in v2.9.0, **could not detect an NVIDIA GPU at all until v2.10.1** - it asked Windows for `nvapi.dll`, the 32-bit NVIDIA entry point, which a 64-bit process cannot load, and then reported that no driver was installed. This README previously said the x64 build "resolves the adapter and initialises correctly", and that was written from a machine with *both* an AMD chipset and an NVIDIA card, which takes an entirely different detection path. On an NVIDIA-only machine - most people - it failed at startup, every time, for two releases. It is fixed in v2.10.1, confirmed against the driver on a real machine. Beyond starting, the x64 build still has not been *played* with: nobody has watched it apply and restore vibrance around a real game. If something behaves differently from the x86 build, that is worth reporting; the title bar names which architecture you are on.

The **restore-after-a-crash** behaviour new in v2.10.0 needs the same warning, more strongly: **nobody has yet killed vibranceGUI with a game running and watched a display come back.** Every check behind it drives fake devices. What that means in practice is that the machinery is tested - the rules about when to write the record, when to leave a display alone, how to survive a torn file - but the one thing that matters most, the full kill-and-relaunch cycle on a real monitor, has not been seen working by anyone. It writes two small files under `%APPDATA%\vibranceGUI\` (`vibranceRestore.xml` and `resolutionRestore.xml`); deleting those is always safe and simply forgets any pending restore. The 782 automated checks behind these fixes drive fakes and stubs, not a real GPU driver, display or game, and several fixes were reasoned from the code and documented driver behaviour rather than reproduced on the reporter's own hardware. If you are on a hybrid NVIDIA + AMD laptop or a Thunderbolt eGPU, you are on the least-tested path here. Reports either way are welcome.

## Does it phone home?

Once, on startup, and only to ask how new the newest release is.

vibranceGUI asks `api.github.com` for this repository's latest release tag, compares it to the
version you are running, and shows a tray notification if yours is older. Clicking it opens the
release page in your browser. **Nothing is downloaded, installed or executed** - the app has no
auto-updater and will not get one while it is unsigned.

- It sends no information about you. The request carries a user agent string and nothing else - no
  identifier, no hardware details, no usage data. GitHub sees an anonymous request for a public
  page, the same as if you opened the releases page yourself.
- It runs at most **once every 12 hours**, on a background thread, and gives up after 8 seconds.
- Every failure - offline, proxy, rate limit - is silent. It never shows an error for its own sake.
- **Untick "Check for a new version on startup"** in the settings to turn it off completely, or set
  `updateCheckEnabled=False` in `%APPDATA%\vibranceGUI\vibranceGUI.ini`.

It defaults to on, and that is a deliberate choice rather than an oversight: the x64 builds of
v2.9.0 and v2.10.0 could not detect an NVIDIA GPU at all, and the people running them had no way
to find out a fix existed. If you are on one of those, the notification says so specifically.

## What is different in this fork

Fixed:

- Starts normally on machines with both NVIDIA and AMD drivers installed, instead of refusing to launch (#150, #145, #142, #67).
- A second monitor's saturation is no longer reset on every launch, and vibrance is restored to the display it was actually applied to rather than wherever focus landed (#60, #36, #144, #95).
- Colour and gamma calibration - ICC profiles, f.lux, Night Light - survives a game exiting, instead of being overwritten with a flat ramp (#128, and likely #131).
- Resolution changes no longer strand the desktop at a game's resolution or spam repeated error dialogs (#114, #132).
- Mouse clicks no longer run the full foreground handler. The hook subscribed to a 21-event range that included mouse capture and never filtered by event type (#156); a slower driver-side call on R595+ drivers may be a second, separate factor. Each real foreground change is cheaper too: the process name now comes from the executable path the app already resolves, instead of two machine-wide process enumerations and an `EnumWindows` sweep whose result nothing read.
- A GPU with no display connected no longer sends the app into a runaway loop until it runs out of memory (#138).
- The x64 build detects NVIDIA GPUs. It asked for the 32-bit `nvapi.dll` rather than `nvapi64.dll`, so on any machine where NVIDIA is the only vendor it failed at startup with "failed to determine your graphics adapter" - blaming a driver that was working. Present in the x64 builds of v2.9.0 and v2.10.0, fixed in v2.10.1. The dialog that reported it now names the file it looked for and the build it is, rather than accusing your driver.
- The v2.5.0 colour settings (per-game gamma, brightness and contrast) are included, with their blocking defects fixed. Upstream tagged that feature as released but never merged it to master.

New:

- A game finder that scans Steam, Epic, EA, Battle.net, Rockstar and Ubisoft libraries for installed games.
- A hotkey that toggles a game's profile off and on. It uses `RegisterHotKey` rather than a keyboard hook, deliberately: a keyboard hook is the shape anti-cheat software looks for. Profiles toggled off are marked in the games list.
- Games can be matched by install directory, not only by executable name.
- The game finder also reads Start Menu and desktop shortcuts, so games that register no uninstall entry - portable installs, and anything not installed through a launcher - get found too.
- Command line options (#120): `--help` lists them all, and `--set-vibrance <n>` sets the Windows level from a script or batch file, handing the request to the running instance instead of refusing to start a second one.
- A separate vibrance level for when a display is in HDR (#147). Opt-in per game; leave the box unticked and nothing changes. See the note above about what is unverified here.
- A game that is already running and already focused when vibranceGUI starts now gets its profile applied, instead of waiting until you alt-tab away and back (#81, and the last named mechanism behind #137). It only ever applies a profile on startup, never reverts one.
- The screen resolution is put back when vibranceGUI closes while a game still holds the foreground (#98). Previously only the gamma ramp and the vibrance level were restored on exit, so a game's resolution or refresh rate could be left behind.
- **Vibrance and resolution are put back after a crash, a Task Manager kill or a logoff**, not only after a clean exit (#95, #98, #144). vibranceGUI now records which displays it took away from their normal state and restores them the next time it starts. It only restores a display that is still sitting at the level or mode *it* set — one you have changed yourself since is left alone. The resolution half works on AMD as well as NVIDIA; the vibrance half is NVIDIA-only, because the AMD driver gives no way to read a display's current level back and therefore no way to tell your change from ours. Gamma and colour settings are not covered yet.
- **A 64-bit build.** vibranceGUI ran as a 32-bit process for its whole life, and could not have done otherwise: the NVIDIA calls were bound to a prebuilt 32-bit DLL by 32-bit C++ mangled name, using a calling convention that does not exist on x64. That DLL is now built from its own published source behind a plain C interface, NVIDIA's display and GPU handles are treated as the pointers they actually are rather than as 32-bit integers, and the AMD path picks its driver library by process architecture instead of the machine's. The x86 build is unchanged and still supported.
- **A notification when a newer release exists.** Checked on startup, at most once every 12 hours, on a background thread. It opens the release page in your browser if you click it and does nothing otherwise - there is no auto-installer, and there will not be one while these builds are unsigned. See "Does it phone home?" above for exactly what is sent, and the checkbox that turns it off.

The [v2.10.3 release notes](https://github.com/SwatX18/vibranceGUI/releases/tag/v2.10.3) are the full version. Issue numbers above are the upstream issues a change addresses, not reports confirmed fixed by the people who filed them.

## Graphics card support

As of 18th April 2015, vibranceGUI also fully supports AMD graphic cards. Prior to that, vibanceGUI was developed to support NVIDIA graphic cards only.

Note that NVIDIA Laptop GPUs are not supported because their drivers do not contain the needed functionality.
Intel did not publish an API for their integrated GPUs and are not supported.

## Troubleshooting (v2.5.0+)

On v2.6.0 the "Both NVIDIA and AMD graphic drivers have been found on your system" error (for example on systems with an AMD iGPU and a dedicated NVIDIA GPU) should no longer appear, but if it does - or you are on an older build - you can force the GPU type via a shortcut: create a shortcut to `vibrance.GUI.exe`, open its Properties, and append a space plus `--force-nvidia` or `--force-amd` to the Target path (e.g. `C:\WHATEVER\vibranceGUI\vibrance.GUI.exe --force-nvidia`).

## Compiling

When compiling, make sure to compile for x86 target platform.

Since v2.6.0 there is nothing to fetch from NuGet: no restore step, no `packages` directory. Costura.Fody and the CommonServiceLocator dependency were removed in that release; before v2.6.0 a fresh clone would not build without restoring packages first. The project targets .NET Framework 4.0, so a modern toolchain also needs the 4.0 targeting pack installed (or a `TargetFrameworkVersion` override) to compile.

## Contributing

Every contribution is greatly appreciated. Do not hesitate to submit every issue and pull request that comes to your mind.

## Contact

Support: https://x.com/swatx18

`Please do not add me at Steam to ask questions about vibranceGUI. Thank you.`
