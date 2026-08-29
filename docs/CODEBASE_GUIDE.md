# vibranceGUI — Codebase Guide

> **What this document is.** A complete technical reference for the vibranceGUI source tree: what the
> program does for its users, how it is built, how it is wired together, what happens at runtime, what
> is broken, and what you must not break when you change it. It is written for two readers with zero
> prior context: a contributor fixing a bug or adding a feature, and an AI coding agent that has to
> orient itself quickly without damaging anything.
>
> **Provenance.** Synthesised from two full-source archaeology passes over `master` at commit
> `919a9f2` (assembly version `2.3.1.1`, `vibrance.GUI/Properties/AssemblyInfo.cs:35-36`), performed
> 2026-08-24, plus direct reading of the source. Statements about the prebuilt native
> `vibranceDLL.dll` come from parsing its PE headers and hand-decoding x86 at named RVAs; they are
> marked **VERIFIED (binary)**. Claims that could not be confirmed by execution are marked
> **INFERENCE** or **UNCERTAIN** and must not be repeated as fact.
>
> **Nothing here has been confirmed by running the program**, and there is no test suite to confirm it
> with (see [§3.7](#37-tests-and-ci)).

---

## Table of contents

1. [What this program actually does](#1-what-this-program-actually-does)
2. [Orientation: TL;DR for the impatient](#2-orientation-tldr-for-the-impatient)
3. [Tech stack, build & run](#3-tech-stack-build--run)
4. [Repository map](#4-repository-map)
5. [Architecture](#5-architecture)
6. [The runtime story, end to end](#6-the-runtime-story-end-to-end)
7. [The NVIDIA path](#7-the-nvidia-path)
8. [The AMD path](#8-the-amd-path)
9. [Settings & persistence](#9-settings--persistence)
10. [UI surface](#10-ui-surface)
11. [Data models reference](#11-data-models-reference)
12. [Known defects & risk register](#12-known-defects--risk-register)
13. [How to extend it](#13-how-to-extend-it)
14. [Open questions for maintainers](#14-open-questions-for-maintainers)

---

## 1. What this program actually does

### 1.1 The product, in plain language

vibranceGUI is a small Windows tray utility that **turns up your monitor's colour saturation while you
are playing a game, and turns it back down when you are not**.

It does this by driving the graphics driver's own saturation control — the same setting you would
otherwise change by hand in the NVIDIA Control Panel ("Digital Vibrance") or AMD's driver software
("Saturation"). Competitive players use a high vibrance level in-game because it makes enemy player
models stand out from the background, but they do not want their desktop, browser and video
permanently oversaturated. Doing it by hand on every launch and every alt-tab is tedious; this program
automates it.

The user's model of the app is:

1. Add one or more game executables to a watch list.
2. For each game, pick an **ingame level** — how saturated things should be while that game is in front.
3. Pick one global **Windows level** — what the desktop should look like the rest of the time.
4. Leave it running in the tray, optionally starting minimised with Windows.

From then on, every time the foreground window changes:

- if the new foreground window belongs to a watched game → the driver is set to that game's ingame
  level;
- if it belongs to anything else → the driver is set back to the Windows level.

Two extras ride along on the same mechanism:

- **An optional per-game resolution switch.** Each watched game can carry a target display mode; it is
  applied when the game takes the foreground and reverted when you leave. It exists for
  (borderless) windowed-mode players, and it is the most failure-prone feature in the program
  (see [§6.4](#64-the-optional-resolution-switch) and [§12](#12-known-defects--risk-register)).
- **An optional "affect primary monitor only"** mode, limiting changes to one display.

### 1.2 The two vendor scales the user sees

There is no vendor-neutral "saturation percentage" anywhere in the program. The number in the UI *is*
the number handed to the driver, and its meaning differs per vendor:

| | NVIDIA | AMD |
|---|---|---|
| Driver control | Digital Vibrance Control (DVC) via NvAPI | Saturation via ADL (`ADL_Display_Color_Set`) |
| Slider range | `0 … 63` | `0 … 300` |
| Neutral / "no boost" | `0` | `100` |
| Label beside the slider | `"50%" … "100%"`, matching the NVIDIA Control Panel | the raw number, no unit |
| Wired up at | `vibrance.GUI/Program.cs:63-69` | `vibrance.GUI/Program.cs:46-51` |

So on NVIDIA `0` means *no boost* and is displayed as `50%` — where the NVIDIA Control Panel's own
slider sits by default. On AMD `100` is neutral; the app lets you go below it (desaturate) but will
not persist that choice across restarts (see [§9.4](#94-value-clamping-on-load)).

### 1.3 What it is not, and what it will not do

- **It is not a colour-profile manager.** It writes a live driver setting and keeps no record of what
  it changed. If the process is killed, crashes, or the machine loses power while a game is in the
  foreground, **the display stays at the ingame level** — there is no "we changed this, restore it
  next time" record anywhere in the code.
- **No Intel support.** Vendor detection knows exactly two DLL names
  (`vibrance.GUI/common/GraphicsAdapter.cs:24-27`); anything else falls through to `Unknown` and the
  app shows an error and exits.
- **NVIDIA laptops** are declared unsupported by the README, but the code *no longer enforces this*:
  the laptop rejection message exists as a constant and is never shown
  (`vibrance.GUI/NVIDIA/NvidiaDynamicVibranceProxy.cs:125-127`, dead — see [§12](#12-known-defects--risk-register)).
- **It never asks for administrator rights.** There is no application manifest at all, so it runs
  `asInvoker`. One visible consequence: elevated games do not show up in the built-in process picker,
  because opening their process handle fails (`vibrance.GUI/common/ProcessExplorer.cs:76`).
- **It only reacts to foreground *changes*.** A game already in the foreground when vibranceGUI starts
  produces no event, so it gets no vibrance until you alt-tab away and back.

---

## 2. Orientation: TL;DR for the impatient

### 2.1 "I want to change X → look at Y"

| I want to change… | Go to |
|---|---|
| What happens when a game gains or loses focus (the heart of the app) | `vibrance.GUI/NVIDIA/NvidiaDynamicVibranceProxy.cs:209-269` and `vibrance.GUI/AMD/AmdDynamicVibranceProxy.cs:108-156` |
| How a foreground window is matched to a watched game | the `FirstOrDefault` at `NvidiaDynamicVibranceProxy.cs:213` / `AmdDynamicVibranceProxy.cs:112` — `ApplicationSetting.Name` vs `Process.ProcessName`, case-insensitive |
| How foreground changes are detected at all | `vibrance.GUI/common/WinEventHook.cs` — one system-wide `SetWinEventHook` on `EVENT_SYSTEM_FOREGROUND` |
| Which GPU vendor is chosen, and the "both drivers found" dialog | `vibrance.GUI/common/GraphicsAdapter.cs:30-51` |
| Slider ranges, defaults, level→label mapping | `vibrance.GUI/Program.cs:41-70` (five numbers per vendor) and `vibrance.GUI/common/SettingsController.cs:96-110` (a second, inconsistent copy) |
| Settings file format, defaults, clamping | `vibrance.GUI/common/SettingsController.cs` |
| Autostart with Windows | `vibrance.GUI/common/RegistryController.cs` + `vibrance.GUI/common/VibranceGUI.cs:258-289` |
| The main window (controls, workers, watched-app list) | `vibrance.GUI/common/VibranceGUI.cs` + `VibranceGUI.Designer.cs` |
| The per-game dialog (ingame level, resolution) | `vibrance.GUI/common/VibranceSettings.cs` |
| The running-process picker | `vibrance.GUI/common/ProcessExplorer.cs` |
| Resolution switching / `DispChangeBadFlags` errors | `vibrance.GUI/common/ResolutionHelper.cs:237-359` (`ChangeResolutionEx`) — see [§6.4](#64-the-optional-resolution-switch) |
| NVIDIA native calls (P/Invoke declarations) | `vibrance.GUI/NVIDIA/NvidiaDynamicVibranceProxy.cs:19-115` — the implementation is a **prebuilt binary from another repository** |
| AMD native calls | `vibrance.GUI/AMD/vendor/AmdAdapter32.cs` (and its clone `AmdAdapter64.cs`) plus `vibrance.GUI/AMD/vendor/adl32/`, `adl64/` |
| Startup wiring / which proxy gets built | `vibrance.GUI/Program.cs` — the only composition root |
| Adding a source file to the build | `vibrance.GUI/vibrance.GUI.csproj:95-152` — pre-SDK project, every file listed by hand |

### 2.2 Five facts that will bite you first

1. **There is no "apply vibrance" method in the abstraction.** `IVibranceProxy`
   (`vibrance.GUI/common/IVibranceProxy.cs:6-18`) contains only configuration *setters*. Every real
   driver write happens inside each proxy's private WinEvent handler. Nothing can ask a proxy to apply
   a level now, and the shell never tries.
2. **Everything runs on the UI thread — including the driver calls.** The hook is
   `WINEVENT_OUTOFCONTEXT`, so callbacks arrive through the message queue of the thread that installed
   it, which is the WinForms UI thread (`common/WinEventHook.cs:187-190`, pumped by `Program.cs:96`).
   NvAPI round-trips, ADL calls and `ChangeDisplaySettingsEx` all block the UI. **Historical note:**
   one path used to pop a modal message box from inside this very callback on a resolution-change
   failure (`common/ResolutionHelper.cs`, pre-`work/resolution-change`) — i.e. over the game that had
   just taken focus. `ResolutionHelper.cs` no longer has a `using System.Windows.Forms` or any
   `MessageBox` call site (the word itself still appears once, in a doc comment describing this very
   fact); see [§6.4](#64-the-optional-resolution-switch) and **D2**.
3. **The build is x86-only, and stays that way until someone rebuilds the native DLL.**
   `vibrance.GUI/NVIDIA/vibranceDLL.dll` is a PE32 i386 image (VERIFIED binary), and `PlatformTarget`
   is `x86` in all four configurations (`vibrance.GUI.csproj:33,44,65,74`).
4. **The NVIDIA native layer is not in this repository.** `vibranceDLL.dll` is a checked-in binary
   built 2017-01-02 from `juvlarN/vibranceDLL` (embedded PDB path, VERIFIED binary). Its 13 bound
   exports are the entire NVIDIA capability surface; adding one means rebuilding that other project.
5. **NVIDIA proxy state is `static`.** `_vibranceInfo`, `_applicationSettings`,
   `_windowsResolutionSettings`, `_gameScreen` and the hook handler are all static
   (`NvidiaDynamicVibranceProxy.cs:131-135,209`), making the class a de-facto singleton — a second
   instance silently clobbers the first. The AMD proxy is instance-scoped **except** `_gameScreen`
   (`AmdDynamicVibranceProxy.cs:21`).

Runner-up, because it wastes a lot of debugging time: **`SetVibranceIngameLevel` is a no-op.** Both
implementations write `VibranceInfo.userVibranceSettingActive`, and *nothing in the solution ever reads
that field*. The intended live preview while dragging the ingame slider does not work
(`NvidiaDynamicVibranceProxy.cs:336-339`, `AmdDynamicVibranceProxy.cs:77-80`).

### 2.3 Repo-state warning: the released v2.5.0 is **not** on `master`

Read this before basing work on `master` or trying to reproduce a user's bug report.

- The only tag in the repository is `v2.5.0` → commit `431d295` (2024-12-20). **It is not an ancestor
  of `master`.** It lives on branch `feature/add-color-settings`, which is open PR #140.
- `master` is at `919a9f2`, `AssemblyVersion 2.3.1.1`.
- `v2.5.0` is 7 commits ahead of `master`; `master` is 2 commits ahead of `v2.5.0`.
- Commits in `v2.5.0` and absent from `master`: `431d295` (force param for autostart path and window
  title), `62541a6` (colour-setting handling, assorted bug fixes), `6900bac` (default
  `neverSwitchResolution` to `true`), `438e15a` (gitignore), `a58b59f` (version bump to 2.5.0.0),
  `e8d5d6d` (text clarity), `968b1ab` (gamma handling).
- **Implication:** gamma/colour-settings support and the safer `neverSwitchResolution` default exist
  *only* on that unmerged branch. Building `master` gives 2.3.1.1 behaviour, which is not what users
  running the released binary have.
- Other branches: `Refactoring_to_WPF` 3 ahead / 171 behind (abandoned WPF rewrite),
  `Dynamic_VibranceGUI` 0 / 92 and `temp` 1 / 85 (stale).

---

## 3. Tech stack, build & run

### 3.1 What it is made of

| | |
|---|---|
| Language / runtime | C#, .NET Framework **4.0** (`vibrance.GUI.csproj:12`; `App.config` `supportedRuntime v4.0`) |
| UI | Windows Forms — three forms, all with designer files |
| Output | `WinExe`, assembly name `vibrance.GUI`, root namespace `vibrance.GUI` (`csproj:8-11`) |
| Platform target | **x86 in every configuration** (`csproj:33,44,65,74`); `Prefer32Bit=false` in the two AnyCPU groups only (`csproj:41,51`) |
| Project style | pre-SDK MSBuild, `ToolsVersion 4.0`, every source file listed explicitly (`csproj:95-152`) |
| Solution | `vibrance.GUI.sln`, format 12.00, "# Visual Studio 2012", one project |
| NuGet packages | none, as of v2.6.0 — `Fody`, `Costura.Fody`, and the unused `CommonServiceLocator` were all removed; the build requires no NuGet restore |
| Native dependencies | `nvapi.dll` (NVIDIA, resolved dynamically inside the prebuilt DLL); `atiadlxy.dll` / `atiadlxx.dll` (AMD, static `DllImport`); plus `user32`, `kernel32`, `psapi`, `advapi32` |
| Size | 64 tracked files in the repo; 58 items in the project; 48 `.cs` files, ~4,622 lines of C# |

### 3.2 Building

```bash
# from the repository root
msbuild vibrance.GUI.sln /p:Configuration=Release /p:Platform=x86
```

No NuGet restore step is needed: as of v2.6.0 the project references no NuGet packages at all, so
there is no `packages/` directory to populate and no restore-related build target to satisfy.

Because the project targets `v4.0`, you need a toolchain that can still target .NET Framework 4.0 (the
4.0 targeting / multi-targeting pack). Open PR #153 proposes moving to .NET 4.8.

**A C# 6 compiler is mandatory despite the 4.0 target framework.** The solution header says "Visual
Studio 2012", but the source uses interpolated strings (`Program.cs:56`, `VibranceGUI.cs:210`,
`VibranceSettings.cs:23`) and a get-only auto-property initialiser
(`NVIDIA/NvidiaDynamicVibranceProxy.cs:360` — `public GraphicsAdapter GraphicsAdapter { get; } = GraphicsAdapter.Nvidia;`).
Building with the VS 2012/2013 compiler fails: target framework and language version are independent.

Output paths by configuration (`csproj:37,47,62,70`):

| Configuration | Output path |
|---|---|
| `Debug` + `Any CPU` | `vibrance.GUI/bin/Debug/` |
| `Release` + `Any CPU` | remapped by the solution to `Release`+`x86` → `vibrance.GUI/bin/x86/Release/` |
| `Debug` + `x86` | `vibrance.GUI/bin/x86/Debug/` |
| `Release` + `x86` | `vibrance.GUI/bin/x86/Release/` |

The shipped artefact is a single `vibrance.GUI.exe`. There is no installer, no post-build step, and no
loose files to copy beside it.

### 3.3 The x86 rule, and why it is not negotiable

The README says only *"When compiling, make sure to compile for x86 target platform."* The reasons are
concrete:

1. **`vibrance.GUI/NVIDIA/vibranceDLL.dll` is a 32-bit PE32 i386 image** (VERIFIED binary: machine
   `0x14c`, 163,840 bytes, link timestamp 2017-01-02 18:22:43 UTC, SHA-256
   `0f229f79934f21617337c28915a9449f7b2395b20d7fb0e4a02d14277163cea0`). A 64-bit process cannot load
   it, and no 64-bit build of it exists in this repository.
2. **Vendor detection probes the 32-bit system directory.** `GraphicsAdapterHelper.GetAdapter()` looks
   in `Environment.SpecialFolder.SystemX86` (`common/GraphicsAdapter.cs:32`) — `C:\Windows\SysWOW64`
   on 64-bit Windows — which is where the *32-bit* `nvapi.dll` and `atiadlxy.dll` live.
3. **The AMD binding assumes a 32-bit caller.** A 32-bit process on 64-bit Windows must load
   `atiadlxy.dll` rather than `atiadlxx.dll`; that assumption is baked into the `adl32`/`adl64` split
   (see [§8.4](#84-the-adl32adl64-duplication-and-why-the-names-are-backwards)).
4. **The code knows it is 32-bit and works around it.** `ProcessExplorer.GetPathFromProcessId` uses
   `psapi!GetModuleFileNameEx` with the comment *"Process.MainModule.FileName crashes when called on a
   x64 process because vibranceGUI is running as x86 process."* (`common/ProcessExplorer.cs:67-72`).

`IntPtr` being 4 bytes is also load-bearing for some P/Invoke signatures — e.g. `getGpuSystemType` is
declared taking an `int` in C# where the native side takes an `int*`
(see [§7.5](#75-the-__thiscall-as-stdcall-binding--the-single-biggest-contributor-hazard)).

### 3.4 The `Debug|Any CPU` trap — with a correction

The solution remaps `Release|Any CPU` to the project's `Release|x86` (`vibrance.GUI.sln:18-19`) but
**does not remap `Debug|Any CPU`**, which stays `Debug|Any CPU` (`vibrance.GUI.sln:14-15`). Pressing
F5 in Visual Studio with the default "Any CPU" solution configuration therefore builds a *different
project configuration* than a Release build does.

**Correction to a claim you will hear about this repo:** this does **not** produce a 64-bit process.
The `Debug|AnyCPU` property group explicitly sets `<PlatformTarget>x86</PlatformTarget>`
(`vibrance.GUI.csproj:33`, with `Prefer32Bit=false` at `:41`), so the executable is still 32-bit and
can still load `vibranceDLL.dll`. What actually differs:

- the output directory is `bin/Debug/` instead of `bin/x86/Debug/`, so the binary you just built is
  not where you expect it and a stale binary in the other folder is easy to run by mistake;
- `Debug|Any CPU` does not set `CodeAnalysisRuleSet`, which both `x86` configurations do
  (`csproj:67,76`).

The practical advice is unchanged — **select the `x86` solution platform** — but when you are chasing a
"it won't load the native DLL" report, do not assume bitness is the cause on this branch; verify it.

### 3.5 How the native NVIDIA DLL is deployed (not what you would guess)

As of v2.6.0 the project no longer uses Costura.Fody. It previously merged *managed* references into
the output assembly, configured by an empty `<Costura/>` element in `vibrance.GUI/FodyWeavers.xml`;
given the reference list the only copy-local managed reference it ever merged was
`Microsoft.Practices.ServiceLocation.dll`, from the unused `CommonServiceLocator` package. That package
was removed in the same release, so Costura had nothing left to do and was dropped alongside it — the
build now references no NuGet packages at all (§3.2).

**The native DLL was never Costura's job.** `vibranceDLL.dll` is deployed via plain MSBuild plus
hand-written extraction, untouched by the Costura removal:

- `vibrance.GUI.csproj:189` — `<EmbeddedResource Include="NVIDIA\vibranceDLL.dll" />`, giving the
  manifest resource name `vibrance.GUI.NVIDIA.vibranceDLL.dll`;
- `Program.cs:55-60` reconstructs exactly that name and calls
  `CommonUtils.LoadUnmanagedLibraryFromResource(...)`;
- `AMD/vendor/utils/CommonUtils.cs:20-36` reads the resource, **writes it to
  `%APPDATA%\vibranceGUI\vibranceDLL.dll`, overwriting on every launch**, and calls
  `kernel32!LoadLibrary("vibranceDLL.dll")`, which resolves through the directory registered with
  `SetDllDirectory` (`Program.cs:36`, and again in the `NativeMethods` static constructor,
  `AMD/vendor/utils/NativeMethods.cs:8-11` — so the call is made twice).

Two consequences. The extraction helper lives in the **AMD** utils namespace but is used only by the
NVIDIA path — a misfiled utility, not a behavioural bug. And `File.WriteAllBytes` on a locked file
throws `IOException`, while neither the extraction nor the following
`Marshal.PrelinkAll(typeof(NvidiaDynamicVibranceProxy))` (`Program.cs:61`) sits in a `try`/`catch` — so
a locked or mismatched DLL is an unhandled exception out of `Main`, not a friendly error.

### 3.6 Running it

`vibrance.GUI.exe` recognises exactly one argument:

| Argument | Effect |
|---|---|
| `-minimized` | start minimised and hidden (`Program.cs:90-94`). Matched with `args.Contains("-minimized")` — ordinal, case-sensitive, whole token. This is the flag the autostart registry entry uses. |

Anything else is ignored; command-line options are open feature request #120. Only one instance may run
per session, enforced with a `Mutex` named `vibranceGUI~Mutex` (`Program.cs:27`); the absence of a
`Global\` prefix makes it session-local, so two logged-in users can each run one.

### 3.7 Tests and CI

- **There is no test project**, but there are automated checks: 412 of them across nine
  `*Fixture.cs` files in `vibrance.GUI/common/`, compiled into the app and run through
  `--selftest-*` flags dispatched early in `Program.cs`. They report through `Checklist`
  (PASS/FAIL/SKIP), not a third-party assertion library, so searching for `Assert.` or
  `*Test*` finds nothing and wrongly suggests the project is untested.
- **CI is dead.** `.travis.yml` targets travis-ci.org (shut down) with `dist: trusty`, `mono: beta`,
  `dotnet: 1.0.3`. There is no GitHub Actions workflow. Assume nothing is verified on push.
- **`.gitattributes` sets `*.sln merge=union` and `*.csproj merge=union`.** Union merge on project
  files concatenates both sides of a conflict, which can silently produce duplicated or invalid XML.
  If you touch the csproj on a branch, inspect it after any merge.

---

## 4. Repository map

```
vibranceGUI/
├── README.md                      product blurb, the "compile for x86" warning, support contacts
├── .travis.yml                    dead CI (travis-ci.org, mono beta) — see §3.7
├── .gitattributes                 *.sln / *.csproj merge=union — see §3.7
├── vibrance.GUI.sln               one project; Debug|Any CPU is NOT remapped to x86 (§3.4)
└── vibrance.GUI/
    ├── Program.cs                 ENTRY POINT and the only composition root (§5.3)
    ├── App.config                 supportedRuntime v4.0
    ├── vibrance.GUI.csproj        pre-SDK project; add new files here by hand
    ├── setting.ico                application icon
    │
    ├── common/                    the app shell — vendor-agnostic
    │   ├── VibranceGUI.cs             main form + de-facto orchestrator (556 lines) (§6, §10.1)
    │   ├── VibranceGUI.Designer.cs    control layout (German designer comments)
    │   ├── VibranceSettings.cs        per-game modal dialog (§10.2)
    │   ├── VibranceSettings.Designer.cs
    │   ├── ProcessExplorer.cs         running-process picker (§10.3)
    │   ├── ProcessExplorer.Designer.cs
    │   ├── ProcessExplorerEntry.cs    DTO: path + icon + process name
    │   ├── IVibranceProxy.cs          THE vendor seam — 10 members, no apply method (§5.2)
    │   ├── ISettingsController.cs     internal; SetVibranceSetting() is never called
    │   ├── IRegistryController.cs     internal; incomplete — see §5.4
    │   ├── SettingsController.cs      INI + XML persistence, defaults and clamping (§9)
    │   ├── RegistryController.cs      HKCU\...\Run autostart (§9.5)
    │   ├── GraphicsAdapter.cs         vendor enum + detection (§6.1)
    │   ├── Definitions.cs             the VibranceInfo struct (§11.4)
    │   ├── ApplicationSetting.cs      one watched game (§11.1)
    │   ├── ResolutionHelper.cs        DEVMODE / ChangeDisplaySettingsEx (§6.4)
    │   ├── ResolutionModeWrapper.cs   serialisable display mode (§11.5)
    │   ├── WinEventHook.cs            THE foreground detector (§6.2)
    │   └── WinEventHookEventArgs.cs   event payload; 4 of its 6 fields are dead
    │
    ├── NVIDIA/                    NVIDIA vendor path (§7)
    │   ├── NvidiaDynamicVibranceProxy.cs   IVibranceProxy impl + 13 P/Invokes into vibranceDLL
    │   ├── NvidiaTypes.cs                  NV_DISPLAY_DVC_INFO, NvApiStatus (dead), NvSystemType
    │   ├── NvidiaVibranceValueWrapper.cs   raw DVC level → "50%".."100%" label map
    │   └── vibranceDLL.dll                 PREBUILT NATIVE BINARY, source is in another repo (§7.2)
    │
    ├── AMD/                       AMD vendor path (§8)
    │   ├── AmdDynamicVibranceProxy.cs      IVibranceProxy impl
    │   └── vendor/
    │       ├── IAmdAdapter.cs              : IDisposable — but Dispose is never called (§8.6)
    │       ├── AmdAdapter32.cs             ADL enumeration + saturation writes
    │       ├── AmdAdapter64.cs             CLONE of AmdAdapter32: 195 of 197 lines identical
    │       ├── adl32/  (8 files)           ADL bindings → atiadlxx.dll
    │       ├── adl64/  (8 files)           CLONE of adl32 → atiadlxy.dll — names are backwards (§8.4)
    │       └── utils/
    │           ├── CommonUtils.cs          %APPDATA% path + the NVIDIA DLL extraction (misfiled)
    │           └── NativeMethods.cs        LoadLibrary / SetDllDirectory
    │
    └── Properties/                AssemblyInfo (version 2.3.1.1), Resources, Settings
```

Three structural facts worth internalising before you edit anything:

- **`common/` is not vendor-neutral.** `IVibranceProxy.cs:2` still has `using vibrance.GUI.NVIDIA;`
  (vestigial), `SettingsController.cs:9` imports the NVIDIA namespace to read `NvapiDefaultLevel` /
  `NvapiMaxLevel`, and `VibranceInfo` carries NVIDIA-shaped fields the AMD path never fills in.
- **`AMD/vendor/utils/` is not AMD-specific.** It hosts the `%APPDATA%` helper and the NVIDIA native
  DLL extraction used by `Program.cs`.
- **`adl32/` and `adl64/` are near-identical clones** carrying one string of difference. Any ADL fix
  must be applied twice ([§8.4](#84-the-adl32adl64-duplication-and-why-the-names-are-backwards)).

---

## 5. Architecture

### 5.1 The layering

```mermaid
graph TD
    subgraph shell["App shell (vendor-agnostic)"]
        P["Program.cs<br/>composition root"]
        VG["VibranceGUI.cs<br/>main form + orchestrator"]
        VS["VibranceSettings.cs<br/>per-game dialog"]
        PE["ProcessExplorer.cs<br/>process picker"]
    end

    subgraph common["common/ services"]
        WEH["WinEventHook<br/>EVENT_SYSTEM_FOREGROUND"]
        SC["SettingsController<br/>INI + XML"]
        RC["RegistryController<br/>HKCU Run key"]
        RH["ResolutionHelper<br/>ChangeDisplaySettingsEx"]
        GA["GraphicsAdapterHelper<br/>vendor detection"]
    end

    IVP{{"IVibranceProxy<br/>10 members, setters only"}}

    subgraph vendor["Vendor proxies"]
        NV["NvidiaDynamicVibranceProxy<br/>static state + static handler"]
        AMD["AmdDynamicVibranceProxy<br/>instance state"]
    end

    subgraph native["Native / driver"]
        DLL["vibranceDLL.dll<br/>PE32 i386, prebuilt 2017"]
        NVAPI["nvapi.dll<br/>Digital Vibrance"]
        ADLA["AmdAdapter32 / AmdAdapter64"]
        ADL["atiadlxx.dll / atiadlxy.dll<br/>ADL_Display_Color_Set"]
    end

    P -->|"picks vendor"| GA
    P -->|"constructs"| VG
    VG -->|"owns"| IVP
    VG --> SC
    VG --> RC
    VG --> VS
    VG --> PE
    IVP -.implemented by.-> NV
    IVP -.implemented by.-> AMD
    NV --> DLL --> NVAPI
    AMD --> ADLA --> ADL
    WEH -->|"WinEventHookHandler"| NV
    WEH -->|"WinEventHookHandler"| AMD
    NV --> RH
    AMD --> RH
```

Read the diagram with one correction to your instincts: **the arrow that matters most points from
`WinEventHook` *into* the proxies**, not from the shell. The shell configures a proxy and then has no
further say; the proxy drives the driver from an event handler the shell never sees.

### 5.2 The `IVibranceProxy` seam

`vibrance.GUI/common/IVibranceProxy.cs:6-18` — ten members, no `IDisposable`, no events, no async:

| Member | Called from | Contract |
|---|---|---|
| `SetApplicationSettings(List<ApplicationSetting>)` | `VibranceGUI.cs:145` (worker thread, once after load) | **Store the reference, do not copy.** The shell keeps mutating that same list on every add/remove (`VibranceGUI.cs:383,517-519,537`). The call exists because `SettingsController.ReadVibranceSettings` returns a *new* list through an `out` parameter, orphaning the one the constructor was given (`VibranceGUI.cs:73-74`). |
| `SetShouldRun(bool)` | `VibranceGUI.cs:146`, `:324` | **Vestigial.** Both implementations only assign `VibranceInfo.shouldRun`, which nothing reads. Fossil of the pre-2015 polling design ([§6.6](#66-why-hooks-and-not-polling--reading-the-fossils)). |
| `SetVibranceWindowsLevel(int)` | `VibranceGUI.cs:147`, `:173` | The desktop level. Stored, **not applied immediately** — dragging the desktop slider changes nothing visible until the next foreground change. |
| `SetVibranceIngameLevel(int)` | `VibranceSettings.cs:40` | **Effectively a no-op** (see [§2.2](#22-five-facts-that-will-bite-you-first)). |
| `UnloadLibraryEx()` | `VibranceGUI.cs:325` | Tear down the hook and release native resources. Return value is **ignored** by the caller. |
| `HandleDvcExit()` | `VibranceGUI.cs:323` | Restore the Windows level on the affected displays before exit. |
| `SetAffectPrimaryMonitorOnly(bool)` | `VibranceGUI.cs:148`, `:237` | Whether the revert path touches all displays or one. |
| `GetVibranceInfo()` | `VibranceGUI.cs:139,155,321` | Returns the `VibranceInfo` **struct by value** — a copy. The shell consumes only `isInitialized`, which is the single "did the vendor layer come up?" signal that gates the entire UI. |
| `GraphicsAdapter { get; }` | `VibranceGUI.cs:369` | Vendor tag, used to pick value ranges when loading settings. |
| `SetNeverSwitchResolution(bool)` | `VibranceGUI.cs:149`, `:251` | Global kill-switch for the resolution feature. |

**The interface is not the real contract.** Three things escape it:

1. **Construction.** The constructors differ in shape —
   `NvidiaDynamicVibranceProxy(settings, resolutions)` (`NvidiaDynamicVibranceProxy.cs:137`) vs
   `AmdDynamicVibranceProxy(IAmdAdapter, settings, resolutions)` (`AmdDynamicVibranceProxy.cs:23`) —
   and `Program.cs:43-45,63-64` papers over the difference with two different lambdas.
2. **Value semantics.** The five numbers that define a vendor's scale (default Windows level, slider
   min, slider max, default ingame value, and the level→label function) are passed to the *form*, not
   to the proxy, and exist nowhere in the interface (`Program.cs:46-51`, `:63-69`).
3. **Behaviour on focus change.** Not on the interface at all; it is a private event handler in each
   implementation, and the two differ substantially ([§6.3](#63-vendor-divergence-in-the-same-flow)).

Rules a new implementation must honour: **never throw out of the constructor** (the shell has no
`try`/`catch` around `getProxy`); set `VibranceInfo.isInitialized` truthfully, because that flag alone
enables the GUI; subscribe to `WinEventHook.GetInstance().WinEventHookHandler` only when initialisation
actually succeeded; and tolerate being called from both the UI thread and the `backgroundWorker` thread
with no locking anywhere.

### 5.3 The composition root: `Program.cs`

`Program.Main` (`Program.cs:23-99`) is the only place that knows which vendor exists. It builds the
*same* `VibranceGUI` form in both branches, parameterised by a proxy factory and the vendor's value
scale:

| Constructor argument (`VibranceGUI.cs:34-40`) | AMD (`Program.cs:43-51`) | NVIDIA (`Program.cs:63-69`) |
|---|---|---|
| `getProxy` | `new AmdDynamicVibranceProxy(Is64BitOperatingSystem ? AmdAdapter64 : AmdAdapter32, x, y)` | `new NvidiaDynamicVibranceProxy(x, y)` |
| `defaultWindowsLevel` | `100` | `NvapiDefaultLevel` = `0` |
| `minTrackBarValue` | `0` | `0` |
| `maxTrackBarValue` | `300` | `NvapiMaxLevel` = `63` |
| `defaultIngameValue` | `100` | `0` |
| `resolveLabelLevel` | `x => x.ToString()` | `x => NvidiaVibranceValueWrapper.Find(x).Percentage` |

The AMD numbers `100/0/300/100` are **hardcoded literals in `Program.cs`**, and they are duplicated —
inconsistently — in `SettingsController.cs:105-110`, which clamps loaded values to `100..300` while the
slider allows `0..300` ([§9.4](#94-value-clamping-on-load)).

### 5.4 Where the abstractions leak

- `IVibranceProxy` is `public`; `ISettingsController` and `IRegistryController` are `internal`, and
  both are instantiated concretely everywhere they are used
  (`VibranceGUI.cs:368,404`, `:260`, `:365`) — so neither interface buys anything today.
- **`IRegistryController` is incomplete.** The UI calls `IsStartupPathUnchanged(appName, pathToExe)`
  (`VibranceGUI.cs:270`), which exists only on the concrete `RegistryController`
  (`RegistryController.cs:70-95`) and is *not* on the interface (`IRegistryController.cs:3-8`). The
  field is declared as the interface (`VibranceGUI.cs:24`) yet a concrete instance is created locally
  to make that call (`VibranceGUI.cs:260`). Any alternative implementation would be unusable.
- `ISettingsController.SetVibranceSetting(szKeyName, value)` is **never called** — only declared and
  implemented.
- Both `SetVibranceSettings` and `SetVibranceSetting` return `bool`, and the returns are meaningless as
  implemented (`SettingsController.cs:66`, `:78` — `Marshal.GetLastWin32Error() == 0` after a full XML
  serialisation, with neither P/Invoke declaring `SetLastError = true`). Every caller ignores them.

---

## 6. The runtime story, end to end

This is the section to read if you read only one. Everything the program does is one of three things:
start up, react to a foreground change, or shut down.

### 6.1 Startup

```mermaid
sequenceDiagram
    participant M as Program.Main
    participant GA as GraphicsAdapterHelper
    participant F as VibranceGUI (form)
    participant PX as Vendor proxy
    participant H as WinEventHook
    participant W as backgroundWorker

    M->>M: Mutex "vibranceGUI~Mutex" — bail out if taken
    M->>M: SetDllDirectory(%APPDATA%\vibranceGUI)
    M->>GA: GetAdapter()
    GA-->>M: Nvidia | Amd | Ambiguous | Unknown
    Note over M: Ambiguous/Unknown -> dialog, then return (no window at all)
    M->>M: NVIDIA only: extract vibranceDLL.dll, PrelinkAll
    M->>F: new VibranceGUI(getProxy, 5 vendor numbers)
    F->>F: enumerate Screen.AllScreens, capture current+supported modes
    F->>PX: getProxy(_applicationSettings, _windowsResolutionSettings)
    PX->>PX: init driver library — NVIDIA also writes DVC level 0 to the primary display
    PX->>H: GetInstance() + subscribe — only if isInitialized
    H->>H: SetWinEventHook(EVENT_SYSTEM_FOREGROUND, OUTOFCONTEXT)
    F->>W: RunWorkerAsync()
    M->>M: -minimized? WindowState=Minimized, SetAllowVisible(false)
    M->>M: Text += " (NVIDIA, 2.3.1.1)"
    M->>F: Application.Run(form)  — this thread is now the message pump
    W->>W: spin on Thread.Sleep(500) until the form handle exists
    W->>F: Invoke(ReadVibranceSettings) — load INI + XML, populate the UI
    W->>PX: SetApplicationSettings / SetShouldRun / SetVibranceWindowsLevel /<br/>SetAffectPrimaryMonitorOnly / SetNeverSwitchResolution
    W->>F: status label "Running!" (green), enable controls
```

Step by step, with the details that matter:

1. **Single instance** (`Program.cs:26-32`). `new Mutex(true, "vibranceGUI~Mutex", out result)`; if the
   mutex was already held, show *"You can run vibranceGUI only once at a time!"* and return. Note this
   happens *before* `Application.EnableVisualStyles()` (`:34`), so that first box is drawn unthemed.
2. **DLL search path** (`Program.cs:36`). `SetDllDirectory(CommonUtils.GetVibrance_GUI_AppDataPath())`.
   That helper (`AMD/vendor/utils/CommonUtils.cs:9-18`) returns `%APPDATA%\vibranceGUI` **and creates
   the directory if it is missing** — the only place that is guaranteed to happen, which
   `SettingsController` silently depends on ([§9.3](#93-write-and-read-paths)).
3. **Vendor detection** (`Program.cs:38` → `common/GraphicsAdapter.cs:30-51`):
   - if **both** the AMD DLL and `nvapi.dll` exist in `SysWOW64` → `Ambiguous`;
   - else if `LoadLibrary(amdDll)` succeeds **and** `IAmdAdapter.IsAvailable()` → `Amd`;
   - else if `LoadLibrary("nvapi.dll")` succeeds → `Nvidia`;
   - else `Unknown`.
   The AMD file name is chosen at static-init by *OS* bitness: `atiadlxy.dll` on 64-bit Windows,
   `atiadlxx.dll` on 32-bit (`GraphicsAdapter.cs:25-27`). None of the `LoadLibrary` handles is ever
   freed.
4. **Error branches quit the process.** `Unknown` (`Program.cs:71-80`) shows the "failed to determine
   your graphics adapter" text plus `new Win32Exception(Marshal.GetLastWin32Error()).Message`, and
   "Yes" opens `https://x.com/swatx18`. `Ambiguous` (`:81-89`) shows the "uninstall your old
   drivers with DDU" text, and "Yes" opens the Guru3D DDU download page. **Both branches `return`
   after opening the browser** — "Yes" also quits. No window is ever shown.
5. **NVIDIA native bootstrap** (`Program.cs:55-61`): extract the embedded DLL
   ([§3.5](#35-how-the-native-nvidia-dll-is-deployed-not-what-you-would-guess)), then
   `Marshal.PrelinkAll(typeof(NvidiaDynamicVibranceProxy))` to force every `DllImport` in that type to
   resolve immediately — so a missing `nvapi.dll` or an entry-point mismatch fails loudly at startup
   rather than mysteriously later. Neither call is guarded.
6. **The form constructor** (`VibranceGUI.cs:34-80`) does far more than lay out controls: it overrides
   the designer's trackbar range with the vendor's (`:51-52`), enumerates every `Screen.AllScreens`
   capturing the current mode and the full supported-mode list per device (`:54-72`), **builds the
   proxy** (`:74`), and kicks off the loading worker (`:79`). If `GetCurrentResolutionSettings` fails
   for a monitor, a modal box appears before the window is ever visible (`:70`).
   Note `_supportedResolutionList` is assigned **only for the primary screen** (`:61-64`) and is the
   only list the per-game dialog ever offers ([§12](#12-known-defects--risk-register)).
7. **The proxy constructor is where the driver comes up.** Both proxies subscribe to the hook only if
   initialisation succeeded; both catch every exception, show a dialog, and then **return a live but
   non-functional object** (`NvidiaDynamicVibranceProxy.cs:155-164`, `AmdDynamicVibranceProxy.cs:45-54`).
8. **The startup worker** (`VibranceGUI.cs:116-151`) busy-waits `Thread.Sleep(500)` until
   `IsHandleCreated`, then marshals `ReadVibranceSettings` onto the UI thread, and — **only if
   `GetVibranceInfo().isInitialized`** — reports progress (status "Running!", green), enables the
   controls, and pushes the loaded configuration into the proxy. The busy-wait is why
   `SetVisibleCore` force-creates the handle when started `-minimized` (`VibranceGUI.cs:82-93`);
   without it a hidden form would never create a handle and the worker would spin forever.

### 6.2 The foreground-change flow — the heart of the app

Every vibrance change in the program originates here.

```mermaid
flowchart TD
    A["User switches window<br/>Windows raises EVENT_SYSTEM_FOREGROUND"] --> B["WinEventHook.WinEventProc<br/>common/WinEventHook.cs:219-250<br/>runs on the UI thread"]
    B --> C["GetWindowThreadProcessId + Process.GetProcessById"]
    C -->|"process already exited<br/>InvalidOperationException / ArgumentException"| X["swallowed — NO event dispatched"]
    C --> D["raise WinEventHookHandler<br/>with ProcessName + hwnd"]
    D --> E{"_applicationSettings.Count &gt; 0 ?"}
    E -->|no| Y["do nothing at all<br/>(never reverts — see §12)"]
    E -->|yes| F{"any ApplicationSetting.Name<br/>== e.ProcessName, case-insensitive?"}

    F -->|"match — APPLY"| G{"resolution change<br/>configured and allowed?"}
    G -->|yes| H["ChangeDisplaySettingsEx to the game mode"]
    G -->|no| J
    H --> J["remember _gameScreen<br/>write ingame level to the driver"]

    F -->|"no match — REVERT"| K{"is this hwnd still<br/>the foreground window?"}
    K -->|no| Z["return — stale event"]
    K -->|yes| L{"a game resolution<br/>is still applied?"}
    L -->|yes| M["ChangeDisplaySettingsEx back to<br/>the mode captured at app start"]
    L -->|no| O
    M --> O["write the Windows level<br/>to all displays, or just one if<br/>affectPrimaryMonitorOnly"]
```

The mechanism underneath:

- **One hook, system-wide.** `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
  IntPtr.Zero, _procDelegate, 0, 0, WINEVENT_OUTOFCONTEXT)` — `common/WinEventHook.cs:187-190`.
  `idProcess`/`idThread` are `0`, so it observes all processes. `WINEVENT_SKIPOWNPROCESS` is *not*
  set, so vibranceGUI's own windows also raise callbacks (harmless: `vibrance.GUI` matches no game and
  the revert branch is idempotent on NVIDIA).
- **The delegate is rooted** in an instance field (`WinEventHook.cs:183`) and the instance in a static
  (`:181`), so the classic "GC collected my callback" crash is avoided — by luck rather than by a
  `GCHandle`.
- **The match rule** is one line in each proxy (`NvidiaDynamicVibranceProxy.cs:213`,
  `AmdDynamicVibranceProxy.cs:112`):

  ```csharp
  ApplicationSetting applicationSetting = _applicationSettings.FirstOrDefault(
      x => string.Equals(x.Name, e.ProcessName, StringComparison.OrdinalIgnoreCase));
  ```

  A watched game is "in front" iff the process owning the new foreground window has a `ProcessName`
  (exe name without extension) that case-insensitively equals a stored `ApplicationSetting.Name`.
  **The path is ignored.** `ApplicationSetting.FileName` is used for de-duplication, icon extraction
  and pruning, never for matching — so *any* process named `csgo` triggers the `csgo` profile,
  wherever it lives.
- **The whole handler is wrapped in `if (_applicationSettings.Count > 0)`**
  (`NvidiaDynamicVibranceProxy.cs:211`, `AmdDynamicVibranceProxy.cs:110`) — including the revert
  branch. Delete your last watched application while a game is ingame and vibrance is never restored.
- **`WinEventHookEventArgs` is mostly dead.** `MainWindowTitle`, `WindowText` and `Process`
  (`common/WinEventHookEventArgs.cs:9-12`) are assigned (or in `Process`'s case not even that) and
  never read. The `Process.GetProcessById` round-trip exists solely to obtain `ProcessName`.

### 6.3 Vendor divergence in the same flow

The two handlers implement noticeably different behaviour. This table is the most useful thing to have
open when you are debugging a vendor-specific report:

| Aspect | NVIDIA (`NvidiaDynamicVibranceProxy.cs:209-269`) | AMD (`AmdDynamicVibranceProxy.cs:108-156`) |
|---|---|---|
| "Is this really the foreground?" | native `isWindowActive(ref hwnd)` (`:239`) — the DLL compares against `GetForegroundWindow()` (VERIFIED binary) | inline `GetForegroundWindow() != processHandle` (`:140`), own P/Invoke at `:105-106` |
| Which display gets the game level | per-window, via `getAssociatedNvidiaDisplayHandle(screen.DeviceName)` (`:216`, `:299-315`) | `Screen.FromHandle(...).DeviceName` matched against the ADL adapter's `DisplayName` (`AmdAdapter32.cs:133`) |
| Redundant-write suppression | **yes** — `equalsDVCLevel(...)` gate at `:218,254,263`, i.e. a driver *read* before every write | **none** — every foreground change writes saturation |
| Order of operations on game focus | resolution change → set DVC on that display | **first resets all displays to the Windows level** (`:127`), then applies the game level (`:130`/`:134`) — a visible double-write |
| `affectPrimaryMonitorOnly` on focus loss | honoured, with an extra bail-out if the new window is on a different screen than `_gameScreen` (`:254-266`) | **not honoured** — always `SetSaturationOnAllDisplays` (`:153`) |
| `_gameScreen` assignment | assigned outside the resolution-change branch, but still inside the `displayHandle != -1 && !equalsDVCLevel(...)` gate (`:218`; assignment at `:230`) | **only inside the resolution-change `if`** (`:123`) — so on AMD `_gameScreen` stays `null` unless a resolution change fired, which in turn disables AMD's own restore-resolution branch at `:146` |
| On exit (`HandleDvcExit`) | honours `affectPrimaryMonitorOnly` (`:368-376`) | ignores it, rewrites every display (`:88-91`) |

### 6.4 The optional resolution switch

**This section describes the post-fix design (branch `work/resolution-change`).** The two-phase
`CDS_UPDATEREGISTRY|CDS_NORESET` pattern this subsection used to describe, `ChangeResolution` (a
second, dead implementation of the same pattern) and the per-proxy `IsResolutionChangeNeeded`/
`PerformResolutionChange` duplication are all **gone**. The root `-4` (`DISP_CHANGE_BADFLAGS`) some
users hit could never be confirmed from this repository — it originates inside `user32` — so the fix
does not chase it; instead it closes every defect *around* the failure, which is where the real user
harm (a permanently blocked WinEvent thread, a stranded desktop resolution, an infinite retry loop)
actually lived. See `T0-CONTRACTS.md`-style history in the git log of this branch for the four
defects fixed together.

**The seam.** `common/ResolutionHelper.cs:18-28` defines `internal interface IDisplayModeDevice`
(`TryGetCurrentMode`, `TryEnumerateMode`, `ChangeMode` — `Devmode` passed **by value**, not `ref`, so
a fake can record exactly what it was handed). `RealDisplayModeDevice` (`:501-522`) is the only
production implementation, calling `EnumDisplaySettings`/`ChangeDisplaySettingsEx` directly.
`common/ResolutionChangeFixture.cs` drives the internal overloads against its own
`FakeDisplayModeDevice`, the same pattern `DeviceGammaRampHelper`/`IGammaDevice`/
`GammaRestoreFixture` already established for the gamma ramp.

**The call.** Both proxies now call two public members directly instead of their own duplicated
helpers: `ResolutionHelper.IsResolutionChangeNeeded(deviceName, target)` (`:177-180`) as a guard, then
`ResolutionHelper.ChangeResolutionEx(target, deviceName, isRevert)` (`:219-222`) to act
(`NvidiaDynamicVibranceProxy.cs:241-254,289-307`; `AmdDynamicVibranceProxy.cs:173-186,209-223`). The
`isRevert` flag selects which of two different give-up bounds applies (below) and which wording a
failure notification uses.

**The sequence** (internal overload, `common/ResolutionHelper.cs:237-359`):

```
1. TryGetCurrentMode  -> unreadable: Failed, logged once, no notification, not counted toward give-up
2. target.MatchesAchievedMode(current)? -> AlreadyMatching, clear this device's failure state, done
3. copy current -> desired; overwrite width/height/bpp/frequency/fixedOutput; OR (not overwrite)
   OwnedFields into dmFields, so DM_POSITION (and anything else EnumDisplaySettings already set)
   survives untouched
4. ChangeMode(CDS_TEST)
     rejected AND DM_DISPLAYFIXEDOUTPUT was declared AND its value actually differs from the
     device's own current value -> drop that one bit, restore the device's own current value,
                                    retry ONCE (skipped when the value already matched - dropping
                                    the bit would change nothing observable, so it is never worth a
                                    second driver call)
     still rejected -> Failed (step 7), CDS_UPDATEREGISTRY is never reached
5. ChangeMode(CDS_UPDATEREGISTRY)
     Successful -> continue; Notupdated -> logged once, continue (mode is live regardless);
     anything else -> Failed (step 7)
6. TryGetCurrentMode again; must match target on the four fields, or -> AppliedUnverified (step 7) -
   a DISTINCT result from Failed, because CDS_UPDATEREGISTRY itself already reported success here;
   the mode most likely DID change, so the proxies keep treating it as applied rather than
   discarding a change that plausibly landed
7. Failed/AppliedUnverified: bump a per-(device,target,direction) consecutive-failure counter; log
   once per (device, dedup key) - the real DispChange code for a step 4/5 rejection, a fixed
   "readback-mismatch" key (with the achieved-vs-target values in the message, not a synthetic
   DispChange code) for step 6; once the counter reaches the direction's bound, raise
   ResolutionChangeFailed exactly once and every further call for that exact key returns Suppressed
   **without touching the driver at all**, until a success on a DIFFERENT target/direction for the
   same device clears it (see ClearFailureState - once a key is itself suppressed, the driver is
   never called for it again through this path, so it cannot produce a success of its own)
```

`CDS_TEST` then `CDS_UPDATEREGISTRY` replaces the old stage-then-commit pattern: `CDS_UPDATEREGISTRY`
alone both applies and persists in one authoritative call, so there is no longer a reachable state
where the registry holds a mode nothing ever confirmed was applied, and `CDS_TEST` catches a mode the
driver would reject before anything is written — which matters because `CDS_UPDATEREGISTRY` gets no
15-second revert-if-unconfirmed safety net the way an interactive Windows Settings change does.
`CDS_NORESET` is never passed at all (asserted by `ResolutionChangeFixture` check 1).

**No `using System.Windows.Forms` and no `MessageBox` call site in `ResolutionHelper.cs`.** A give-up
is reported through `ResolutionHelper.ResolutionChangeFailed` (`:117`), which `VibranceGUI`'s
constructor subscribes (`VibranceGUI.cs:94`) and turns into a `notifyIcon` balloon tip
(`OnResolutionChangeFailed`, `VibranceGUI.cs:557-580`) — non-modal, and, because the raise is
deferred to the give-up attempt rather than fired on every failure, at most one balloon per
(device, target) streak. There is deliberately no "IsGivingUp" flag on the event args: a give-up is
the only reason it is ever raised, so a field that would always read `true` carries no information.
`CleanUp()` unsubscribes both this and `SystemEvents.DisplaySettingsChanged` in a `finally`
(`VibranceGUI.cs:433-434`) — mandatory, not just good practice; see the guard described next.

**`OnDisplaySettingsChanged`/`OnResolutionChangeFailed` guard both `IsDisposed` and
`!IsHandleCreated`, not just `InvokeRequired`** (`VibranceGUI.cs:515-580`). `Control.InvokeRequired`
returns **false** whenever the control has no window handle yet, and the handle genuinely does not
exist for the whole span of the constructor's NvAPI/ADL initialisation after these handlers are
subscribed (`backgroundWorker_DoWork` busy-waits on `!IsHandleCreated`) — exactly the window in which
a `SystemEvents` notification is likely at autostart, as monitors settle, since `SystemEvents` starts
its own dedicated thread and message pump the moment the first handler is attached, independent of
the form's handle entirely. Without the explicit check, a notification landing in that window would
mutate `_windowsResolutionSettings` directly on the `SystemEvents` thread. The same guard covers the
symmetric shutdown case: `CleanUp()`'s `-=` cannot cover a notification already in flight, which
could otherwise find the handle destroyed, or the form disposed (`BeginInvoke` throwing
`ObjectDisposedException` on the `SystemEvents` thread with nothing there to catch it).

**Give-up bounds are asymmetric on purpose** (`ApplyFailureBound = 3`, `RevertFailureBound = 10`,
`ResolutionHelper.cs:87-88`): giving up on an *apply* only strands the user at their own Windows
resolution, the safe side; giving up on a *revert* strands them at the **game's** resolution, with
nothing else in the program that will ever retry it, so it is worth trying substantially longer
before accepting that outcome.

**The frozen-snapshot fix.** `_windowsResolutionSettings` (`VibranceGUI.cs`) used to be built once in
the constructor and never touched again — if the user changed their desktop resolution by hand, or
plugged in a monitor, the cached "Windows resolution" the revert path compares against went stale,
and every API call involved still reported success (see former defect **D58** below). The
constructor now calls `RebuildWindowsResolutionSettings(true)` (`VibranceGUI.cs:77`), which projects
`Screen.AllScreens` into a device-name list and delegates the actual refresh to
`WindowsResolutionRefresher.Refresh` (`WindowsResolutionRefresher.cs`) — extracted out of
`VibranceGUI.cs` so `ResolutionChangeFixture` can drive it through a fake `IDisplayModeDevice`, with
no `Screen`, no `Form` and no real display anywhere in the call stack. `SystemEvents.DisplaySettingsChanged`
(`Microsoft.Win32`, already referenced via `System.dll`) is subscribed in the constructor (`:103`) and
**must** be unsubscribed in `CleanUp()` (`:441`) — it holds a strong reference to the handler on its
own dedicated thread, so a leaked subscription leaks the form and can fault at shutdown. The handler
(`OnDisplaySettingsChanged`, `:493-526`) guards `IsDisposed`/`!IsHandleCreated` and marshals onto the
UI thread with `BeginInvoke` (see above) before handing off to `ResolutionAdoptionDebouncer` — **not**
calling `RebuildWindowsResolutionSettings(false)` directly any more (see "Known limitation" below for
why this changed) — which itself calls it, either immediately or after a debounce, with
`false` so a hot-plug or resolution change never pops the constructor's own failure dialog from
inside an arbitrary system event, which would be exactly the D2-shaped mistake this whole fix
removes.
**The single most dangerous line in the fix:** while `_v.GetVibranceInfo().isResolutionChangeApplied`
is true (a game's own resolution change is currently live), a refresh must **not** overwrite the
already-captured "Windows resolution" (`Item1`) for any known device — a live read at that moment
would return the *game's* mode, not the desktop's, and silently adopting it would strand the desktop
at the game's resolution forever, since the revert path compares against exactly that value
(`WindowsResolutionRefresher.Refresh`, `WindowsResolutionRefresher.cs:34-137`). `Item2` (the
supported-mode list) is carried over unchanged in that case — the same `List<ResolutionModeWrapper>`
instance, never re-enumerated; a screen with no prior entry still gets both captured fresh, since it
cannot be the screen the game is running on. That same instance is reused — never a fresh copy — for
any device already in the dictionary regardless of that flag, since it is a property of the device
rather than of whichever mode is currently active; re-enumerating it on every refresh would cost
several hundred `EnumDisplaySettings`
P/Invokes per screen on the UI thread, twice per alt-tab cycle (vibranceGUI's own resolution changes
fire `DisplaySettingsChanged` too), and reusing the identical instance is also what keeps
`_supportedResolutionList` — captured once, in the constructor, and `readonly` — from silently going
stale after a refresh.

**A device that drops out and later reattaches keeps its desktop mode.** `_lastKnownWindowsModes`
(`VibranceGUI.cs`) records every device's last-captured mode, INCLUDING one no longer attached, so
that when `Refresh` sees a device with `preserveCapturedMode` true but no existing dictionary entry —
because it dropped out of `attachedDeviceNames` during an earlier refresh, e.g. a cable bounce or a
docking-station reattach while a game's own resolution change is still applied to it — it falls back
to that retained mode instead of a live read that would otherwise capture the game's own mode, the
same danger the paragraph above describes (`WindowsResolutionRefresher.cs:89-103`). The map is never
pruned once a device drops out, but that is safe to leave unbounded in practice: it is bounded by the
OS's own `\\.\DISPLAYn` device-name namespace, not by anything this program tracks. `Item2` is *not*
carried across the gap — a detached device has no dictionary entry at all, so a reattach always
re-enumerates its supported modes from scratch (`WindowsResolutionRefresher.cs:67-80`), picking up
whatever a driver update or a different port reports now.

**An adopted foreign mode is never special-cased by value, on purpose — narrowed by duration
instead.** `WindowsResolutionRefresher.Refresh` itself is untouched: with `preserveCapturedMode`
false, it still adopts whatever mode is live as the new `Item1` the instant it actually runs — even
one a game set without going through a profile-driven apply at all (see
`ResolutionChangeFixture.cs`, `CheckAdoptedForeignModeSelfHealsOnceItIsGone`, which documents exactly
that as `Refresh`'s permanent, deliberate contract). A reviewer proposed skipping that re-capture
whenever the live mode matches a configured `ApplicationSetting.ResolutionSettings` entry instead;
it was rejected outright, and still is, because at refresh time "a game changed the resolution
itself with no profile applied" and "the user's own genuine desktop mode happens to equal a
configured entry" are observationally identical — both are a bare `DisplaySettingsChanged` plus a
live mode that differs from the last capture, with no reliable field comparison available to tell
them apart (`Equals` includes the driver-unreliable `DmDisplayFixedOutput`; `MatchesAchievedMode`
widens the false-positive surface instead). Special-casing the match would make a user's *real*
desktop mode permanently un-capturable the moment they configure a game at that same mode, and every
future revert would drag them to a stale value while still reporting success — a strictly worse,
non-self-healing failure than adopting a game's own foreign mode, which corrects itself the moment
that mode goes away.

`ResolutionAdoptionDebouncer` (`ResolutionAdoptionDebouncer.cs`) is the fix that keys on *duration*
instead, sitting one layer above `Refresh`: `VibranceGUI.OnDisplaySettingsChanged` no longer calls
`RebuildWindowsResolutionSettings(false)` directly, but arms a one-shot countdown
(`DebounceIntervalMs`, currently 2000ms) and only lets that call through once a live mode has held
steady with no further `DisplaySettingsChanged` arriving for the whole interval. **This narrows the
exposure, it does not close it.** A game that holds its own foreign mode for *longer* than
`DebounceIntervalMs` — exclusive fullscreen for the whole session, the dominant real-world shape —
is still adopted exactly as before, the moment the debounce elapses with that mode still live, and
still self-heals the same way once the mode goes away. What the debounce actually removes is
adoption of foreign modes that *don't* outlast the interval: startup/exit mode flaps, alt-tab
restore/re-set pairs, launcher and anti-cheat mode sets, and the window at game exit where the dying
mode is still live before Windows restores the desktop. The accepted cost in exchange: if a
countdown is still pending when a profiled game's own apply sets `isResolutionChangeApplied`, the
pending genuine change is not merely delayed — `RebuildWindowsResolutionSettings` re-reads that flag
as true at whichever moment it next runs and preserves the *old*, pre-change `Item1` unconditionally
(the single most dangerous line above, working exactly as designed), so the user's change is dropped
for that entire game session and the game's own exit-revert then drags the real desktop back to the
stale value. Narrow — it needs a genuine resolution change and a profiled game launch inside the
same `DebounceIntervalMs` window to coincide — but real. The constructor's own initial build
(`RebuildWindowsResolutionSettings(true)`) is not routed through the debouncer at all and is
unaffected by any of this: restarting vibranceGUI while a game already holds a foreign mode still
captures it immediately, unchanged from before this class existed.

**The fixed-output-loop fix.** `IsResolutionChangeNeeded`/`ChangeResolutionEx`'s "does this still
need changing?" guard is `ResolutionModeWrapper.MatchesAchievedMode` (`ResolutionModeWrapper.cs`),
which compares only `DmPelsWidth`/`DmPelsHeight`/`DmBitsPerPel`/`DmDisplayFrequency` —
**deliberately not** `DmDisplayFixedOutput`. `Equals`/`GetHashCode`/`ToString` are untouched and still
compare all five fields (the combo box lookup and the `applicationData.xml` round trip depend on
that). `DmDisplayFixedOutput` is only honoured by `ChangeDisplaySettingsEx` when
`DM_DISPLAYFIXEDOUTPUT` survives into the *achieved* mode's own `dmFields`, which is driver-dependent
— some drivers apply the four real fields correctly but silently pin this one to their own default
regardless of what was requested. Basing the guard on a field a driver is free to never honour is
what let a user's "(Center)" mode selection (former defect **D59** below) re-fire a real mode set and
registry write on every single foreground event, forever, even though the mode had genuinely already
been achieved on every field the driver actually supports.

The working user-side mitigation is still the "Never change resolutions" checkbox, which
short-circuits every call site. It is also the default on the unmerged `feature/add-color-settings`
branch (commit `6900bac`), which tells you what the maintainer concluded about this feature before
this fix existed.

**UNCERTAIN, still:** the root cause of a `DISP_CHANGE_BADFLAGS`-class rejection from `CDS_TEST`
itself cannot be determined from this repository — it originates inside `user32`/the driver. The
candidates consistent with the code are unchanged from before this fix: (a) the device not being
attached to the desktop, or a mirroring/virtual driver, rejecting the mode outright — `deviceName`
comes straight from `Screen.FromHandle(e.Handle).DeviceName` with no attachment check; (b) a
display-topology change since the mode was captured (hot-plug, sleep/resume, driver restart) making
the cached entry refer to a different physical device. What changed is that `CDS_TEST` now catches
this **before** anything is written, and the give-up bound and notification mean a persistently
rejecting device stops being retried and tells the user, instead of retrying forever with a modal box
on every switch.

### 6.5 Shutdown

Triggered from the tray menu's `Exit` or the window's X, via `Form1_FormClosing` → `CleanUp()`
(`VibranceGUI.cs:161-164`, `:314-332`):

```
CleanUp():
  statusLabel.Text = "Closing..."; ForeColor = Red; this.Update()     // :318-320
  if (_v != null && _v.GetVibranceInfo().isInitialized):
      _v.HandleDvcExit()      // restore the Windows level on the displays
      _v.SetShouldRun(false)  // vestigial — nothing reads shouldRun
      _v.UnloadLibraryEx()    // unhook the WinEvent hook, then unload the native library (NVIDIA)
  catch (Exception ex) -> Log(ex)                                     // :328-331
```

What is **not** done on shutdown, by omission:

- settings are not saved — a pending 5-second debounced save is simply lost
  ([§9.6](#96-the-debounced-save));
- **the screen resolution is not restored** if a game was ingame when vibranceGUI exits;
- the extracted `%APPDATA%\vibranceGUI\vibranceDLL.dll` is not deleted;
- `WinEventHook._instance` is not cleared (`WinEventHook.cs:181`);
- on AMD, ADL is never torn down at all — `UnloadLibraryEx` unhooks and returns `true`
  (`AmdDynamicVibranceProxy.cs:82-86`), and `IAmdAdapter.Dispose()` is never called by anyone
  ([§8.6](#86-resource-management-on-the-amd-path)).

On an abnormal exit (Task Manager kill, crash, logoff, power loss) **nothing at all is restored** — no
vibrance, no resolution. This is the mechanism behind reports like issue #144 ("vibrance does not reset
to Windows level when program closes"): the reset only happens on the clean `FormClosing` path, and
even then only if `isInitialized` was true.

### 6.6 Why hooks, and not polling — reading the fossils

You will find several members that make no sense until you know the history. Before commit `20065d2`
("Updated vibranceGUI to be dynamic for all programs", 2015-07-07) the NVIDIA proxy ran a polling loop
on a background thread:

```csharp
while (vibranceInfo.shouldRun) { /* check the foreground window */ Thread.Sleep(vibranceInfo.sleepInterval); }
```

That commit introduced `WinEventHook.cs` and replaced the poll with event-driven detection. The
leftovers are still in the tree and are **all dead**: `VibranceInfo.shouldRun` and
`VibranceInfo.sleepInterval` (`common/Definitions.cs:16-17`), `SetShouldRun` on the interface,
`SetSleepInterval` (`NvidiaDynamicVibranceProxy.cs:341-344`, not even on the interface), the empty
`HandleDvc()` stub (`:346-348`), and — on the native side — the `handleDVC` export, which still
contains the in-DLL polling loop and the string `"DVC Level Thread exited!"` (VERIFIED binary).

Do not "fix" these by wiring them up. Delete them or leave them; they describe a design that no longer
exists.

### 6.7 Threading model in one page

| Thread | What runs on it |
|---|---|
| **UI / STA main thread** (`Application.Run`, `Program.cs:96`) | all WinForms work; **all WinEvent callbacks** and therefore all vibrance writes and all `ChangeDisplaySettingsEx` calls; both proxy constructors |
| `backgroundWorker` (`VibranceGUI.cs:116-151`) | one-shot startup load: busy-wait for the handle, `Invoke` the settings read, then push configuration into the proxy |
| `settingsBackgroundWorker` (`VibranceGUI.cs:181-185`) | `Thread.Sleep(5000)` then save ([§9.6](#96-the-debounced-save)) |
| `ProcessExplorer.backgroundWorker` (`ProcessExplorer.cs:107-110`) | enumerate running processes, report each entry back to the UI thread |
| `Microsoft.Win32.SystemEvents`' own dedicated thread | raises `DisplaySettingsChanged` (`VibranceGUI.cs:500-518`); the handler `BeginInvoke`s onto the UI thread before touching `_windowsResolutionSettings`, which `OnWinEventHook` reads with no locking of its own — see [§6.4](#64-the-optional-resolution-switch) |

Consequences you must design around:

- **No locking exists anywhere.** It is safe today only because the interesting mutations happen on one
  thread. The `out` parameters captured by the lambda at `VibranceGUI.cs:129-132` are written on the UI
  thread and read on the worker thread, with the implicit `Invoke` barrier as the only synchronisation.
- **Callbacks keep firing during nested modal loops** (`VibranceSettings.ShowDialog()` at
  `VibranceGUI.cs:511`, any `MessageBox`), so a proxy can be reading `_applicationSettings` while the
  user is mid-edit. Single-threaded, so no data race — but real re-entrancy.
- **Slow work in the callback freezes the UI** and, because it is a foreground-change callback,
  potentially the moment a game goes fullscreen.

### 6.8 Known blind spots in detection

1. **Startup blind spot.** The hook fires on *transitions* only; a game already running in the
   foreground gets nothing until you alt-tab away and back.
2. **Process-exit race** (`WinEventHook.cs:242-249`). If the process that raised the event has exited
   by the time `Process.GetProcessById` runs, the exception is swallowed and **no event is dispatched
   at all**. When a game crashes or exits, the very event that would have reverted vibrance can be
   dropped, leaving the desktop at the ingame level until the next foreground switch. This is a
   plausible mechanism behind issue #144-style reports and part of issue #137 ("does not reliably
   detect game in foreground") — **INFERENCE**, not confirmed at runtime.
3. **Window-text buffer race** (`:223-225`): `GetWindowTextLength` then `GetWindowTextA` are two calls
   and the title can change in between. Irrelevant in practice — nothing reads that text.
4. **Stale-event double-check exists only on the revert path** (NVIDIA `:239-240`, AMD `:140-141`).
   The apply path has no such check.
5. **`GetInstance()` has no lock** (`WinEventHook.cs:212-217`). Today both proxies call it from the UI
   thread, so the lazy singleton is safe; construct proxies concurrently in future and you get two
   hooks and a leaked handle.

### 6.9 Every message the user can see, and where it comes from

Support shortcut: a user quotes a string, you find the code path. Strings are verbatim from the
source; the ellipses mark text abbreviated for this table only.

| Message | Origin | Shown when |
|---|---|---|
| "You can run vibranceGUI only once at a time!" (caption "vibranceGUI Error") | `Program.cs:30` | another instance already holds the mutex; the app then exits. Drawn **unthemed**, because it precedes `EnableVisualStyles()` (`:34`) |
| "Failed to determine your Graphic GraphicsAdapter type (NVIDIA/AMD). … Intel laptops are not supported … Error: " + the Win32 error message | `ErrorGraphicsAdapterUnknown`, `Program.cs:19`, shown `:74-79` | `GetAdapter() == Unknown`. Yes → opens the maintainer's Twitter. **The app exits either way** |
| "Both NVIDIA and AMD graphic drivers have been found on your system. … Use the program \"Display Driver Uninstaller\" …" | `ErrorGraphicsAdapterAmbiguous`, `Program.cs:20`, shown `:83-88` | both vendor DLLs found in SysWOW64. Yes → opens the Guru3D DDU page. **The app exits either way** — and on a hybrid laptop this advice is wrong (**D3**) |
| *(a raw .NET exception dump — unlocalised stack trace)* | `MessageBox.Show(ex.ToString())` — `NvidiaDynamicVibranceProxy.cs:157`, `AmdDynamicVibranceProxy.cs:47` | first dialog on **any** exception inside a proxy constructor |
| "VibranceProxy failed to initialize! Press Ok to open the vibranceGUI Steam Guide in your browser. Scroll down to section \"Troubleshooting, Errors, Q&A\"." | `NvapiErrorInitFailed`, `NvidiaDynamicVibranceProxy.cs:123-124`; shown `:158` and **reused by AMD** at `AmdDynamicVibranceProxy.cs:48` | immediately after that dump. OK → `https://vibrancegui.com/vibrance/guide` (**D21**) |
| "VibranceProxy failed to initialize! Graphics card system type (Desktop / Laptop) is unknown!" | `NvapiErrorSystypeUnknown`, `NvidiaDynamicVibranceProxy.cs:128`, shown `:180` | any enumerated GPU reports `NvSystemTypeUnknown`. Really means "the NvAPI call failed" (**D19**) |
| "VibranceProxy detected that you are running a Laptop with integrated NVIDIA card. …" | `NvapiErrorSystypeUnsupported`, `NvidiaDynamicVibranceProxy.cs:125-127` | **never — dead constant** (**D20**) |
| "Current resolution mode could not be determined. Switching back to your Windows resolution will not work." | `ShowResolutionReadFailureDialog` (`VibranceGUI.cs:482`), passed as the `onUnreadableDevice` callback to `WindowsResolutionRefresher.Refresh` only when `RebuildWindowsResolutionSettings`'s `showFailureDialog` is true | `EnumDisplaySettings` failed for a monitor. Shown only from the constructor's own build — the `SystemEvents.DisplaySettingsChanged` refresh path (`showFailureDialog: false`) never shows it, deliberately: see [§6.4](#64-the-optional-resolution-switch). The callback's `deviceName` parameter is unused in the message on purpose — it exists so `ResolutionChangeFixture` can assert *which* device reported, not to make this dialog start naming devices |
| *(historical)* "Changing the resolution failed: DispChangeBadflags" (or any other `DispChange` member name) | **removed** — `ResolutionHelper.cs` has no `using System.Windows.Forms` and no `MessageBox` call site after `work/resolution-change` | was a staging `ChangeDisplaySettingsEx` failure, raised **inside the foreground-change callback**, repeating on every subsequent switch (**D2**, issues #114/#132 — see [§6.4](#64-the-optional-resolution-switch) for the replacement: a `notifyIcon` balloon tip via `ResolutionHelper.ResolutionChangeFailed`) |
| Balloon tips: "Registered to Autostart!" / "Registering to Autostart failed!" / "Updated Autostart Path!" / "Updating Autostart Path failed!" / "Unregistered from Autostart!" / "Unregistering from Autostart failed!" | `VibranceGUI.cs:266-288` | the autostart checkbox — **including when it is set programmatically at startup** ([§9.5](#95-autostart)) |
| Status label: "Initializing…" → "Running!" (green) → "Closing…" (red) | `VibranceGUI.Designer.cs:194`; `VibranceGUI.cs:205-206`; `:318-319` | "Running!" appears only if `isInitialized` was true (`:139-141`) — if it never turns green, the vendor layer failed silently (**D23**) |
| "NVAPI Unloaded: …" | `VibranceGUI.cs:210` | **never** — `ReportProgress(2)` is never called (**§12.7**) |
| "NVAPI Error" (a native `MessageBoxA` caption) | string inside `vibranceDLL.dll` (VERIFIED binary), used by the native `printError` | not reachable from the 13 bound exports |

Everything not in this table **fails silently**: a `false` return from `initializeLibrary`, every
every `setDVCLevel` failure (return values discarded at `NvidiaDynamicVibranceProxy.cs:202,232,261,265,372,375`),
and every ADL return code (`AmdAdapter32.cs:20,135`).

---

## 7. The NVIDIA path

### 7.1 The layer stack

```
VibranceGUI (shell)
   │  IVibranceProxy
   ▼
NvidiaDynamicVibranceProxy.cs        C#, 13 P/Invokes, ALL state static
   │  DllImport("vibranceDLL.dll"), CallingConvention.StdCall
   ▼
vibranceDLL.dll                      prebuilt native C++ (PE32 i386, 2017) — SOURCE NOT IN THIS REPO
   │  LoadLibraryA("nvapi.dll") + nvapi_QueryInterface(<13 ids>)
   ▼
nvapi.dll → NVIDIA display driver → Digital Vibrance on the panel
```

### 7.2 What `vibranceDLL.dll` actually is

**VERIFIED (binary).** `vibrance.GUI/NVIDIA/vibranceDLL.dll` is **163,840 bytes**, SHA-256
`0f229f79934f21617337c28915a9449f7b2395b20d7fb0e4a02d14277163cea0`, **PE32 i386** (machine `0x14c`),
6 sections, link timestamp **2017-01-02 18:22:43 UTC** — a native C++ MSVC Release build.

- Its embedded PDB path is `C:\Users\juv\Documents\GitHub\vibranceDLL\Release\vibranceDLL.pdb`, i.e.
  **the source lives in a separate repository, `juvlarN/vibranceDLL`**. Commit `06b40fb` in this repo
  ("Updated vibranceDLL to https://github.com/juvlarN/vibranceDLL/commit/e2b480f2…") confirms the
  workflow: the DLL is built there and the binary is copied in here.
- **There is no version resource** (no `VS_VERSION_INFO`), so the binary cannot be version-checked at
  runtime and you cannot tell two builds apart except by hash.
- **It does not statically import `nvapi.dll`.** Its import table is only `KERNEL32.dll` (77 functions
  including `LoadLibraryA`, `GetProcAddress`, `GetSystemDirectoryW`), `USER32.dll` (`FindWindowW`,
  `GetWindowTextA`, `GetWindowTextLengthW`, `MessageBoxA`, `GetForegroundWindow`) and `ADVAPI32.dll`
  (`SystemFunction036`). NvAPI is resolved dynamically at init ([§7.4](#74-the-initialisation-handshake)).
- Deployment is described in [§3.5](#35-how-the-native-nvidia-dll-is-deployed-not-what-you-would-guess):
  embedded as an MSBuild resource, extracted by hand to `%APPDATA%\vibranceGUI\` on every launch.

**Practical consequence:** the NVIDIA capability surface of vibranceGUI is frozen at whatever those 13
exports do. Anything new on the NVIDIA side requires building the *other* repository.

### 7.3 The P/Invoke surface

All entry points are **MSVC-mangled C++ member functions** of `vibranceDLL::vibrance`
(`?name@vibrance@vibranceDLL@@QAE…`, where `QAE` means `public: __thiscall`). All are declared in C#
as `CallingConvention.StdCall` **with no `this` argument** — see the hazard in
[§7.5](#75-the-__thiscall-as-stdcall-binding--the-single-biggest-contributor-hazard).

| # | C# declaration (`NvidiaDynamicVibranceProxy.cs`) | Native signature | CharSet |
|---|---|---|---|
| 1 | `bool initializeLibrary()` `:19-24` | `bool()` | Auto |
| 2 | `bool unloadLibrary()` `:26-31` | `bool()` | Auto |
| 3 | `int getActiveOutputs(int[], int[])` `:34-39` | `int(int* const[], int* const[])` | Auto |
| 4 | `void enumeratePhsyicalGPUs(int[])` `:41-46` | `void(int* const[])` | Auto |
| 5 | `bool getGpuName(int[], StringBuilder)` `:48-53` | `bool(int* const[], char*)` | **Ansi** |
| 6 | `bool getDVCInfo(ref NvDisplayDvcInfo, int)` `:55-60` | `bool(NV_DISPLAY_DVC_INFO*, int)` | **Ansi** |
| 7 | `int enumerateNvidiaDisplayHandle(int)` `:62-67` | `int(int)` | Auto |
| 8 | `bool setDVCLevel(int, int)` `:69-74` | `bool(int,int)` | Auto |
| 9 | `bool isWindowActive(ref IntPtr)` `:76-81` | `bool(HWND*)` | Auto |
| 10 | `bool isCsgoStarted(ref IntPtr)` `:83-88` | `bool(HWND*)` — **never called** | **Ansi** |
| 11 | `bool equalsDVCLevel(int, int)` `:90-95` | `bool(int,int)` | Auto |
| 12 | `NvSystemType getGpuSystemType(int)` `:97-102` | `int(int*)` — **native takes a pointer, C# passes an `int` by value** | Auto |
| 13 | `int getAssociatedNvidiaDisplayHandle(string, int)` `:110-115` | `int(const char*, int)` | **Ansi** |
| — | `GetWindowTextLength` / `GetWindowTextA` `:104-108` | `user32.dll` — **both dead** | — |

**The typo `enumeratePhsyicalGPUs` is in the exported symbol itself**, so it must be preserved verbatim
in any rebinding. Do not "fix" the spelling on the C# side.

**Exports present in the DLL but not bound by C#** (VERIFIED binary export table, 21 entries):
constructor, destructor, `operator=`, `?getInterfaceVersionString@`, `?handleDVC@`, two `?printError@`
overloads, `?test@`. `handleDVC` is the legacy in-DLL polling loop — it contains the string
`"DVC Level Thread exited!"` and calls `FindWindowW(NULL, L"Counter-Strike: Global Offensive")` — and
the C# side still carries a matching **empty** `HandleDvc()` stub (`:346-348`).

### 7.4 The initialisation handshake

**VERIFIED (binary)** — full decode of `initializeLibrary` at RVA `0x2e00`:

1. `LoadLibraryA("nvapi.dll")`; return `false` if it fails.
2. `GetProcAddress(hMod, "nvapi_QueryInterface")`, cached at `0x1002835c`.
3. **13 consecutive `nvapi_QueryInterface(id)` calls** (cdecl, arguments cleaned up in one
   `add esp,0x34`), each result cached in a module global. The IDs are literal bytes in the binary:

| Order | Interface ID (verified bytes) | Name (from public NvAPI ID tables) | Used by |
|---|---|---|---|
| 1 | `0x0150E828` | `NvAPI_Initialize` | end of `initializeLibrary` |
| 2 | `0xD22BDD7E` | `NvAPI_Unload` | `unloadLibrary` |
| 3 | `0xE5AC921F` | `NvAPI_EnumPhysicalGPUs` | `enumeratePhsyicalGPUs` |
| 4 | `0xCEEE8E9F` | `NvAPI_GPU_GetFullName` | `getGpuName` |
| 5 | `0xE3E89B6F` | `NvAPI_GPU_GetActiveOutputs` | `getActiveOutputs` |
| 6 | `0x4085DE45` | `NvAPI_GetDVCInfo` | `getDVCInfo`, `equalsDVCLevel` |
| 7 | `0x172409B4` | `NvAPI_SetDVCLevel` | `setDVCLevel` |
| 8 | `0x9ABDD40D` | `NvAPI_EnumNvidiaDisplayHandle` | `enumerateNvidiaDisplayHandle` |
| 9 | `0x01053FA5` | `NvAPI_GetInterfaceVersionString` | (unbound export) |
| 10 | `0x6C2D048C` | `NvAPI_GetErrorMessage` | `printError` (unbound) |
| 11 | `0x0E45002D` | `NvAPI_GetDVCInfoEx` | **resolved and null-checked, then never used** |
| 12 | `0x35C29134` | `NvAPI_GetAssociatedNvidiaDisplayHandle` | `getAssociatedNvidiaDisplayHandle` |
| 13 | `0xBAAABFCC` | `NvAPI_GPU_GetSystemType` | `getGpuSystemType` |

*(The IDs are verified byte values from the binary; the human-readable names come from public NvAPI ID
tables, not from the binary itself.)*

4. It null-checks **12 of the 13** pointers. **`NvAPI_GetAssociatedNvidiaDisplayHandle` is not
   checked** — on a driver too old to expose it, init still succeeds and per-window display resolution
   later calls through a NULL pointer.
5. Calls `NvAPI_Initialize()` and returns `status == NVAPI_OK`.

**Driver-version assumption:** because all 12 checked IDs must resolve, the DLL effectively demands a
driver that exposes `NvAPI_GetDVCInfoEx` (a 2013-era addition) **even though it never calls it**. Any
driver missing any one of them makes `initializeLibrary` return `false`, after which the C# constructor
silently skips `InitializeProxy()` (`:144-147`), `isInitialized` stays `false`, and **no error dialog is
shown** — the user gets a dead window with every control greyed out.

### 7.5 The `__thiscall`-as-StdCall binding — the single biggest contributor hazard

The exports are `__thiscall` member functions; C# declares them `StdCall` and passes no `this`
pointer. **This works, and the reason it works is fragile:**

- **VERIFIED (binary):** the constructor `??0vibrance@vibranceDLL@@QAE@XZ` at RVA `0x2f70` is
  `8b c1 c3` — `mov eax, ecx; ret`. The destructor at `0x2f80` is a bare `ret`. **The class carries no
  instance state**; everything lives in module globals at `0x10028328`–`0x1002835c`.
- **VERIFIED (binary):** none of the 13 bound exports reads the incoming `ECX`. For example
  `setDVCLevel` (RVA `0x2c20`) is
  `push ebp; mov ebp,esp; push [ebp+0Ch]; push 0; push [ebp+08h]; call [NvAPI_SetDVCLevel]; add esp,0Ch; test eax,eax; sete al; pop ebp; ret 8`
  — arguments come only from `[ebp+8]`/`[ebp+0Ch]`, and cleanup is callee-side `ret 8`, byte-for-byte
  compatible with `__stdcall(int,int)`.

**So the binding is safe only because the C++ class is stateless. If anyone ever adds a member field to
the C++ `vibrance` class, every one of these P/Invokes breaks with garbage-`this` corruption.** Write
this on a sticky note before touching `juvlarN/vibranceDLL`.

**Related hazard — INFERENCE, not confirmed at runtime.** All the `bool`-returning exports set only
**AL** (`sete al` / `mov al,1` / `xor al,al`), leaving the upper 24 bits of `EAX` holding whatever the
last NvAPI status left there. C#'s default marshalling of a `bool` *return value* is a 4-byte Win32
`BOOL`, i.e. the whole of `EAX` tested against zero. Concretely, `initializeLibrary`'s failure exit at
RVA `0x2f63` is `xor al,al; ret`, reached from a pointer null-check, so `EAX` can still hold a non-zero
function pointer with a zeroed low byte → **a failed init could be observed as `true` in C#.** This is
cheap to test and would explain "it says it's running but nothing happens" reports. It has not been
tested.

### 7.6 What each native call really does (VERIFIED binary)

| Export | Behaviour |
|---|---|
| `enumeratePhsyicalGPUs` (`0x2cc0`) | `NvAPI_EnumPhysicalGPUs(array, &count)`. The count is written to a DLL-local stack slot and **discarded**; C# infers population by scanning for non-zero entries (`:173-175`). |
| `getGpuName` (`0x2d00`) | `NvAPI_GPU_GetFullName(gpuHandles[0], szName)` — **only ever GPU index 0**. 64-byte buffer, matching `StringBuilder(64)` at `:191`. |
| `getActiveOutputs` (`0x2d20`) | Loop counter compared `cmp esi,1; jb` → **exactly one iteration**, on `gpuHandles[0]`. It treats the second parameter as `int*[]` and passes `outputIds[i]` **as a pointer**; C# hands it a zero-filled `int[64]` (`:170`), so **NULL** reaches NvAPI. **INFERENCE:** NvAPI rejects the NULL with a non-zero status, so `activeOutput` ends up holding an NvAPI *error code* and the null-deref is never reached. Either way the call is functionally dead — a genuine type mismatch across the boundary. |
| `getDVCInfo` (`0x2bf0`) | Writes `0x00010010` into `info.version` itself, then `NvAPI_GetDVCInfo(handle, 0, &info)` — **outputId hardcoded to 0**. |
| `setDVCLevel` (`0x2c20`) | `NvAPI_SetDVCLevel(handle, 0, level)` — outputId hardcoded to 0, **no clamping**; the level passes straight through. |
| `equalsDVCLevel` (`0x2b90`) | Builds its own `NV_DISPLAY_DVC_INFO`, calls `NvAPI_GetDVCInfo`, returns `currentLevel == level`. **Every "do I need to write?" check is a real driver round-trip, executed inside the WinEvent callback.** |
| `enumerateNvidiaDisplayHandle` (`0x2c40`) | The critical one. `NvAPI_EnumNvidiaDisplayHandle(index, &h)`: status `0` → return `h`; status `-7` (`NVAPI_END_ENUMERATION`) → return `-1`; **any other status falls into the success path and returns the untouched local, i.e. `0`.** This is the engine behind the runaway loop in [§7.9](#79-nvidia-specific-failure-modes). |
| `getAssociatedNvidiaDisplayHandle` (`0x2da0`) | `NvAPI_GetAssociatedNvidiaDisplayHandle(szName, &h)`; returns `h` on success, **`-1` on any failure**. The `int length` parameter is **entirely unused**, which makes the `GCHandle.Alloc(deviceName, Pinned)` / `Free()` dance at `:307-309` pure ceremony — the marshaller copies the string to a native ANSI buffer regardless, and pinning a managed string does nothing for the callee. |
| `isWindowActive` (`0x2b60`) | `GetForegroundWindow()`; `false` if NULL, else `*hwnd == foreground`. |
| `isCsgoStarted` (`0x2b40`) | `FindWindowW(NULL, L"Counter-Strike: Global Offensive")`, stores the result through the pointer, and **always returns TRUE** (`mov al,1`). Hardcoded to one game title; unused from C#. |
| `unloadLibrary` (`0x2df0`) | `NvAPI_Unload()`, returns `status == 0`. |

Note the pattern: **`outputId` is hardcoded to `0` in both `getDVCInfo` and `setDVCLevel`**, so
vibranceGUI can only ever address a GPU's first output.

### 7.7 C#-side initialisation, and the startup reset users notice

`InitializeProxy()` (`NvidiaDynamicVibranceProxy.cs:167-207`), in order:

1. `enumeratePhsyicalGPUs` into an `int[64]` (`NvapiMaxPhysicalGpus = 64`, `:119`).
2. For every non-zero handle, `getGpuSystemType(handle)`; if **any** returns `NvSystemTypeUnknown`,
   show *"VibranceProxy failed to initialize! Graphics card system type (Desktop / Laptop) is
   unknown!"*, set `isInitialized = false` and **return** (`:177-185`).
3. `EnumerateDisplayHandles()` (`:286-297`) — calls `enumerateNvidiaDisplayHandle(i)` for
   `i = 0,1,2,…` **until it returns `-1`**, appending each handle.
4. `getActiveOutputs` → `_vibranceInfo.activeOutput` (**never read anywhere**).
5. `getGpuName` → `_vibranceInfo.szGpuName` (**never displayed**); `:192` allocates a dead
   `char[64] sz`.
6. `_vibranceInfo.defaultHandle = enumerateNvidiaDisplayHandle(0)`.
7. `getDVCInfo(ref info, defaultHandle)`; **if `info.currentLevel != userVibranceSettingDefault`,
   write `userVibranceSettingDefault`** (`:197-204`).
8. `isInitialized = true`.

Step 7 has a behavioural consequence that is easy to miss and that users do notice.
`InitializeProxy` runs from the constructor (`:146`), and `_vibranceInfo` was default-initialised three
lines earlier (`:143`), so **`userVibranceSettingDefault` is still `0` at that moment**. The saved
Windows level is not pushed in until the startup worker reaches
`VibranceGUI.cs:147`. Net effect: **launching vibranceGUI on NVIDIA momentarily forces Digital Vibrance
to neutral (0 / "50%") on the primary display, then re-applies the saved value.** The AMD path performs
no equivalent write at init.

Also note the "system type unknown" dialog in step 2 is a **hardware-sounding message for a software
failure**: the DLL returns `NvSystemTypeUnknown` for *any* NvAPI failure in `getGpuSystemType`
(VERIFIED binary: `xor ecx,ecx; cmovz ecx,[local]` at RVA `0x2d60` — error ⇒ 0). And the
laptop-specific rejection message that the README implies exists
(`NvapiErrorSystypeUnsupported`, `:125-127`) is **never shown by any code path**;
`NvSystemTypeLaptop` is not rejected anywhere.

### 7.8 NVIDIA value semantics

| Property | Value | Source |
|---|---|---|
| Raw driver range | `0 … 63` | `NvapiMaxLevel = 63` (`:120`) |
| Neutral / default | `0` | `NvapiDefaultLevel = 0` (`:121`) — displayed as "50%" |
| Slider min / max / default | `0` / `63` / `0` | `Program.cs:65-68` → `VibranceGUI.cs:51-52`, `VibranceSettings.cs:16-18` |
| Slider → driver | **identity** — the trackbar value *is* the DVC level | `VibranceGUI.cs:173`, `VibranceSettings.cs:40,52` |
| Slider → label | `NvidiaVibranceValueWrapper.Find(x).Percentage` | `Program.cs:69` |
| Clamping | **driver-side only** — the DLL passes the level through unmodified (VERIFIED binary) | `setDVCLevel` RVA `0x2c20` |
| Driver-reported range | `minLevel`/`maxLevel` are fetched (`:198`) and **ignored** | `NvidiaTypes.cs:14-15` |

`NvidiaVibranceValueWrapper` (`vibrance.GUI/NVIDIA/NvidiaVibranceValueWrapper.cs`) maps raw levels to
the percentages the NVIDIA Control Panel shows:

- `staticValues` (`:24-25`) is a **51-element list** of the raw levels the Control Panel itself
  produces: `0,1,3,4,5,6,8,9,10,11,13,14,15,16,18,19,20,21,23,24,25,26,28,29,30,32,33,34,35,37,38,39,40,42,43,44,45,47,48,49,50,52,53,54,55,57,58,59,60,62,63`.
- `GenerateSettingsWrapper` (`:21-34`) pairs them with `"50%"` … `"100%"`. Beware:
  `NvidiaVibranceValueWrapper.NvapiDefaultLevel = 50` (`:7`) is a **percentage**, while
  `NvidiaDynamicVibranceProxy.NvapiDefaultLevel = 0` (`:121`) is a **raw level** — same name, different
  meaning, in two files. A genuine readability trap.
- `Find(value)` (`:36-43`) is `_settingsList.Find(x => x.Value == value) ?? Find(value + 1)`: raw levels
  missing from the list (`2,7,12,17,22,27,31,36,41,46,51,56,61`) round **up** to the next label.
  **`Find(v)` for `v > 63` recurses forever → `StackOverflowException`, which .NET cannot catch and
  which kills the process.** That is reachable from a settings file containing a level above 63
  ([§9.7](#97-the-cross-vendor-settings-hazard)).

Supporting types in `vibrance.GUI/NVIDIA/NvidiaTypes.cs`: `NvDisplayDvcInfo` (`:9-23`, sequential,
4 × 4 bytes, mirroring `NV_DISPLAY_DVC_INFO { version, currentLevel, minLevel, maxLevel }` — blittable,
so `ref` marshals as a direct pointer); `NvApiStatus` (`:25-56`, a full 100+ member transcription of the
NvAPI status enum that **nothing references**); `NvSystemType` (`:58-63` — `Unknown = 0, Laptop = 1,
Desktop = 2`). Useful `NvApiStatus` values when reading the DLL analysis:
`NvapiNvidiaDeviceNotFound = -6`, `NvapiEndEnumeration = -7`, `NvapiApiNotInitialized = -4`,
`NvapiNotSupported = -104`, `NvapiLibraryNotFound = -2`.

### 7.9 NVIDIA-specific failure modes

**Issue #138 — "extreme CPU usage when no dedicated GPU connected" — INFERENCE, well supported.**
There is a concrete infinite loop spanning the C#/native boundary:

- `EnumerateDisplayHandles()` (`:286-297`) is `for (int i = 0, displayHandle = 0; displayHandle != -1; i++)`
  and **terminates only on `-1`**.
- **VERIFIED (binary):** `enumerateNvidiaDisplayHandle` returns `-1` *only* for
  `NVAPI_END_ENUMERATION (-7)`; for any other non-zero status it returns `0`.
- On a machine with `nvapi.dll` present but no usable NVIDIA GPU, the expected statuses are
  `NVAPI_NVIDIA_DEVICE_NOT_FOUND (-6)` or `NVAPI_API_NOT_INITIALIZED (-4)` — **neither is `-7`**.
- Result: the loop never exits, hammering a P/Invoke into the driver as fast as it can while appending
  `0` to `displayHandles` — one core pinned at 100 % **plus unbounded memory growth**. It runs inside
  the proxy constructor, on the UI thread, so **the window never even appears**.

**Silent dead GUI.** If `initializeLibrary` returns `false` (broken NvAPI, missing entry points), the
constructor skips `InitializeProxy`, `isInitialized` stays `false`, and no dialog is shown at all
(`:144-153`). The user sees an open window with everything greyed out and the status label never
turning green.

**Hard crash on a bad DLL.** `Marshal.PrelinkAll` (`Program.cs:61`) resolves all 13 entry points
eagerly and is not inside a `try`/`catch`, so a missing or renamed export throws
`EntryPointNotFoundException` on the startup thread. `LoadUnmanagedLibraryFromResource` does not check
`LoadLibrary`'s result, and `File.WriteAllBytes` throws if the target file is locked (e.g. by another
instance).

**Issues #150 / #145 / #142 (hybrid and dual-GPU laptops) — INFERENCE.** Four code-grounded
mechanisms, strongest first:

1. **AMD iGPU + NVIDIA dGPU ⇒ refuses to start.** `GraphicsAdapter.cs:33-37` returns `Ambiguous`
   whenever *both* `atiadlxy.dll` and `nvapi.dll` exist in SysWOW64 — the normal state of a laptop with
   an AMD APU and an NVIDIA discrete GPU, or of any machine with both vendors' drivers installed. The
   user gets the "uninstall your old drivers with DDU" dialog and the app exits (`Program.cs:81-89`) —
   advice that is actively wrong for a hybrid machine.
2. **Optimus (Intel iGPU + NVIDIA dGPU) ⇒ silently does nothing.** On muxless Optimus the panel is
   driven by the Intel iGPU, so `NvAPI_GetAssociatedNvidiaDisplayHandle("\\.\DISPLAY1")` fails and the
   DLL returns `-1` (VERIFIED binary). `GetApplicationDisplayHandle` returns `-1` (`:299-315`), the
   guard `displayHandle != -1` at `:218` fails, and the handler exits without applying vibrance and
   without any message.
3. **dGPU powered down ⇒ "system type is unknown".** `InitializeProxy` calls `getGpuSystemType` for
   **every** non-zero handle (`:173-186`); one failure anywhere aborts init with that dialog.
4. **Multi-GPU reporting is index-0 only.** `getGpuName` and `getActiveOutputs` only look at
   `gpuHandles[0]` (VERIFIED binary), so on a two-GPU system the app describes whichever GPU NvAPI
   enumerates first — not necessarily the one driving the display.

**UNCERTAIN and important:** NVIDIA replaced DVC with a newer colour-settings API on modern driver
branches. Whether `NvAPI_SetDVCLevel` still works on current drivers is **not established anywhere in
this repository**, and nothing in the code tracks driver versions. Open issues #149 and #156 concern
recent driver branches; the unmerged `feature/add-color-settings` branch suggests the maintainer was
exploring the newer API.

---

## 8. The AMD path

### 8.1 The layer stack

```
AmdDynamicVibranceProxy            AMD/AmdDynamicVibranceProxy.cs — instance state (except _gameScreen)
   │  IAmdAdapter                  AMD/vendor/IAmdAdapter.cs (: IDisposable — never disposed)
   ▼
AmdAdapter32 / AmdAdapter64        AMD/vendor/AmdAdapter{32,64}.cs — identical twins (§8.4)
   │  static class Adl             adl32/ADL.cs | adl64/ADL.cs — lazy delegate resolution
   │  AdlCheckLibrary              adl{32,64}/ADLCheckLibrary.cs — "does this export exist?"
   ▼
AdlImport                          adl32 → atiadlxx.dll  |  adl64 → atiadlxy.dll
   │  static DllImports, CallingConvention.Cdecl
   ▼
AMD display driver → panel saturation
```

### 8.2 How ADL gets loaded, and the capability gate

**The app never `LoadLibrary`s ADL for real work.** Loading is implicit through static `[DllImport]`
declarations (`adl32/ADLImport.cs:15-43`, all `Cdecl`), so the CLR loads `atiadlxx.dll`/`atiadlxy.dll`
from the standard search path on the first call. The only explicit probing is:

- `GraphicsAdapterHelper` (`common/GraphicsAdapter.cs:21-22,53-63`) — `kernel32!LoadLibrary(dllName)`
  purely for presence detection; **the module handle is never freed**.
- `AdlCheckLibrary`'s constructor (`adl32/ADLCheckLibrary.cs:10-22`) —
  `ADL_Main_Control_IsFunctionValid(IntPtr.Zero, "ADL_Main_Control_Create") == 1`, then
  `GetModuleHandle` to cache the module. **Three `catch` clauses, all empty** (`:19-21`), so a
  `DllNotFoundException` here is indistinguishable from "driver present but old".
- Each `Adl.*` delegate property (`adl32/ADL.cs:77-203`) lazily asks
  `AdlCheckLibrary.IsFunctionValid("ADL_…")` **once** (guarded by a `_xxxCheck` bool) and assigns the
  corresponding `AdlImport` method if valid. **If the export is missing, the property stays `null`
  forever** and callers must null-check — most do, but not all (see [§8.3](#83-enumeration-and-the-saturation-write)).

If ADL is missing entirely, every `IsFunctionValid` returns `false`, every `Adl.*` property is `null`,
`IsAvailable()` returns `false`, `isInitialized` stays `false` — and the user gets the same **silent
dead GUI** as the NVIDIA failure path, with no dialog. Note that in that case `Init()` is never
reached, which is the only reason the unguarded `Adl.AdlMainControlCreate` call at
`AmdAdapter32.cs:20` is safe today.

Detection carries a matching assumption: it expects `atiadlxx.dll` on 32-bit Windows and
`atiadlxy.dll` on 64-bit Windows to exist under `SpecialFolder.SystemX86`
(`common/GraphicsAdapter.cs:33-45`). **A driver package that ships only one of those names breaks
detection outright** — the app reports `Unknown` and exits.

### 8.3 Enumeration and the saturation write

`AmdAdapter32.Init()` (`AMD/vendor/AmdAdapter32.cs:13-102`):

1. `Adl.AdlMainControlCreate(Adl.AdlMainMemoryAlloc, 1)` (`:20`) — `1` means "connected adapters only".
   **Not null-checked, return value ignored** — while the very next statement null-checks a sibling
   delegate (`:22`). Safe today only because `Init()` is always preceded by `IsAvailable()`.
2. `Adl.AdlAdapterNumberOfAdaptersGet(ref numberOfAdapters)` (`:22-25`).
3. **`Adl.AdlMainControlCreate(...)` a second time** (`:27`) — almost certainly copy-paste, and
   unbalanced against the single `Destroy`.
4. Allocate an `AdlAdapterInfoArray` (40 × `AdlAdapterInfo`, `ADL.cs:51`) with `AllocCoTaskMem` +
   `StructureToPtr` (`:35-37`).
5. `Adl.AdlAdapterAdapterInfoGet(adapterBuffer, size)` (`:39`), marshal back with `PtrToStructure`
   (`:42`).
6. Per adapter: `Adl.AdlAdapterActiveGet(adapterIndex, ref isActive)` (`:53`) — **`isActive` is written
   and never read**; the loop branches on the ADL return code instead (`:56`).
7. `Adl.AdlDisplayDisplayInfoGet(adapterIndex, ref numberOfDisplays, out displayBuffer, 1)` (`:65`,
   `1` = force re-detect), then a manual pointer walk
   `displayBuffer.ToInt64() + j * sizeof(AdlDisplayInfo)` (`:71`).
8. Skip displays whose `DisplayID.DisplayLogicalAdapterIndex == -1` (unmapped) (`:79-82`). **The
   `DisplayInfoMask`/`DisplayInfoValue` connected/mapped bits are never examined.**
9. Retain `(DisplayInfo, AdapterInfo, adapterIndex)` triples (`:84-89`) and record the display buffer
   for a later free that never happens (`:93`).

The actual write, `SetSaturationOnDisplay(level, displayName)` (`:127-142`), walks those triples and,
when `adapterIndex == DisplayID.DisplayLogicalAdapterIndex` **and**
(`adlAdapterInfo.DisplayName == displayName` **or** `displayName == null`), calls:

```csharp
Adl.AdlDisplayColorSet(adapterIndex,
                       adlDisplayInfo.DisplayID.DisplayLogicalIndex,
                       Adl.AdlDisplayColorSaturation /* 1<<2 = 4 */,
                       vibranceLevel);
```

**The return value is ignored**, so an ADL failure is completely invisible to the app and to the user.
`SetSaturationOnAllDisplays(level)` is simply `SetSaturationOnDisplay(level, null)` (`:122-125`).

The ADL functions actually bound:

| ADL export | Bound at | Delegate | Called from | Note |
|---|---|---|---|---|
| `ADL_Main_Control_Create` | `ADLImport.cs:16` | `ADL.cs:77-91` | `AmdAdapter32.cs:20,27,108` | **twice per `Init`**, once per `IsAvailable` |
| `ADL_Main_Control_Destroy` | `:19` | `ADL.cs:93-107` | `:112`, `:193`, `ADLCheckLibrary.cs:28` | also called from a **finalizer** |
| `ADL_Main_Control_IsFunctionValid` | `:22` | — (direct) | `ADLCheckLibrary.cs:14,37` | the capability gate |
| `ADL_Main_Control_GetProcAddress` | `:25` | — | `ADLCheckLibrary.cs:50` | wrapper exists, **never called** |
| `ADL_Adapter_NumberOfAdapters_Get` | `:28` | `ADL.cs:109-123` | `:24` | |
| `ADL_Adapter_AdapterInfo_Get` | `:31` | `ADL.cs:125-139` | `:39` | |
| `ADL_Adapter_Active_Get` | `:34` | `ADL.cs:141-155` | `:53` | result unused |
| `ADL_Display_DisplayInfo_Get` | `:37` | `ADL.cs:157-171` | `:65` | |
| **`ADL_Display_Color_Set`** | `:40` | `ADL.cs:173-187` | `:135` | **the actual saturation write** |
| `ADL_Display_Color_Get` | `:43` | `ADL.cs:189-203` | — | **dead** — and it is exactly the call that would return the driver's real current/default/min/max/step for saturation |

Memory handling: `Adl.AdlMainMemoryAlloc` (`ADL.cs:61-67`) wraps `Marshal.AllocCoTaskMem` and is handed
to `ADL_Main_Control_Create` so ADL can allocate the display-info array on the caller's heap. The
delegate is a plain static field with **no `GCHandle`** — it survives only because it is rooted in a
static. The matching `ADL_Main_Memory_Free` (`:69-75`) exists and is **never called**; the adapters free
ADL memory directly with `Marshal.FreeCoTaskMem` inside `Disposer.Dispose()` (`AmdAdapter32.cs:176-195`)
— correctly paired with `AllocCoTaskMem`, but bypassing the intended callback.

Data structures (`adl32/ADLAdapterInfo.cs`, `ADLDisplayInfo.cs`, `ADLDisplayID.cs`) mirror the ADL
structs, `Sequential`, with six `ByValTStr` fields of `AdlMaxPath = 256` in `ADLAdapterInfo.cs` (plus two more in `ADLDisplayInfo.cs`, eight in total). **`CharSet` is not
specified**, so `ByValTStr` defaults to ANSI for a sequential struct — correct for ADL's `char[]`
fields, but implicit. All are `internal struct` inside a `public` API surface;
`AmdAdapter32.SetSaturationOnDisplay` gets away with it only because it uses them inside a lambda whose
types are inferred. Signatures use untyped placeholder parameter names (`int a, int b, int c, int d` —
`ADLImport.cs:40`, `Delegates.cs:14`), so the meaning of
`ADL_Display_Color_Set(adapterIndex, displayIndex, colorType, value)` is discoverable only at the call
site.

### 8.4 The adl32/adl64 duplication, and why the names are backwards

Every file pair was diffed. **The complete set of differences is:**

| File pair | Difference |
|---|---|
| `ADL.cs` | **namespace line only** (`:23`) |
| `ADLAdapterInfo.cs`, `ADLAdapterInfoArray.cs`, `ADLCheckLibrary.cs`, `ADLDisplayID.cs`, `ADLDisplayInfo.cs`, `Delegates.cs` | namespace only (`:3`) |
| `ADLImport.cs` | namespace (`:4`) **plus one string**: adl32 `"atiadlxx.dll"` vs adl64 `"atiadlxy.dll"` (`:8`) |
| `AmdAdapter32.cs` vs `AmdAdapter64.cs` | the `using` line (`:4`) and the class name (`:8`). **Nothing else — 195 of 197 lines identical.** |

There is no difference in structure packing, no `[StructLayout(Pack=…)]`, no `IntPtr`-size handling, no
differing entry-point names, no `#if` conditionals. **940 lines of source exist to carry one string
literal.**

**And the names mean the opposite of what they say.** The process is *always* 32-bit
([§3.3](#33-the-x86-rule-and-why-it-is-not-negotiable)), so `IntPtr` never changes size. The 32/64
distinction here is about **OS bitness**, i.e. which ADL library a 32-bit process is able to load:

- `common/GraphicsAdapter.cs:25-27` picks the file name by `Environment.Is64BitOperatingSystem` →
  64-bit OS ⇒ `adl64.AdlImport.AtiadlFileName` = **`atiadlxy.dll`**; 32-bit OS ⇒ `adl32` =
  **`atiadlxx.dll`**.
- This follows AMD's own ADL sample guidance: a 32-bit caller on 64-bit Windows cannot load the native
  `atiadlxx.dll` and must use `atiadlxy.dll`. **So `adl64` means "we are running on a 64-bit OS", and
  it loads the *32-bit* ADL library. The folder names say the opposite of what they do.**
- The history corroborates the confusion: commit `f1e748d` ("amd: handle also 32bit systems") renamed
  the original `adl/` to `adl32/` and cloned it to `adl64/`, and in that very commit the newly created
  `adl32/ADLImport.cs` **still declared namespace `…adl64` and `atiadlxy.dll`**. The split was muddled
  from birth.

**Verdict: copy-paste debt with a small real excuse.** The excuse is that `[DllImport("…")]` requires a
compile-time constant library name, so you genuinely cannot swap the two names with a variable using
static P/Invoke. Fixes, in increasing order of effort:

1. `SetDllDirectory` plus a single binding, letting the loader resolve one name (the app already calls
   `SetDllDirectory`).
2. **`LoadLibrary(name)` + `Marshal.GetDelegateForFunctionPointer`** — exactly the shape ADL is designed
   for, and the code is already 80 % of the way there: `Delegates.cs` defines every signature and
   `AdlCheckLibrary.GetProcAddress` (`:45-53`, currently dead) already wraps
   `ADL_Main_Control_GetProcAddress`.
3. One `AmdAdapter<TAdl>` generic, or a single class taking an `IAdlBinding`.

Cost of the status quo: **every ADL bug fix must be applied twice**, in near-identical files, in
namespaces whose names are backwards. Two independent selection sites do the picking, both spelled
`Environment.Is64BitOperatingSystem ? new AmdAdapter64() : new AmdAdapter32()`
(`common/GraphicsAdapter.cs:40` for a throwaway detection instance, `Program.cs:43-45` for the real
one).

### 8.5 AMD value semantics

| Property | Value | Source |
|---|---|---|
| Raw ADL range used | `0 … 300` | `Program.cs:48-49` |
| Neutral / default | `100` | `Program.cs:47,50`; `SettingsController.cs:108` |
| Slider → driver | **identity** — passed straight to `ADL_Display_Color_Set(…, SATURATION, value)` | `AmdAdapter32.cs:135-139` |
| Slider → label | `x => x.ToString()` — a raw number, no unit | `Program.cs:51` |
| Clamping / validation | **none anywhere in the app** | — |
| Driver-reported range | **never queried** — `ADL_Display_Color_Get` is bound and dead | `ADL.cs:189-203` |

**UNCERTAIN:** the `0..300` bound is hardcoded with a `// todo` beside it
(`SettingsController.cs:107-109`) and is never reconciled with what the driver actually reports. AMD's
own saturation control is commonly `0..200` with `100` neutral. Whether values above the driver's
maximum are clamped, rejected or applied is unverified — and since `AdlDisplayColorSet`'s return code is
discarded, **the app cannot tell either way**.

### 8.6 Resource management on the AMD path

`IAmdAdapter` derives from `IDisposable` (`AMD/vendor/IAmdAdapter.cs:8`) and `AmdAdapter32` implements a
proper `Disposer` that calls `ADL_Main_Control_Destroy` and frees both the CoTaskMem adapter buffer and
each ADL-allocated display buffer (`AmdAdapter32.cs:176-195`). **Nothing ever calls it.**
`AmdDynamicVibranceProxy.UnloadLibraryEx()` unhooks the WinEvent hook and `return true;`
(`AmdDynamicVibranceProxy.cs:82-86`); neither proxy implements `IDisposable`. The declared disposal
chain is simply not wired up.

The measurable results per run: **two unmatched `ADL_Main_Control_Create` calls** (`:20` and `:27`, plus
one more in `IsAvailable` at `:108` that *is* matched at `:112`), one leaked `AllocCoTaskMem` adapter
buffer, and one leaked ADL display buffer per adapter. There is also a **finalizer that calls into the
vendor driver**: `AdlCheckLibrary`'s finalizer (`adl32/ADLCheckLibrary.cs:24-30`) calls
`ADL_Main_Control_Destroy()` during shutdown — a hang/crash risk on the finalizer thread — and it reads
the **static** `_adlCheckLibrary._adlLibrary` rather than `this._adlLibrary` (`:26`).

---

## 9. Settings & persistence

### 9.1 Where everything lives

| What | Path | Format |
|---|---|---|
| Scalar settings | `%APPDATA%\vibranceGUI\vibranceGUI.ini` (`common/SettingsController.cs:36`) | Win32 INI via `Get`/`WritePrivateProfileString` |
| Watched applications | `%APPDATA%\vibranceGUI\applicationData.xml` (`SettingsController.cs:37`) | `XmlSerializer` of `List<ApplicationSetting>` |
| Autostart | `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, value `vibranceGUI` (`common/RegistryController.cs:8`, `VibranceGUI.cs:25`) | `REG_SZ` = `"<exe path>" -minimized` |
| Extracted native DLL | `%APPDATA%\vibranceGUI\vibranceDLL.dll` (`AMD/vendor/utils/CommonUtils.cs:29`) | rewritten on every NVIDIA launch, never deleted |
| Exception log | `%APPDATA%\vibranceGUI.log` — note: **not** inside the `vibranceGUI` folder (`VibranceGUI.cs:336`) | append-only text |
| Dead log path | `vibranceGUI_log.txt`, relative to the working directory (`VibranceGUI.cs:353`) | `Log(string)` is never called |

Nothing is stored per monitor, per vendor or per version. There is no schema version, no migration
code and no legacy-key handling anywhere: old files either parse or they do not.

### 9.2 File formats

**`vibranceGUI.ini`** — one section, `[Settings]` (`SettingsController.cs:30`):

| Key | Constant | Written at | Read at | Meaning |
|---|---|---|---|---|
| `inactiveValue` | `SzKeyNameInactive` (`:31`) | `:47` | `:124-129` | the Windows/desktop level, as an integer string |
| `refreshRate` | `SzKeyNameRefreshRate` (`:32`) | never | `:131-137` | **read into a buffer and never used** — dead |
| `affectPrimaryMonitorOnly` | `:33` | `:48` | `:139-145`, default `"false"` | `bool.Parse`d |
| `neverSwitchResolution` | `:34` | `:49` | `:147-153`, default `"false"` | `bool.Parse`d |

```ini
[Settings]
inactiveValue=0
affectPrimaryMonitorOnly=false
neverSwitchResolution=false
```

**`applicationData.xml`** — `XmlSerializer(typeof(List<ApplicationSetting>))`, producing a root
`<ArrayOfApplicationSetting>` with the public properties in declaration order
(`common/ApplicationSetting.cs:8-13`):

```xml
<ArrayOfApplicationSetting xmlns:xsi="..." xmlns:xsd="...">
  <ApplicationSetting>
    <Name>csgo</Name>
    <FileName>C:\Games\csgo\csgo.exe</FileName>
    <IngameLevel>50</IngameLevel>
    <IsResolutionChangeNeeded>false</IsResolutionChangeNeeded>
    <ResolutionSettings>
      <DmPelsWidth>1920</DmPelsWidth>
      <DmPelsHeight>1080</DmPelsHeight>
      <DmBitsPerPel>32</DmBitsPerPel>
      <DmDisplayFrequency>144</DmDisplayFrequency>
      <DmDisplayFixedOutput>0</DmDisplayFixedOutput>
    </ResolutionSettings>
  </ApplicationSetting>
</ArrayOfApplicationSetting>
```

`ResolutionSettings` carries `[XmlElement(IsNullable = true)]` (`ApplicationSetting.cs:12`). The
property names, order and nullability are taken verbatim from the code; the surrounding element names
follow the default `XmlSerializer` contract (**not** captured from a live file). Note the two
identity-ish fields: `Name` is the runtime match key, `FileName` is the de-duplication and equality key
([§11.1](#111-applicationsetting)).

### 9.3 Write and read paths

**Write** — `SettingsController.SetVibranceSettings` (`:40-67`): `PrepareFile()`, then three
`WritePrivateProfileString` calls, then `XmlWriter.Create` (truncating), `Serialize`, `Flush`, `Close`.

- Any exception in the XML block returns `false` (`:61-64`) **and leaves a truncated or empty
  `applicationData.xml`** — the writer is not in a `using`, so a mid-serialise failure both leaks the
  writer and corrupts the file.
- `PrepareFile()` (`:81-94`) creates the INI as a zero-byte file with `new StreamWriter(_fileName); sw.Close();`.
  **It does not create the parent directory** — that happens only as a side effect of
  `CommonUtils.GetVibrance_GUI_AppDataPath()` in `Program.cs:36`. The `StreamWriter` is not disposed on
  the exception path, and an `IOException` from a missing directory propagates to the caller.
- The `bool` return is noise ([§5.4](#54-where-the-abstractions-leak)), and the caller ignores it
  (`VibranceGUI.cs:406-411`).

**Read** — `ReadVibranceSettings` (`:96-184`):

1. Pick `defaultLevel`/`maxLevel` from the vendor: NVIDIA `0`/`63` from the proxy constants
   (`:100-104`); AMD **hardcoded `100`/`300` with a `// todo`** (`:105-110`).
2. `if (!IsFileExisting(ini) || !IsFileExisting(xml))` → return all defaults (`:112-119`). Note the
   **logical OR**: deleting only `applicationData.xml` also resets the vibrance level and both
   checkboxes.
3. Read the four INI values into 1024-char buffers.
4. `int.Parse` / `bool.Parse` inside a `try`; **any** parse failure yields all defaults, an empty list
   and an early return (`:155-168`).
5. Clamp the Windows level ([§9.4](#94-value-clamping-on-load)).
6. Deserialise the XML; any exception yields an empty list (`:180-183`). The `XmlReader` is not in a
   `using` and leaks on that path.

**Corrupt or missing files are never fatal and never reported.** The program silently degrades to
defaults. If a user reports "my settings keep resetting", this is where to look.

`ReadVibranceSettings` on the form side (`VibranceGUI.cs:363-400`) additionally acts as a garbage
collector: entries whose `FileName` no longer exists on disk are dropped from `_applicationSettings`
(`:379-385`) — silently, and **the pruned list is not written back**, so the XML keeps the stale entry
until the next save.

### 9.4 Value clamping on load

```csharp
// SettingsController.cs:170-171
if (vibranceWindowsLevel < defaultLevel || vibranceWindowsLevel > maxLevel)
    vibranceWindowsLevel = defaultLevel;
```

**The lower bound is `defaultLevel`, not a minimum.** For NVIDIA `defaultLevel == 0`, so this is
harmless. **For AMD `defaultLevel == 100`, so any saved Windows level below 100 — i.e. any
desaturation — is silently reset to 100 on every start**, even though the slider's minimum is `0`
(`Program.cs:48`). AMD users cannot persist a below-neutral desktop saturation.

**Per-application `IngameLevel` values are not clamped at all** (`:173-183`), which is what makes
[§9.7](#97-the-cross-vendor-settings-hazard) dangerous.

### 9.5 Autostart

`checkBoxAutostart_CheckedChanged` (`VibranceGUI.cs:258-289`) creates a fresh `RegistryController` per
invocation and builds `pathToExe = "\"" + Application.ExecutablePath + "\" -minimized"` (`:263`):

| Condition | Action | Balloon text |
|---|---|---|
| checked, not registered | `RegisterProgram` | "Registered to Autostart!" / "Registering to Autostart failed!" |
| checked, registered, path changed | `RegisterProgram` | "Updated Autostart Path!" / "Updating Autostart Path failed!" |
| checked, registered, path unchanged | `return` | (no balloon) |
| unchecked | `UnregisterProgram` | "Unregistered from Autostart!" / "Unregistering from Autostart failed!" |

Everything is under `HKCU`, so no elevation is needed. Two subtleties:

- `CheckedChanged` also fires on **programmatic** assignment, and `ReadVibranceSettings` assigns
  `checkBoxAutostart.Checked` during startup (`:366`) — so a user who moved the executable sees an
  unexplained "Updated Autostart Path!" balloon on launch. Self-healing, but confusing. The same
  re-entrancy applies to the other two checkboxes (`:377-378`), which schedules a settings save five
  seconds after every launch.
- `RegistryController` reuses one `RegistryKey` **field** across all four methods
  (`RegistryController.cs:10`) and opens the key writable even for read-only checks (`:59`). Two
  null-dereference defects live here — see [§12.2](#122-crashes-and-data-loss).

### 9.6 The debounced save

Settings are saved through a five-second debounce, not on change and not on exit:

```csharp
// VibranceGUI.cs:181-185
private void settingsBackgroundWorker_DoWork(object sender, DoWorkEventArgs e)
{
    Thread.Sleep(5000);
    ForceSaveVibranceSettings();
}
```

It is kicked off by the desktop trackbar (`:175-178`) and by the two checkboxes (`:238-241`,
`:252-255`), each guarded by `if (!IsBusy)`. `ForceSaveVibranceSettings` (`:187-199`) marshals three
control reads back to the UI thread with `this.Invoke` and then calls `SaveVibranceSettings`. Adding,
editing or removing a watched application saves immediately instead (`:520`, `:540`).

Two failure modes follow directly:

- **Quit within five seconds of moving the slider and the change is lost** — `CleanUp` does not save.
- If the form is disposed during the sleep, `this.Invoke` throws on the worker thread;
  `BackgroundWorker` funnels it into `RunWorkerCompleted`, whose handler is **empty** and never
  inspects `e.Error` (`:214-216`). The failure vanishes.

### 9.7 The cross-vendor settings hazard

The two settings files are **not vendor-tagged**, and they live in a shared per-user location. Move a
profile between an AMD and an NVIDIA machine (or switch GPU vendor on the same machine) and:

- an AMD-written `IngameLevel` of up to `300`, loaded on NVIDIA, hits
  `trackBarIngameLevel.Value = setting.IngameLevel` with `Maximum = 63` (`VibranceSettings.cs:30`) →
  **`ArgumentOutOfRangeException` when the per-game dialog is opened**;
- the same value passed to `NvidiaVibranceValueWrapper.Find(300)` recurses forever →
  **`StackOverflowException`, which .NET cannot catch** ([§7.8](#78-nvidia-value-semantics));
- conversely, NVIDIA levels (`0..63`) read on AMD are all far below neutral `100`, so **every game
  desaturates**.

---

## 10. UI surface

Three forms, all WinForms with designer files. Data moves between them by constructor parameters and
one getter — there is no view-model, no binding and no messaging.

### 10.1 `VibranceGUI` — the main window

`ClientSize 419×524`, `FixedSingle`, no maximise box, title `vibranceGUI` — to which `Program.cs:95`
appends `" (NVIDIA, 2.3.1.1)"` or `" (AMD, …)"`.

| Region | Controls |
|---|---|
| Header | `labelTwitter`, `linkLabelTwitter`, `labelPaypal`, `buttonPaypal` (`VibranceGUI.Designer.cs:205-250`) |
| `groupBox1` "Settings" | `checkBoxAutostart` "Autostart vibranceGUI", `checkBoxPrimaryMonitorOnly` "Affect Primary Monitor only", `checkBoxNeverChangeResolutions` "Never change resolutions", and a nested "Windows Vibrance Level" group with `trackBarWindowsLevel` + `labelWindowsLevel` (`:120-185`) |
| `groupBox5` "Program Settings" | `buttonProcessExplorer` (labelled **"Add"**), `buttonAddProgram` (labelled **"Add manually"**), `buttonRemoveProgram`, and `listApplications` — a large-icon `ListView`, 48×48 with custom spacing (`:259-309`) |
| Status area | `observerStatusLabel` (the static string "Observer status: " — **never updated**), `statusLabel` ("Initializing…" → "Running!" green → "Closing…" red) (`:187-203`) |
| Non-visual | `notifyIcon` + context menu (Twitter, Exit), `toolTip`, `backgroundWorker`, `settingsBackgroundWorker` (`:33-52`) |

`VibranceGUI` is a 556-line god-class: the form, the composition root for settings and registry access,
the owner of the watched-application list and its icons, the tray icon host, a static logger, and a
`SendMessage` P/Invoke for `LVM_SETICONSPACING` (`VibranceGUI.cs:489-502`). It does **not** apply
vibrance — that happens only inside the proxy's event handler.

Behavioural details worth knowing:

- **The tray icon is always visible** (`Designer.cs:73`), even when the window is open. Minimising
  hides the window (`VibranceGUI.cs:105-114`; the balloon-tip lines there are commented out).
- `notifyIcon_MouseClick` (`:218-228`) restores the window without filtering `e.Button`, so a
  **right-click both opens the context menu and restores the window**.
- The `ListView` is a hand-maintained parallel projection of `_applicationSettings`: `Text` = display
  name, `Tag` = full path (the join key), `ImageIndex` = the index assigned at insert time (`:394`,
  `:468`). Removal manually decrements the `ImageIndex` of every following item (`:533-534`). There is
  no back-pointer from `ListViewItem` to `ApplicationSetting`.
- Balloon tips double as the autostart feedback channel (`:266-288`).

### 10.2 `VibranceSettings` — the per-game modal dialog

`ClientSize 270×266`, `CenterParent`, `FixedSingle`. Contains the game icon, a title ("Settings for
\"csgo\""), an "Ingame Vibrance Level" trackbar with its label, and an "Ingame Resolution" group
(a "For (Borderless) Windowed Mode players only!" note, the "Change Resolution when Ingame" checkbox and
the mode combo), plus Save.

Data flow, in full:

```
VibranceGUI.listApplications_DoubleClick            (VibranceGUI.cs:504-527)
  new VibranceSettings(_v, _minTrackBarValue, _maxTrackBarValue, _defaultIngameValue,
                       selectedItem /* ListViewItem */, actualSetting /* may be null */,
                       _supportedResolutionList, _resolveLabelLevel)
  ShowDialog()
      └─ buttonSave_Click  ->  DialogResult.OK                      (VibranceSettings.cs:44-48)
  if OK:
      newSetting = settingsWindow.GetApplicationSetting()            (VibranceSettings.cs:50-54)
                 = new ApplicationSetting(_sender.Text, _sender.Tag.ToString(),
                       trackBarIngameLevel.Value,
                       (ResolutionModeWrapper)cBoxResolution.SelectedItem,
                       checkBoxResolution.Checked)
      remove any existing entry with the same FileName, add newSetting, ForceSaveVibranceSettings()
  else if the setting was new:
      roll back the ListViewItem that was just added                 (VibranceGUI.cs:522-525)
```

Two traps live here. The dialog's icon comes from `_sender.ListView.LargeImageList.Images[_sender.ImageIndex]`
(`VibranceSettings.cs:24`), so the `ImageIndex` bookkeeping in the main form is load-bearing. And the
resolution combo is always filled from `_supportedResolutionList`, which is **only the primary
monitor's mode list** (`VibranceGUI.cs:61-64`) — for a game on a secondary monitor you can only pick
modes the primary supports, and if the secondary does not support the chosen mode the `Contains` guard
in the proxy silently skips the change with no feedback.

`trackBarIngameLevel_Scroll` (`:38-42`) calls `_v.SetVibranceIngameLevel(...)`, which does nothing
([§2.2](#22-five-facts-that-will-bite-you-first)), and updates the label.

### 10.3 `ProcessExplorer` — the running-process picker

`ClientSize 639×289`, `CenterScreen`, opened **non-modally** from `VibranceGUI.buttonProcessExplorer_Click`
(`VibranceGUI.cs:551-555`) — clicking "Add" repeatedly opens multiple explorer windows. The parent is
stored as a plain `Form` and **downcast** to `VibranceGUI` when used (`ProcessExplorer.cs:21,93`).

A background worker (`:40-63`) walks `Process.GetProcesses()`, keeps processes that have a
`MainWindowHandle` and are not vibranceGUI itself, resolves the executable path via
`psapi!GetModuleFileNameEx` (`:65-81`), and reports each entry back to the UI thread, which adds an icon
and a `ListViewItem` whose `Tag` is the whole `ProcessExplorerEntry` (`:112-124`). Double-clicking an
entry hides the window, calls `((VibranceGUI)vibranceGui).AddProgramExtern(entry)` (`:93`) — which
marshals to the UI thread (`VibranceGUI.cs:437-450`) and opens the per-game dialog — and closes.

Elevated processes throw when their handle is opened; the exception is caught and logged (`:57-60`), so
**elevated games simply do not appear in the list** unless vibranceGUI itself is elevated.

### 10.4 How the three forms exchange data

```mermaid
flowchart LR
    PE["ProcessExplorer<br/>(non-modal)"] -->|"AddProgramExtern(entry)<br/>marshalled to UI thread"| VG
    VG["VibranceGUI<br/>owns List&lt;ApplicationSetting&gt;<br/>+ the ListView projection"] -->|"ctor args:<br/>proxy, ranges, ListViewItem,<br/>existing setting, resolutions"| VS["VibranceSettings<br/>(modal)"]
    VS -->|"DialogResult.OK<br/>+ GetApplicationSetting()"| VG
    VG -->|"SetApplicationSettings(list)<br/>— by reference, once"| PX["Vendor proxy"]
    VG -->|"ForceSaveVibranceSettings()"| SC["SettingsController<br/>INI + XML"]
```

The single most important thing in this picture: **`_applicationSettings` is one `List<T>` instance
shared by reference between the form and the proxy** (`VibranceGUI.cs:73-74`, re-pointed once at
`:145`). Every add and remove the UI performs is immediately visible to the proxy's event handler. Do
not replace that list with a new instance without calling `SetApplicationSettings` again.

---

## 11. Data models reference

### 11.1 `ApplicationSetting`

`vibrance.GUI/common/ApplicationSetting.cs` — public, XML-serialised, one watched game.

| Member | Type | Meaning | Notes |
|---|---|---|---|
| `Name` | `string` | **The runtime match key**, compared case-insensitively against `Process.ProcessName` | sourced from `ListViewItem.Text`, i.e. `Path.GetFileNameWithoutExtension(path)` (`VibranceGUI.cs:467`) — e.g. `csgo` |
| `FileName` | `string` | Full path to the executable | used for de-duplication, icon extraction, existence pruning and equality — **never for matching** |
| `IngameLevel` | `int` | Level to apply while this app is in front | NVIDIA `0..63`, AMD `0..300`; **not validated on load** |
| `IsResolutionChangeNeeded` | `bool` | Whether to switch resolution for this app | |
| `ResolutionSettings` | `ResolutionModeWrapper` | Target display mode; `[XmlElement(IsNullable = true)]` | may be `null`; nulls are tolerated downstream |

Two constructors: parameterless (required by `XmlSerializer`, `:15`) and the five-argument one
(`:17-24`). **Equality is by `FileName` only** (`:26-34`, added in commit `cd42c3c`), with
`GetHashCode()` returning `FileName.GetHashCode()` (`:36-39`) — consistent, ordinal, case-**sensitive**,
and it will throw `NullReferenceException` if `FileName` is null (reachable from hand-edited XML).
**Remember the asymmetry: equality and de-duplication use `FileName`, but the runtime match uses
`Name`.**

### 11.2 `VibranceInfo`

`vibrance.GUI/common/Definitions.cs:7-21` — `[StructLayout(LayoutKind.Sequential)] public struct`. The
attribute is a leftover; the struct holds a `String` and a `List<int>` and is never marshalled.

| Field | Type | Alive? |
|---|---|---|
| `isInitialized` | `bool` | **yes** — the shell's only health signal (`VibranceGUI.cs:139,155,321`) |
| `activeOutput` | `int` | written once (`NvidiaDynamicVibranceProxy.cs:190`), never read |
| `defaultHandle` | `int` | live — an NVIDIA display handle |
| `userVibranceSettingDefault` | `int` | live — the desktop level used on revert |
| `userVibranceSettingActive` | `int` | **dead** — written by `SetVibranceIngameLevel`, never read |
| `szGpuName` | `String` | written once (`:194`), never displayed |
| `shouldRun` | `bool` | **dead** — polling-loop fossil |
| `sleepInterval` | `int` | **dead** — polling-loop fossil |
| `displayHandles` | `List<int>` | live for NVIDIA multi-monitor; **null on the AMD path** |
| `affectPrimaryMonitorOnly` | `bool` | live |
| `neverChangeResolution` | `bool` | live |

It is a **struct returned by value** from `GetVibranceInfo()`, so callers get a snapshot; mutating the
returned copy is silently useless. It is also NVIDIA-shaped — four of its fields exist only for the
NVIDIA path.

### 11.3 `ResolutionModeWrapper`

`vibrance.GUI/common/ResolutionModeWrapper.cs` — a serialisable subset of `DEVMODE`: `DmPelsWidth`,
`DmPelsHeight`, `DmBitsPerPel`, `DmDisplayFrequency`, `DmDisplayFixedOutput` (all `uint`), plus a
parameterless constructor for XML and one taking a `Devmode` (`:18-25`).

- `ToString()` (`:27-31`) uses **deliberately shuffled format indices** —
  `"{0} x {1} @ {3} hz ({2} bit, {4})"`, e.g. `1920 x 1080 @ 144 hz (32 bit, Default)`. The last token
  is `Enum.GetName(typeof(Dmdfo), DmDisplayFixedOutput)`, which returns `null` for out-of-range values.
  This is the text shown in the settings combo box.
- `Equals` (`:33-56`) accepts **either** a `ResolutionModeWrapper` **or** a raw `Devmode` (converting on
  the fly) and compares all five fields; `GetHashCode()` (`:58-67`) is overridden to match.
- `MatchesAchievedMode(Devmode)` (`:84-90`, added on `work/resolution-change`) — a **second**,
  narrower comparison used only by `ResolutionHelper.IsResolutionChangeNeeded`/`ChangeResolutionEx`.
  Compares only the four driver-controlled fields, deliberately excluding `DmDisplayFixedOutput` —
  see **D59** in [§12.1](#121-the-defects-that-explain-real-upstream-issues) and
  [§6.4](#64-the-optional-resolution-switch) for why `Equals` itself was left untouched (the settings
  combo box and the `applicationData.xml` round trip need all five fields to match, `Equals`'s
  original job).

### 11.4 `GraphicsAdapter`

`vibrance.GUI/common/GraphicsAdapter.cs:10-16` — `Unknown = 0, Nvidia = 1, Amd = 2, Ambiguous = 3`.
`Ambiguous` means both vendor driver DLLs were found in `SysWOW64`; it is a fatal condition for
startup, not a preference to resolve ([§6.1](#61-startup)). The companion `GraphicsAdapterHelper` lives
in the same file (`:18-64`).

### 11.5 `ProcessExplorerEntry`

`vibrance.GUI/common/ProcessExplorerEntry.cs` — `Path` (full executable path), `Icon`
(`System.Drawing.Icon`) and `ProcessName`. Two constructors: one taking a `Process` and reading
`.ProcessName` (`:19-24`, used by `ProcessExplorer.cs:53`) and one taking the name directly (`:26-31`,
used by `VibranceGUI.cs:431`, where the name is `Path.GetFileNameWithoutExtension`). No equality, and
no `IDisposable` despite owning an `Icon`.

### 11.6 `WinEventHookEventArgs`

`vibrance.GUI/common/WinEventHookEventArgs.cs` — `ProcessId`, `Process`, `WindowText`, `ProcessName`,
`MainWindowTitle`, `Handle`. **Only `ProcessName` and `Handle` are consumed.** `Process` is never even
assigned; `WindowText` and `MainWindowTitle` are assigned and never read.

### 11.7 Win32 types in `ResolutionHelper.cs`

`Devmode` (`:469-572`) is a faithful `DEVMODEA` — `[StructLayout(Sequential, CharSet = Ansi)]` with two
32-character `ByValTStr` name fields and the documented field order; `Pointl` (`:574-581`) carries
`dmPosition`. The enums match `winuser.h`: `DispChange` (`:450-459`;
`Successful=0, Restart=1, Failed=-1, Badmode=-2, Notupdated=-3, Badflags=-4, Badparam=-5`), `Dmdfo`
(`:461-465`; `Default=0, Stretch=1, Center=2`) and `ChangeDisplaySettingsFlags` (`:583-598`). None of
the `DllImport`s specifies a `CharSet`, so they bind to the ANSI entry points — consistent with the
`Devmode` declaration.

Added on `work/resolution-change`, alongside the seam described in
[§6.4](#64-the-optional-resolution-switch):

- `DevmodeFields` (`:604-614`) — the small, named subset of `DM_*` `dmFields` bits
  `ApplyTargetFields` cares about (`DmPosition`, `DmPelsWidth`, `DmPelsHeight`, `DmBitsPerPel`,
  `DmDisplayFrequency`, `DmDisplayFixedOutput`, `DmDisplayOrientation`), values unchanged from
  `winuser.h`.
- `internal interface IDisplayModeDevice` (`:16-26`) — the device seam; `RealDisplayModeDevice`
  (`:426-448`) is the only production implementation.
- `public enum ResolutionChangeResult` (`ResolutionHelper.cs:62-68`, **nested inside**
  `ResolutionHelper` — callers write `ResolutionHelper.ResolutionChangeResult.Applied`) —
  `Applied`, `AlreadyMatching`, `Failed`, `Suppressed`.
- `public class ResolutionFailureEventArgs : EventArgs` (`:34-49`, a **sibling** top-level type, not
  nested) — `DeviceName`, `Target`, `FailureCode`, `IsRevert`, `IsGivingUp`.

---

## 12. Known defects & risk register

Every item below carries a `file:line` citation so you can jump straight to the code and judge for
yourself. Items are numbered **D1…D59** for reference in issues and commit messages; the numbering is
this document's, not the project's.

**Read the uncertainty markers literally.** Items marked **INFERENCE** are mechanisms derived from the
code that plausibly produce a reported symptom; they have **not** been reproduced or confirmed at
runtime. Items marked **UNCERTAIN** are questions the code cannot answer. Do not repeat either as
established fact in a bug report or a commit message.

### 12.1 The defects that explain real upstream issues

| Issue | Symptom | Mechanism | Confidence |
|---|---|---|---|
| **#138** | extreme CPU usage when no dedicated GPU is connected | **D1** below — an infinite loop between `EnumerateDisplayHandles` and the native `enumerateNvidiaDisplayHandle` | **INFERENCE**, well supported (native side VERIFIED binary) |
| **#114 / #132** *(fixed on `work/resolution-change`)* | `Changing the resolution failed: DispChangeBadflags`, repeatedly, in Valorant and elsewhere | **D2**/**D58**/**D59** below — a modal box on the callback thread, a success read from the wrong `ChangeDisplaySettingsEx` call, a `_windowsResolutionSettings` snapshot that never learned about a user-initiated desktop resolution change (**D58** — probably #114's actual complaint), and a `DmDisplayFixedOutput`-inclusive equality guard that could re-fire a real mode set forever (**D59** — fits #132's "it keeps on saying that") | message/repeat path is **certain**; the root cause of the underlying `-4` is still **UNCERTAIN** (originates inside `user32`) |
| **#150 / #145 / #142** | hybrid and dual-GPU laptops, AMD chipset + NVIDIA GPU, dual-GPU desktops | **D3** below — four independent mechanisms, the strongest being that any machine with both vendors' drivers present is classified `Ambiguous` and refused | **INFERENCE** |
| **#144** | vibrance does not reset to the Windows level when the program closes | **D4** below — the reset happens only on the clean `FormClosing` path *and* only if `isInitialized`; abnormal exit restores nothing, and a dropped foreground event can strand the level | **INFERENCE** for the dropped-event half; the abnormal-exit half is **certain** |

**D1 — NVIDIA display-handle enumeration can never terminate.** `EnumerateDisplayHandles()`
(`vibrance.GUI/NVIDIA/NvidiaDynamicVibranceProxy.cs:286-297`) loops until
`enumerateNvidiaDisplayHandle` returns `-1`. **VERIFIED (binary):** that export returns `-1` *only* for
`NVAPI_END_ENUMERATION (-7)`; for any other non-zero status it falls into the success path and returns
`0`. On a machine with `nvapi.dll` present but no usable NVIDIA GPU, the expected statuses are `-6`
(`NVAPI_NVIDIA_DEVICE_NOT_FOUND`) or `-4` (`NVAPI_API_NOT_INITIALIZED`), so the loop spins forever —
one core at 100 % plus unbounded growth of `displayHandles` — inside the proxy constructor on the UI
thread, so the window never appears. Fix on either side: bound the loop (`i < NvapiMaxPhysicalGpus` or a
sane display cap) and/or make the native function return `-1` for every non-`0` status.

**D2 — FIXED on `work/resolution-change`. The resolution failure dialog was modal, on the callback
thread, and self-repeating.** The old `ResolutionHelper.ChangeResolutionEx` showed
`MessageBox.Show("Changing the resolution failed: " + Enum.GetName(...))`, reached from
`PerformResolutionChange` in either proxy — i.e. **on the UI thread inside `OnWinEventHook`** — so the
box appeared over a game that had just taken the foreground, where it could be invisible while
blocking input, and the message pump was stalled until it was dismissed. Because the guard was "does
the current mode differ from the target?", a failed change left the condition true and the box
returned on the next foreground switch. Fixed by removing the `using System.Windows.Forms` and every
`MessageBox` call site from `ResolutionHelper.cs` entirely and reporting a give-up through the
`ResolutionHelper.ResolutionChangeFailed` event instead, which `VibranceGUI` turns into a non-modal
`notifyIcon` balloon tip — see [§6.4](#64-the-optional-resolution-switch) for the full sequence, the
give-up bounds, and the two related defects below that the deeper investigation of #114/#132 turned
up along the way. `ResolutionHelper.ChangeResolution` (the dead code that duplicated the same
`MessageBox` call) and its `ChangeDisplaySettings` P/Invoke are deleted, not just unused.

**D58 — FIXED on `work/resolution-change`. `_windowsResolutionSettings` was a frozen snapshot — likely
#114's actual complaint.** `VibranceGUI`'s constructor built the "what is the user's Windows
resolution, and what modes does this device support" dictionary exactly once, at startup, and nothing
in the whole repository ever refreshed it (`SystemEvents`/`DisplaySettingsChanged`/`WM_DISPLAYCHANGE`
were all absent — verified, zero hits). If the user changed their desktop resolution by hand, or the
display topology changed (hot-plug, sleep/resume), the cached "Windows resolution" the revert path
compared against went stale, and every API call involved still reported success — from the user's
perspective, alt-tabbing out of the game "changes the resolution back to the wrong thing" with no
error anywhere. Fixed by subscribing `SystemEvents.DisplaySettingsChanged` in the constructor and
rebuilding the dictionary in place (mutating the same `Dictionary` instance both proxies hold a
reference to, NVIDIA's `static`ally) on every change — guarded so a refresh that lands while a game's
own resolution change is currently applied does not adopt the game's mode as the new "Windows
resolution" (see [§6.4](#64-the-optional-resolution-switch) for why that guard is the single most
dangerous line in the whole fix). The refresh logic itself now lives in
`WindowsResolutionRefresher.Refresh` (`WindowsResolutionRefresher.cs`), extracted out of
`VibranceGUI.cs` so it can run under `ResolutionChangeFixture` against a fake `IDisplayModeDevice`;
that fixture is also where the extraction's own follow-on gap got closed — a device that dropped out
of the dictionary entirely (detached) and reattached later had no "existing" entry for the guard
above to preserve, so it fell through to a live read exactly as if it were brand new, adopting the
game's mode on reattach. Closed by `_lastKnownWindowsModes` (`VibranceGUI.cs`), a last-known-mode
map that survives a device's absence and is checked as a fallback when no dictionary entry exists —
see [§6.4](#64-the-optional-resolution-switch) for the full mechanism.

**D59 — FIXED on `work/resolution-change`. The "does this still need changing?" guard included
`DmDisplayFixedOutput`, which some drivers never honestly report back — fits #132's "it keeps on
saying that".** Old `IsResolutionChangeNeeded` compared the full `ResolutionModeWrapper.Equals`,
which includes `DmDisplayFixedOutput` (the "(Center)"/"(Stretch)" scaling choice) — but nothing
declared `DM_DISPLAYFIXEDOUTPUT` in `dmFields` (see D2's `dmFields` problem above), so a driver was
free to silently ignore the requested value and always report back its own default. A user picking a
"(Center)" mode could get a change that succeeded on the four real, driver-supported fields yet could
never satisfy an equality guard that also demanded the ignored fifth one — so a real mode set and
registry write re-fired on **every** foreground event, forever. Fixed by
`ResolutionModeWrapper.MatchesAchievedMode`, a second comparison used only by
`IsResolutionChangeNeeded`/`ChangeResolutionEx` that deliberately excludes `DmDisplayFixedOutput`;
`Equals`/`GetHashCode`/`ToString` are untouched, since the settings combo box and the
`applicationData.xml` round trip still need all five fields to match.

**D3 — hybrid/dual-GPU systems.** Four mechanisms, strongest first (all **INFERENCE**, detailed in
[§7.9](#79-nvidia-specific-failure-modes)): (a) `GraphicsAdapter.cs:33-37` returns `Ambiguous` whenever
both vendor DLLs exist in SysWOW64 and `Program.cs:81-89` then quits, showing DDU advice that is wrong
for a hybrid machine; (b) on muxless Optimus `getAssociatedNvidiaDisplayHandle` returns `-1`, the guard
at `NvidiaDynamicVibranceProxy.cs:218` fails and nothing happens, silently; (c) a powered-down dGPU
makes `getGpuSystemType` fail, which the DLL flattens to `Unknown`, which aborts init with a
hardware-sounding dialog (`:177-185`); (d) GPU name and active outputs are read from `gpuHandles[0]`
only.

**D4 — nothing restores the display on an abnormal exit, and events can be dropped.** `CleanUp()`
(`VibranceGUI.cs:314-332`) is reached only from `Form1_FormClosing`, and its body is guarded by
`_v.GetVibranceInfo().isInitialized`. A Task Manager kill, crash, or logoff leaves the panel at the
ingame level, and **the resolution is never restored on exit at all**, even on the clean path. Separately,
if the process that raised a foreground event has already exited when `Process.GetProcessById` runs, the
exception is swallowed and **no event is dispatched** (`common/WinEventHook.cs:242-249`) — so the event
that would have reverted vibrance when a game exits can simply be lost. The third contributor is **D9**
below. There is no persisted "we changed this, restore it next time" record anywhere.

### 12.2 Crashes and data loss

**D5 — `RegistryController` throws out of `finally` on a null key.** `RegisterProgram`
(`vibrance.GUI/common/RegistryController.cs:21-33`) does `if (_startupKey == null) return false;` inside
the `try`, then `finally { _startupKey.Close(); }` — a `NullReferenceException` raised *in the finally*,
which the enclosing `catch` cannot catch. Same shape in `IsStartupPathUnchanged` (`:75-94`) and in
`UnregisterProgram`, which has no null check at all (`:41-51`). Any environment where the `Run` key
cannot be opened for write turns "return false" into an unhandled exception on the UI thread.

**D6 — double-clicking empty space in the application list throws.**
`listApplications_DoubleClick` indexes `this.listApplications.SelectedItems[0]` unguarded
(`VibranceGUI.cs:506`); `ListView.DoubleClick` fires on empty space too, giving an unhandled
`ArgumentOutOfRangeException`. The `if (selectedItem != null)` on the next line (`:507`) is dead code —
the indexer throws rather than returning null.

**D7 — the removal loop mutates the collection it is enumerating.** `buttonRemoveProgram_Click`
(`VibranceGUI.cs:531-538`) iterates `listApplications.SelectedItems` while `removeApplicationListItem`
removes from `listApplications.Items` inside the loop; the live selection collection shifts underneath,
so multi-select removal skips entries or throws.

**D8 — cross-vendor settings collision.** Neither settings file carries a vendor discriminator, and
`IngameLevel` is never clamped on load (`common/SettingsController.cs:173-183`). An AMD-written level up
to `300` read on NVIDIA gives `ArgumentOutOfRangeException` at `VibranceSettings.cs:30` (trackbar
`Maximum = 63`) and, through `NvidiaVibranceValueWrapper.Find(300)`
(`vibrance.GUI/NVIDIA/NvidiaVibranceValueWrapper.cs:36-43`), infinite recursion →
**`StackOverflowException`, which .NET cannot catch**. Full description in
[§9.7](#97-the-cross-vendor-settings-hazard).

**D9 — deleting the last watched application strands the vibrance.** Both handlers wrap *both* branches
in `if (_applicationSettings.Count > 0)` (`NvidiaDynamicVibranceProxy.cs:211`,
`AmdDynamicVibranceProxy.cs:110`), so removing the last profile while a game is ingame means the revert
branch never runs again.

**D10 — settings changed within five seconds of exit are lost**, because the save is a
`Thread.Sleep(5000)` debounce (`VibranceGUI.cs:181-185`) and `CleanUp` does not flush it. If the form is
disposed during that sleep, the `Invoke` in `ForceSaveVibranceSettings` (`:192`) throws on the worker
thread and lands in an **empty** `RunWorkerCompleted` that never inspects `e.Error` (`:214-216`).

**D11 — a failed XML write leaves a truncated settings file.** `SetVibranceSettings`
(`SettingsController.cs:51-64`) creates the `XmlWriter` outside a `using` and returns `false` on
exception, after the file has already been truncated by `XmlWriter.Create`.

**D12 — an unguarded native bootstrap crashes with a raw .NET dialog.** `Program.cs:57-61`
(`LoadUnmanagedLibraryFromResource` then `Marshal.PrelinkAll`) has no `try`/`catch`; a locked
`vibranceDLL.dll` (`File.WriteAllBytes` → `IOException`) or a missing export
(`EntryPointNotFoundException`) escapes `Main`.

### 12.3 Silently wrong behaviour

**D13 — `SetVibranceIngameLevel` does nothing.** Both implementations write
`VibranceInfo.userVibranceSettingActive` (`NvidiaDynamicVibranceProxy.cs:336-339`,
`AmdDynamicVibranceProxy.cs:77-80`), which nothing reads. The live preview in the per-game dialog
(`VibranceSettings.cs:40`) is inert.

**D14 — the AMD load clamp contradicts the AMD slider range.** Slider is `0..300`
(`Program.cs:48-49`); reload clamps to `defaultLevel(100)..maxLevel(300)`
(`SettingsController.cs:108-109,170-171`), so any saved desktop saturation below 100 resets to 100 on
the next launch. The `// todo` at `SettingsController.cs:107` marks the spot.

**D15 — NVIDIA momentarily resets Digital Vibrance to 0 at startup.** `InitializeProxy` reads the
current DVC level and writes `userVibranceSettingDefault`, which is still `0` at that moment
(`NvidiaDynamicVibranceProxy.cs:143,197-204`); the saved value is only applied later from
`VibranceGUI.cs:147`. Detail in [§7.7](#77-c-side-initialisation-and-the-startup-reset-users-notice).

**D16 — AMD double-writes on every game focus.** The handler first resets *all* displays to the Windows
level (`AmdDynamicVibranceProxy.cs:127`) and then applies the game level (`:130`/`:134`) — a visible
flash, and it happens on every foreground event because AMD has **no redundant-write suppression** at
all (NVIDIA gates its writes with `equalsDVCLevel`).

**D17 — AMD ignores "affect primary monitor only" when reverting and on exit.** The revert path always
calls `SetSaturationOnAllDisplays` (`AmdDynamicVibranceProxy.cs:153`), and so does `HandleDvcExit`
(`:88-91`), despite the flag being stored (`:93-96`).

**D18 — AMD's `_gameScreen` is only assigned inside the resolution-change branch**
(`AmdDynamicVibranceProxy.cs:123`), so with resolution switching off it stays `null`, which in turn
disables AMD's own restore-resolution branch at `:146`. NVIDIA assigns it outside the resolution-change branch, but still inside the `displayHandle != -1 && !equalsDVCLevel(...)` gate (`:218`; assignment at `:230`).

**D19 — the "system type is unknown" dialog is a hardware-sounding message for a software failure.**
**VERIFIED (binary):** the DLL returns `NvSystemTypeUnknown` for *any* NvAPI failure in
`getGpuSystemType`. It aborts init for the whole app if **any** enumerated GPU reports it
(`NvidiaDynamicVibranceProxy.cs:177-185`).

**D20 — the laptop rejection message is dead code.** `NvapiErrorSystypeUnsupported`
(`NvidiaDynamicVibranceProxy.cs:125-127`) is referenced by nothing, and `NvSystemTypeLaptop` is not
rejected anywhere — even though the README still says NVIDIA laptops are unsupported.

**D21 — AMD users see NVIDIA-worded error text.** `AmdDynamicVibranceProxy.cs:48-52` reuses
`NvidiaDynamicVibranceProxy.NvapiErrorInitFailed` and its Steam-guide link.

**D22 — a failed initialisation produces a live but non-functional proxy.** Both constructors catch
everything, show a dialog and return normally (`NvidiaDynamicVibranceProxy.cs:155-164`,
`AmdDynamicVibranceProxy.cs:45-54`). And **AMD sets `isInitialized = true` *before* calling `Init()`**
(`:34-35`), so an exception inside `Init()` leaves the proxy marked initialised.

**D23 — when the driver layer fails cleanly, the user gets no message at all.** If
`initializeLibrary()` returns `false` (NVIDIA) or `IsAvailable()` returns `false` (AMD), the constructor
simply skips initialisation; the GUI opens with every control disabled and the status label never turns
green (`VibranceGUI.cs:102,139-150`).

**D24 — the per-game resolution list only ever offers the primary monitor's modes.**
`_supportedResolutionList` is assigned only for `screen.Primary` (`VibranceGUI.cs:61-64`) and is the
list handed to every per-game dialog (`:510` → `VibranceSettings.cs:25`). For a game on a secondary
monitor, unsupported picks are silently skipped by the `Contains` guard in the proxy.

**D25 — pruned application entries are not written back.** `ReadVibranceSettings` drops entries whose
file no longer exists (`VibranceGUI.cs:379-385`) without saving, so the XML keeps the stale entry until
the next save for another reason.

**D26 — the INI is read with the Unicode entry point and written with the ANSI one.**
`GetPrivateProfileString` is declared `CharSet.Unicode` (`SettingsController.cs:16-23`) while
`WritePrivateProfileString` uses `EntryPoint = "WritePrivateProfileString"` with the default ANSI charset
(`:26-28`). Harmless for today's ASCII values; a trap for any future non-ASCII value.

**D27 — matching ignores the executable path.** Any process whose `ProcessName` equals a configured
`Name` triggers that profile, wherever it lives (`NvidiaDynamicVibranceProxy.cs:213`,
`AmdDynamicVibranceProxy.cs:112`), even though the full path is stored in `FileName`.

**D28 — the startup blind spot.** The hook only fires on transitions, so a game already in the
foreground when vibranceGUI starts gets nothing until the user alt-tabs away and back.

### 12.4 Native-boundary hazards

**D29 — the `__thiscall`-as-`StdCall` binding depends on the C++ class staying stateless.**
VERIFIED binary analysis in
[§7.5](#75-the-__thiscall-as-stdcall-binding--the-single-biggest-contributor-hazard). Adding a member
field to the C++ `vibrance` class breaks all 13 P/Invokes.

**D30 — bool returns may carry stale high bytes. INFERENCE, unverified at runtime.** The exports set
only `AL`; C# marshals a `bool` return as a 4-byte `BOOL`. A failed `initializeLibrary` could therefore
be observed as `true`. Cheap to test; would explain "it says Running but nothing happens" reports.

**D31 — `getActiveOutputs` has an `int[]` vs `int*[]` type mismatch across the boundary** and is
functionally dead (`NvidiaDynamicVibranceProxy.cs:34-39`, `:190`; native RVA `0x2d20`). Its result is
stored in `VibranceInfo.activeOutput`, which nothing reads.

**D32 — `getGpuSystemType` is declared taking an `int` in C# and an `int*` in C++**
(`NvidiaDynamicVibranceProxy.cs:97-102`). It works only because NvAPI GPU handles *are* pointers and the
process is 32-bit; the type lie will bite anyone porting to x64.

**D33 — the native DLL does not null-check `NvAPI_GetAssociatedNvidiaDisplayHandle`** at init
(VERIFIED binary, [§7.4](#74-the-initialisation-handshake)), so on a driver that lacks it, init succeeds
and the per-window display lookup later calls through a NULL pointer.

**D34 — `outputId` is hardcoded to `0`** in both `getDVCInfo` and `setDVCLevel` (VERIFIED binary), so
only a GPU's first output can ever be addressed.

**D35 — every redundancy check is a driver round-trip inside the callback.** `equalsDVCLevel` performs a
real `NvAPI_GetDVCInfo` call (VERIFIED binary), and the revert path can call it once per display
(`NvidiaDynamicVibranceProxy.cs:263`) — all on the UI thread.

**D36 — no ADL return code is ever checked.** `AdlMainControlCreate` (`AmdAdapter32.cs:20,27`),
`AdlAdapterNumberOfAdaptersGet` (`:24`) and above all **`AdlDisplayColorSet` (`:135`)** discard their
results, so an AMD failure is invisible to the app and to the user.

**D37 — `AmdAdapter32.Init()` calls a delegate with no null check** (`:20`) while null-checking a
sibling one line later (`:22`). Safe only because `Init()` is always preceded by `IsAvailable()`.

**D38 — `UnloadLibraryEx` dereferences `_hook` unconditionally**
(`NvidiaDynamicVibranceProxy.cs:364`); an NRE there aborts cleanup *before* `unloadLibrary()` runs. It is
survivable today only because `CleanUp` checks `isInitialized` first.

**D39 — the prebuilt DLL cannot be version-checked.** No `VS_VERSION_INFO` resource
([§7.2](#72-what-vibrancedlldll-actually-is)); the only identity is its hash.

**D40 — the driver-capability floor is accidental.** The DLL requires 12 NvAPI interface IDs to resolve,
**including `NvAPI_GetDVCInfoEx`, which it never uses** ([§7.4](#74-the-initialisation-handshake)).

**D41 — UNCERTAIN: whether `NvAPI_SetDVCLevel` still works on current NVIDIA driver branches.** Nothing
in the repository tracks this; open issues #149 and #156 concern recent drivers, and the unmerged
`feature/add-color-settings` branch suggests the newer colour API was being explored.

### 12.5 Resource leaks and lifetime

**D42 — the whole AMD disposal chain is declared and never wired up.** `IAmdAdapter : IDisposable`
(`AMD/vendor/IAmdAdapter.cs:8`) with a real `Disposer` (`AmdAdapter32.cs:176-195`), but nothing ever
calls `Dispose()`, because `AmdDynamicVibranceProxy.UnloadLibraryEx()` just unhooks and returns `true`
(`:82-86`) and neither proxy implements `IDisposable`. Per run this leaks **two unmatched
`ADL_Main_Control_Create` calls** (`AmdAdapter32.cs:20,27`), the `AllocCoTaskMem` adapter buffer (`:36`)
and one ADL display buffer per adapter.

**D43 — a finalizer calls into the vendor driver.** `AdlCheckLibrary`'s finalizer
(`AMD/vendor/adl32/ADLCheckLibrary.cs:24-30`) calls `ADL_Main_Control_Destroy()` during shutdown — a
hang/crash risk on the finalizer thread — and reads the **static** `_adlCheckLibrary._adlLibrary`
instead of `this._adlLibrary` (`:26`).

**D44 — `RegistryController.IsProgramRegistered` opens the key writable and never closes it**
(`RegistryController.cs:55-68`): one leaked handle per call, i.e. per app start and per checkbox toggle.

**D45 — `SettingsController` leaks its streams on every failure path** — the `StreamWriter` in
`PrepareFile` (`:85-90`), the `XmlWriter` (`:53-64`) and the `XmlReader` (`:175-183`). None uses
`using`.

**D46 — icons are never disposed.** `Icon.ExtractAssociatedIcon` results are added to `ImageList`s and
dropped (`VibranceGUI.cs:389,428`; `ProcessExplorer.cs:53`).

**D47 — `ProcessExplorer.GetAllProcesses` never disposes the `Process` objects** from
`Process.GetProcesses()` and calls `Process.GetCurrentProcess()` **inside the loop**
(`ProcessExplorer.cs:42-46`), allocating and leaking one per iteration.

**D48 — detection `LoadLibrary` handles are never freed** (`common/GraphicsAdapter.cs:57`); the
extracted `%APPDATA%\vibranceGUI\vibranceDLL.dll` is never deleted; `NvAPI_Unload` runs only on the
clean shutdown path (`NvidiaDynamicVibranceProxy.cs:365`).

### 12.6 Threading and re-entrancy

**D49 — all driver and display work happens on the UI thread inside the WinEvent callback**
([§6.7](#67-threading-model-in-one-page)). Long native calls freeze the UI; `ChangeDisplaySettingsEx` can
block for seconds during a mode set.

**D50 — the startup worker busy-waits with no timeout.**
`while (!this.IsHandleCreated) Thread.Sleep(500);` (`VibranceGUI.cs:122-125`). A form that never creates
a handle hangs that worker forever; `SetVisibleCore` force-creating the handle (`:82-93`) is the
workaround that keeps `-minimized` working.

**D51 — the shared settings list has no synchronisation.** One `List<ApplicationSetting>` instance is
shared by reference between form and proxy (`VibranceGUI.cs:73-74,145`), mutated from UI event handlers
and read from the hook callback. Safe today only because both happen to run on the same thread — and
callbacks *do* fire during nested modal loops (`VibranceSettings.ShowDialog()` at `:511`), so the proxy
can read the list while the user is mid-edit.

**D52 — `WinEventHook.GetInstance()` is a lock-free lazy singleton** (`common/WinEventHook.cs:212-217`)
and `_instance` is never cleared after `RemoveWinEventHook` (`:181`), so the hook cannot be
re-established in-process.

**D53 — `out` parameters are transferred across threads via an `Invoke` lambda**
(`VibranceGUI.cs:127-137`) — legal, but the only barrier is the `Invoke` itself.

### 12.7 Dead code and fossils

| Item | Location |
|---|---|
| **D54** `WinEvent` constant block — only 2 of 71 constants used | `common/WinEventHook.cs:34-178` (144 dead lines) |
| Polling-loop fossils: `shouldRun`, `sleepInterval`, `SetShouldRun`, `SetSleepInterval`, empty `HandleDvc()` | `common/Definitions.cs:16-17`; `NvidiaDynamicVibranceProxy.cs:341-348` |
| `SetVibranceIngameLevel` / `userVibranceSettingActive` write-only pair (**D13**) | `common/IVibranceProxy.cs:11`, `common/Definitions.cs:14` |
| `WinEventHookEventArgs.Process` never assigned or read; `WindowText`/`MainWindowTitle` assigned, never read — so the `GetWindowTextLength`/`GetWindowTextA` work is pointless | `common/WinEventHookEventArgs.cs:9-12`; `common/WinEventHook.cs:223-225` |
| `SettingsController.SetVibranceSetting` never called; the `refreshRate` key is read and discarded | `common/SettingsController.cs:69-79,131-137` |
| `ResolutionHelper.ChangeResolution` — **deleted** on `work/resolution-change`, along with its `ChangeDisplaySettings` P/Invoke, not merely dead; the no-arg `EnumerateSupportedResolutionModes()` is still present and still never called | `common/ResolutionHelper.cs:141-144` |
| `VibranceGUI.Log(string)` never called — and it writes to a *different* place than `Log(Exception)` (CWD-relative `vibranceGUI_log.txt` vs `%APPDATA%\vibranceGUI.log`) | `VibranceGUI.cs:334-361` |
| The `ProgressPercentage == 2` branch ("NVAPI Unloaded: …") is unreachable; only `ReportProgress(1)` is ever called | `VibranceGUI.cs:141,208-211` |
| `observerStatusLabel` shows a static string forever | `VibranceGUI.Designer.cs:196-203` |
| `NvApiStatus` — the entire 100+ member enum, unreferenced | `NVIDIA/NvidiaTypes.cs:25-56` |
| `isCsgoStarted` P/Invoke; the two `user32` text P/Invokes; the dead `char[64] sz` local | `NvidiaDynamicVibranceProxy.cs:83-88,104-108,192` |
| ADL: `AdlDisplayColorGet`, `ADL_Main_Memory_Free`, `AdlCheckLibrary.GetProcAddress`, and the unused constants (`AdlFail`, `AdlDriverOk`, `AdlMaxDisplays`, `AdlDisplayColorBrightness/Contrast/Hue/Temperature`, …) | `AMD/vendor/adl32/ADL.cs:44-59,69-75,189-203`; `ADLCheckLibrary.cs:45-53` |
| `isActive` assigned and never read | `AMD/vendor/AmdAdapter32.cs:43,53` |
| Native exports unreachable from C#: `handleDVC`, `test`, `getInterfaceVersionString`, both `printError` overloads | `vibranceDLL.dll` (VERIFIED binary) |
| `using System.Management;` is unused | `vibrance.GUI.csproj:80`; `common/ProcessExplorer.cs:9` |
| `Properties\Settings.settings` is empty and `Properties\Resources.resx` has no entries | — |

### 12.8 Structure and coupling

**D55 — `VibranceGUI` is a 556-line god-class**: form, composition root, settings I/O, icon/ImageList
bookkeeping, autostart, static logger and a raw `SendMessage` P/Invoke, all in one type
(`VibranceGUI.cs:489-502` for the last one). `ProcessExplorer` stores its parent as `Form` and downcasts
to `VibranceGUI` (`ProcessExplorer.cs:21,93`), creating a cycle an interface would remove.

**D56 — `common/` is not vendor-neutral.** `WinEventHook.cs:7-8` imports both vendor namespaces
unnecessarily; `IVibranceProxy.cs:2` and `ISettingsController.cs:2` import `vibrance.GUI.NVIDIA`;
`SettingsController.cs:100-104` hard-references NVIDIA proxy constants; `GraphicsAdapter.cs:4-6` reaches
into `AMD.vendor.adl32`/`adl64`. Meanwhile `AMD/vendor/utils/CommonUtils.cs` hosts the **NVIDIA** DLL
extraction.

**D57 — the same logic exists twice, in several places.** `OnWinEventHook` is a near-duplicate across
the two proxies (`NvidiaDynamicVibranceProxy.cs:213-304` vs `AmdDynamicVibranceProxy.cs:145-224`);
`AmdAdapter32.cs` and `AmdAdapter64.cs` are 195/197 identical; `adl32/**` and `adl64/**` differ by one
string; the vendor level ranges are duplicated *and disagree* between `Program.cs:47-50` and
`SettingsController.cs:105-110`; `GetForegroundWindow` logic exists both natively (NVIDIA) and as a C#
`DllImport` (`AmdDynamicVibranceProxy.cs:142-143`). Every fix must be applied twice. **One instance
fixed on `work/resolution-change`:** the per-proxy `IsResolutionChangeNeeded`/`PerformResolutionChange`
pair this used to call out is deleted from both proxies; both now call
`ResolutionHelper.IsResolutionChangeNeeded`/`ChangeResolutionEx` directly (see
[§6.4](#64-the-optional-resolution-switch)).

### 12.9 Hardcoded values, security surface and correctness nits

- **DLL-search surface.** `SetDllDirectory("%APPDATA%\vibranceGUI")` (`Program.cs:36`,
  `AMD/vendor/utils/NativeMethods.cs:10`) puts a **user-writable directory on the loader search path**
  before `LoadLibrary("nvapi.dll")` / `LoadLibrary("atiadlxy.dll")` are attempted by name
  (`common/GraphicsAdapter.cs:57`). `vibranceDLL.dll` is written to and loaded from that same directory
  with no hash or signature check (`CommonUtils.cs:29-34`). Not remotely exploitable, but any process
  running as the user can plant a same-named DLL there — worth knowing before you add more
  `LoadLibrary`-by-name calls.
- **Hardcoded external URLs**, all opened with `Process.Start` on a user click: the maintainer's Twitter
  (`Program.cs:19,77`; `VibranceGUI.cs:26,293,298`), the PayPal donation link (`VibranceGUI.cs:27`), the
  Guru3D DDU page (`Program.cs:86`), the Steam guide (`NvidiaDynamicVibranceProxy.cs:129`).
- **Hardcoded names and limits**: the mutex `vibranceGUI~Mutex`, `AppName = "vibranceGUI"`, the `Run`
  key, both settings filenames, `vibranceGUI.log`; `AdlMaxAdapters = 40` (`ADL.cs:51`);
  `NvapiMaxPhysicalGpus = 64` (`NvidiaDynamicVibranceProxy.cs:119`); the AMD `0..300` range with its
  `// todo`; and `"Counter-Strike: Global Offensive"` **compiled into the native DLL** (VERIFIED binary).
- **Culture-sensitive comparisons**: `.ToLower()` for path comparison (`VibranceGUI.cs:426,456`) where
  `OrdinalIgnoreCase` is meant (the proxies do use `OrdinalIgnoreCase` for name matching);
  `int.Parse`/`bool.Parse` with the current culture on INI values (`SettingsController.cs:157-159`).
- **`ResolutionModeWrapper` overrides `Equals` without `GetHashCode`**
  (`common/ResolutionModeWrapper.cs:33-56`), and its `ToString()` uses out-of-order format indices
  (`:29-30`) — intentional, but a landmine when editing.
- **Silent `catch` blocks**: every `catch` in `RegistryController` and `SettingsController` is a bare
  `catch (Exception)` returning a value with no logging; `AdlCheckLibrary` has three empty catches
  (`adl32/ADLCheckLibrary.cs:19-21`); `WinEventHook.RemoveWinEventHook` logs a **freshly constructed**
  exception instead of the one it caught and has an empty `finally {}` (`common/WinEventHook.cs:202-209`).
- **Naming/i18n leftovers**: `activeProcceses` (`ProcessExplorer.cs:42`), German designer comments
  (`VibranceGUI.Designer.cs:5-27`) and a German MSBuild error message (`vibrance.GUI.csproj:214`),
  handlers still called `Form1_*` (`VibranceGUI.cs:100-164`).

### 12.10 Open upstream issues and what the code says about them

Status as recorded during this analysis. "No mechanism identified" means this pass did not find one —
not that none exists.

| Issue | Title (abbreviated) | What the code says |
|---|---|---|
| #156 | frametime spikes with NVIDIA R595+ drivers | No mechanism identified. Related to **D41** (whether DVC still behaves on current drivers) and **D35** (a driver read before every write, inside the foreground callback). |
| #150, #145, #142 | laptop + external monitor; dual GPU with integrated graphics; NVIDIA GPU on AMD chipset | **D3** — four candidate mechanisms, INFERENCE. |
| #149 | does not work with NVIDIA driver 591.44 | No mechanism identified; see **D40**/**D41**. |
| #147, #143, #120 | feature requests: SDR/HDR sliders, reset keybind, command-line options | Not defects. Note only `-minimized` is parsed (`Program.cs:90`). |
| #144 | vibrance does not reset to Windows level when the program closes | **D4** (+ **D9**). |
| #138 | extreme CPU usage with no dedicated GPU | **D1**, INFERENCE, well supported. |
| #137 | does not reliably detect the game in the foreground | Candidates: the dropped-event race (**D4**), the startup blind spot (**D28**), and name-only matching (**D27**) — e.g. a launcher process owning the foreground window. INFERENCE. |
| #134 | CS2 jumps to the second monitor on alt-tab | No mechanism established. The historical candidate was the unconditional global commit call (`ChangeDisplaySettingsEx(null, ...)`) the pre-fix code ran after every staged mode change — **removed** on `work/resolution-change` (see **D2**; the new `CDS_TEST`/`CDS_UPDATEREGISTRY` sequence never touches a device other than the one it was asked about), so this hypothesis no longer applies to current `master`+this branch even though it was never confirmed either way. |
| #133 | native GUI is bugging | No mechanism identified. |
| #132, #114 | `DispChangeBadFlags` when changing resolution | **D2**/**D58**/**D59**, fixed on `work/resolution-change` — see [§6.4](#64-the-optional-resolution-switch). |
| #131 | degrades image on return to normal | No mechanism established; on AMD note **D16** (unconditional rewrite of every display on every event). |
| #128 | contrast/brightness messed up after CS:GO exit | Factually, this app writes **only** the saturation bit — `Adl.AdlDisplayColorSaturation` (`AMD/vendor/AmdAdapter32.cs:135-139`) — and never brightness, contrast, hue or temperature; those ADL constants are defined and unused (`adl32/ADL.cs:44-48`). On NVIDIA it writes only DVC levels. No code path in this repository sets contrast or brightness. |
| #116 | alt-tab out of Vulkan games → black screen | No mechanism established; the resolution switch (**D2** area) is the only display-mode code, and "Never change resolutions" is the available mitigation. |
| #113 | inconsistent behaviour in Battlefield 2042 | No mechanism identified. |
| #111 | AMD saturation set to 0 after a Windows restart | No mechanism identified. Two relevant facts: the AMD path performs **no driver write at initialisation** (unlike NVIDIA, **D15**), so whatever the driver holds after a reboot persists until the first foreground change; and the load clamp (**D14**) resets sub-100 saved values to 100. |

---

## 13. How to extend it

### 13.1 Adding a third vendor (for example Intel)

1. **`vibrance.GUI/common/GraphicsAdapter.cs`** — add the enum member (`:10-16`), the probe DLL name
   (`:24-27`) and a branch in `GetAdapter()` (`:30-51`). **Beware:** the `Ambiguous` check (`:33-37`) is
   a hardcoded pairwise test between two vendors; with three vendors it has to become "count how many
   vendor DLLs are present". The probe looks in `SpecialFolder.SystemX86`, so you need the **32-bit**
   vendor library.
2. **`vibrance.GUI/Intel/IntelDynamicVibranceProxy.cs`** — implement all ten `IVibranceProxy` members.
   **Copy the AMD proxy's shape, not the NVIDIA one**: instance state, an injected vendor adapter, no
   static handler. Subscribe to `WinEventHook.GetInstance()` in the constructor **only after
   initialisation actually succeeded**, and implement the redundant-write suppression that AMD lacks
   (**D16**).
3. **`vibrance.GUI/Program.cs`** — add a branch beside `:41-70` supplying the proxy factory plus
   `defaultWindowsLevel`, `minTrackBarValue`, `maxTrackBarValue`, `defaultIngameValue` and the
   `Func<int,string>` label resolver. **Those five numbers *are* the vendor's value contract; there is no
   interface for them.**
4. **`vibrance.GUI/common/SettingsController.cs:96-110`** — add the vendor's `defaultLevel`/`maxLevel`,
   and **fix the `< defaultLevel` lower-bound bug at `:170`** rather than replicating it (**D14**).
5. **`vibrance.GUI/vibrance.GUI.csproj`** — add every new file by hand (`:95-152`). If you ship a native
   helper, add it as an `<EmbeddedResource>` (`:189`) and extract it with
   `CommonUtils.LoadUnmanagedLibraryFromResource`.

Pitfalls specific to this codebase:

- `IVibranceProxy` has no `Init`, no `Apply` and no `Dispose`. All the work must happen in the
  constructor and be reported through `GetVibranceInfo().isInitialized`, **or the shell will never enable
  the GUI** (`VibranceGUI.cs:139`).
- `VibranceInfo` is NVIDIA-shaped. You will either leave half its fields unset (as AMD does) or extend
  the struct.
- The settings files are not vendor-namespaced; a third value range makes **D8** strictly worse. Fix it
  by writing the adapter name into the INI and rejecting or clamping mismatched files.
- **Do not copy the `adl32`/`adl64` pattern** for a bitness split. Use
  `LoadLibrary` + `Marshal.GetDelegateForFunctionPointer`; `Delegates.cs` already defines every signature
  and `AdlCheckLibrary.GetProcAddress` already wraps the mechanism (and is currently dead).
- The build is **x86-only**; any native dependency must be 32-bit.

### 13.2 Adding a driver capability (contrast, hue, gamma, colour settings…)

**AMD is the easy side.** `ADL_Display_Color_Set`'s third parameter is a bit selector, and
`Adl.AdlDisplayColorBrightness` / `Contrast` / `Hue` / `Temperature` are **already defined and unused**
(`AMD/vendor/adl32/ADL.cs:44-48`). Adding contrast is essentially
`AdlDisplayColorSet(adapterIndex, displayIndex, Adl.AdlDisplayColorContrast, value)` — **in both
`AmdAdapter32.cs` and `AmdAdapter64.cs`**.

**NVIDIA cannot be extended from C# alone.** New functionality needs a new `vibranceDLL.dll` built from
`juvlarN/vibranceDLL`, then a new `[DllImport]` block with the exact MSVC-mangled `EntryPoint`
(`NvidiaDynamicVibranceProxy.cs:19-115`). **Get the mangled name from `dumpbin /exports` on the rebuilt
DLL — do not hand-write it.** Keep the C++ class **stateless** (**D29**). Update the embedded resource;
note that `%APPDATA%\vibranceGUI\vibranceDLL.dll` is overwritten at every start, so stale copies
self-heal, but a running instance locks the file.

**Do this first, whatever else you do.** Wire up the two calls that already exist and are dead:
`ADL_Display_Color_Get` (`adl32/ADL.cs:189-203`) and NVIDIA's `NvDisplayDvcInfo.minLevel`/`maxLevel`,
which are already fetched and ignored (`NvidiaDynamicVibranceProxy.cs:198`,
`NVIDIA/NvidiaTypes.cs:14-15`). Sourcing the UI ranges from the driver instead of the hardcoded
`0..63` / `0..300` removes the largest single class of value-semantics bugs in this codebase (**D8**,
**D14**, and the `Find()` stack overflow).

### 13.3 Boundary rules a contributor must not violate

1. **Never throw out of a proxy constructor.** `Program.cs` has no `try`/`catch` around the factory.
2. **Never replace the shared `List<ApplicationSetting>` without calling `SetApplicationSettings`.** The
   proxy holds the *reference*; a new list silently disconnects the UI from the matcher
   (`VibranceGUI.cs:73-74,145`).
3. **Never do slow, blocking or modal work in `OnWinEventHook`.** It runs on the UI thread, during a
   foreground transition, often over a fullscreen game (**D2**, **D49**).
4. **Never add member state to the C++ `vibrance` class** without converting every P/Invoke to a
   `this`-safe form (**D29**).
5. **Never "correct" the mangled entry-point names**, including the typo `enumeratePhsyicalGPUs`.
6. **Never build AnyCPU or x64.** The native DLL is PE32 i386
   ([§3.3](#33-the-x86-rule-and-why-it-is-not-negotiable)).
7. **Never assume `isInitialized == true` means the driver works.** AMD sets it before `Init()`
   (**D22**), and **D30** suggests the NVIDIA return value itself may be untrustworthy.
8. **Never mutate the `VibranceInfo` returned by `GetVibranceInfo()`** — it is a struct copy.
9. **Never change the settings file shape without a migration plan.** There is no schema version and no
   legacy handling; old files either parse or reset to defaults, silently (**§9.3**).
10. **Never add a source file without adding it to `vibrance.GUI.csproj`** — and inspect that file after
    any merge, because `merge=union` is configured for it (**§3.7**).
11. **Keep the `WinEventHook` singleton single.** `GetInstance()` is not thread-safe and `_instance` is
    never cleared (**D52**).
12. **Do not add new vendor-specific references to `common/`.** It is already leaky (**D56**); adding more
    makes the vendor seam meaningless.
13. **Do not promote an INFERENCE to a fact** in code comments, commit messages or issue replies. Several
    of the most important claims in this document (**D1**, **D3**, **D30**) are unverified at runtime.

---

## 14. Open questions for maintainers

Merged from both archaeology passes, de-duplicated. These are genuinely unanswerable from the code.

**Native / NVIDIA**

1. Is `vibranceDLL.dll`'s source (`juvlarN/vibranceDLL`) still available and buildable? The binary here
   is from **2017-01-02** and has no version resource to check against (**D39**).
2. Does `NvAPI_SetDVCLevel` still function on current NVIDIA driver branches, or has Digital Vibrance
   moved to the newer colour-settings API? (The `feature/add-color-settings` branch suggests this was
   being explored; issues #149 and #156 point the same way.) (**D41**)
3. Is `getActiveOutputs` supposed to do anything? Its result is stored and never read, and the
   `int[]`/`int*[]` mismatch means it cannot work as written (**D31**).
4. Are NVIDIA laptops intentionally *allowed* now, or was the rejection check lost in a refactor? The
   message still exists as dead code and the README still says they are unsupported (**D20**).
5. Is the AL-only bool return (**D30**) real in practice? A ten-minute runtime test would settle it and
   might explain a class of "it says Running but nothing happens" reports.

**AMD**

6. What is the *actual* saturation range the AMD driver accepts — is the hardcoded `0..300` correct, or
   was it a guess? The `// todo` at `SettingsController.cs:107` suggests the latter.
7. Is the AMD clamp `defaultLevel = 100` (`SettingsController.cs:108`) deliberate (100 % is the ADL
   baseline) or a copy of the trackbar default? If deliberate, `minTrackBarValue` at `Program.cs:48`
   should be `100` to match (**D14**).
8. Was the `adl32` = `atiadlxx` / `adl64` = `atiadlxy` mapping deliberate, or an artefact of the confused
   rename in commit `f1e748d`? (**§8.4**)

**Shell and product**

9. Was `SetVibranceIngameLevel` ever meant to preview the level live? Nothing consumes
   `userVibranceSettingActive` (**D13**).
10. Should `applicationData.xml` be namespaced per GPU vendor, or should `IngameLevel` simply be clamped
    on load, so that switching vendors does not crash the app (**D8**)?
11. For #114/#132: do the affected users have more than one monitor, and does the failing
    `deviceName` correspond to an attached display? `work/resolution-change` added logging (device
    name and the `DispChange` code, once per device+code — `RecordFailure`,
    `common/ResolutionHelper.cs:345-376`) but not `Marshal.GetLastWin32Error()`, since
    `ChangeDisplaySettingsEx`'s `DispChange` return value already carries the failure code and the
    Win32 last-error is not documented as meaningful for this API; still the cheapest next
    diagnostic step if the `DispChange` code alone turns out not to be enough (**D2**).
12. Was the Travis pipeline ever green, and is CI intended to be restored? A GitHub Actions job on
    `windows-latest` would actually exercise this project, unlike the Linux/Mono one (**§3.7**).
13. What is the plan for PR #140 (`feature/add-color-settings`, which carries the released `v2.5.0` tag)
    and PR #153 (.NET 4.8 + hotkeys)? `master` currently ships older behaviour than the published
    release (**§2.3**).
