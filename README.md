# vibranceGUI

VibranceGUI is a Windows Utility written in C# that automates NVIDIAs Digitial Vibrance Control and AMDs Saturation for Games, e.g. Counter-Strike: Global Offensive by utilizing native graphic card driver APIs.

This is a fork of [juv/vibranceGUI](https://github.com/juv/vibranceGUI). Almost all of the code is the original author's work. Upstream's master branch has had no new commits since December 2024, so the changes below are published here instead.

## Download

**[Latest release: v2.6.0](https://github.com/SwatX18/vibranceGUI/releases)** - a zip with two files, no installer. Unzip it anywhere and run `vibrance.GUI.exe`.

The download at vibrancegui.com is the original author's build and contains none of the changes below.

## Before you download

v2.6.0 is this fork's first release, and nothing in it has been run against a live game by anyone, including me. The fixes are backed by 338 automated checks, but those drive fakes and stubs, not a real GPU driver, display or game. Several of them were validated by reading the code and reasoning about documented driver behaviour, not by reproducing the original bug on the reporter's hardware. If you are on a hybrid NVIDIA + AMD laptop or a Thunderbolt eGPU, you are on the least-tested path here. Reports either way are welcome.

## What is different in this fork

Fixed:

- Starts normally on machines with both NVIDIA and AMD drivers installed, instead of refusing to launch (#150, #145, #142, #67).
- A second monitor's saturation is no longer reset on every launch, and vibrance is restored to the display it was actually applied to rather than wherever focus landed (#60, #36, #144, #95).
- Colour and gamma calibration - ICC profiles, f.lux, Night Light - survives a game exiting, instead of being overwritten with a flat ramp (#128, and likely #131).
- Resolution changes no longer strand the desktop at a game's resolution or spam repeated error dialogs (#114, #132).
- Mouse clicks no longer run the full foreground handler. The hook subscribed to a 21-event range that included mouse capture and never filtered by event type (#156); a slower driver-side call on R595+ drivers may be a second, separate factor.
- A GPU with no display connected no longer sends the app into a runaway loop until it runs out of memory (#138).
- The v2.5.0 colour settings (per-game gamma, brightness and contrast) are included, with their blocking defects fixed. Upstream tagged that feature as released but never merged it to master.

New:

- A game finder that scans Steam, Epic, EA, Battle.net, Rockstar and Ubisoft libraries for installed games.
- A hotkey that toggles a game's profile off and on. It uses `RegisterHotKey` rather than a keyboard hook, deliberately: a keyboard hook is the shape anti-cheat software looks for. Profiles toggled off are marked in the games list.
- Games can be matched by install directory, not only by executable name.

The [v2.6.0 release notes](https://github.com/SwatX18/vibranceGUI/releases/tag/v2.6.0) are the full version. Issue numbers above are the upstream issues a change addresses, not reports confirmed fixed by the people who filed them.

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
