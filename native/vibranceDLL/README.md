# vibranceDLL (vendored)

This directory is a vendored copy of the native DLL that backs vibranceGUI's NVIDIA
digital-vibrance control, kept in-tree (not a git submodule) because we patch its
Visual Studio project directly.

- Upstream: https://github.com/juv/vibranceDLL
- Commit vendored: `0240cace664ac4373697b3e367ea204bcc07831a` (upstream `master` at the
  time of vendoring, 2026-09-23).
- Licence: neither this repository nor upstream `juv/vibranceDLL` carries a licence
  file of any kind. Nothing here should be taken as legal advice about what that
  means for reuse; it is recorded here only as a fact about the source as vendored.
- Fidelity: `vibrance.h`, `vibrance.cpp`, `stdafx.h`, `stdafx.cpp` and `targetver.h`
  are unmodified from upstream - confirmed with `git hash-object` against each
  file's committed blob SHA at the pinned commit, which matches exactly. (Their
  German comments are upstream's own CP1252/Latin-1-encoded text; a UTF-8-assuming
  viewer may render that as mojibake, but the bytes themselves are untouched.) The
  only file edited is `vibrance/vibrance.vcxproj` (§ below), plus the two wholly new
  files this vendoring adds.

## Note on the DLL this replaces

The `vibranceDLL.dll` previously checked in at `vibrance.GUI/NVIDIA/vibranceDLL.dll`
was **not** built from `juv/vibranceDLL` master. Its export table has two
`printError` overloads and a `?test@` export that master does not produce; those
exports trace to the fork `juvlarN/vibranceDLL` instead. All 12 exports that the C#
proxy actually binds are identical between the two, so this vendored source is a
correct replacement for build purposes, but a byte-identical rebuild of the old DLL
is not possible from this source and was never the goal.

## Changes made relative to the vendored upstream commit

1. `vibrance/vibrance.vcxproj`: `PlatformToolset` changed from `v140_xp` to `v143`
   in both configurations. `v140_xp` (the Windows XP-compatible toolset) is not
   installed in this build environment and nothing here targets XP any more; v143
   is the locally available MSVC 14.44.35207 toolset.
2. `vibrance/vibrance.vcxproj`: Debug's `<ConfigurationType>` changed from
   `Application` to `DynamicLibrary`. Upstream's Debug configuration builds an
   `.exe`; only Release builds a DLL. Since this project is consumed as a DLL,
   Debug is made consistent with Release.
3. `vibrance/vibrance.vcxproj`: added `<RuntimeLibrary>MultiThreaded</RuntimeLibrary>`
   (`MultiThreadedDebug` in the Debug configuration) to both `ItemDefinitionGroup`
   blocks' `<ClCompile>` settings. Upstream's own README instructs building this way
   manually ("Change the runtime library setting to *Multithreaded* (/MT) under
   Visual Studio's project properties") but the setting was never captured in the
   `.vcxproj` itself, so a default v143 build links `/MD` and the resulting DLL
   would import `MSVCP140.dll`, `VCRUNTIME140.dll` and `api-ms-win-crt-*.dll` - a VC++
   redistributable dependency the shipped DLL does not have. `/MT` statically links
   the CRT so the import table stays limited to `KERNEL32.dll` and `USER32.dll`
   (VERIFIED for this build - parsed the rebuilt DLL's own import table directly).
   The previously shipped 2017 binary additionally imported `ADVAPI32.dll`
   (`SystemFunction036`); this rebuild does not need it and does not import it - one
   fewer dependency, not a regression, since nothing in `vibrance.cpp`/`stdafx.cpp`
   calls into ADVAPI32.
4. Added `vibrance/vibrance_c.h` and `vibrance/vibrance_c.cpp` (and added both to
   `vibrance.vcxproj`). These are a purely additive `extern "C"` wrapper layer around
   `vibranceDLL::vibrance` - one free function per method, each constructing a local
   `vibranceDLL::vibrance` instance and forwarding the call. `vibrance.h` and
   `vibrance.cpp` themselves are untouched. The wrappers use `__cdecl` and export
   undecorated names (`vibrance_<originalName>`) so the same source produces an
   identical export-name set on both x86 and x64, and so the C# side can bind by a
   stable name instead of a 32-bit-only mangled `__thiscall` symbol. See the
   comments in `vibrance_c.cpp` for the reasoning behind returning `int` instead of
   `bool`, and for why the forwarding instance is a local automatic rather than a
   function-local `static`.
5. Removed `.gitattributes`, `.gitignore` and `vibrance.vcxproj.filters` from the
   vendored copy; none of the three affect a command-line build, and the main repo
   already carries its own `.gitignore` at the root.
6. Replaced upstream's `README.md` with this file.
