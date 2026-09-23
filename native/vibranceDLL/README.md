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
- Fidelity: `stdafx.h`, `stdafx.cpp` and `targetver.h` are unmodified from upstream -
  confirmed with `git hash-object` against each file's committed blob SHA at the
  pinned commit, which matches exactly. `vibrance.h` and `vibrance.cpp` are **no
  longer** bit-identical to upstream - see items 7 and 8 below for exactly what
  changed and why (item 7's one line predates item 8's broader change; both remain
  true, cumulatively). (Their German comments are upstream's own
  CP1252/Latin-1-encoded text; a UTF-8-assuming viewer may render that as mojibake,
  but the bytes themselves are untouched, in both the touched and untouched files.)
  The files edited are `vibrance/vibrance.vcxproj` (§ below), `vibrance/vibrance.h`
  and `vibrance/vibrance.cpp` (items 7-8), plus the two wholly new files this
  vendoring adds.

## Note on the DLL this replaces

The `vibranceDLL.dll` previously checked in at `vibrance.GUI/NVIDIA/vibranceDLL.dll`
was **not** built from `juv/vibranceDLL` master. Its export table has two
`printError` overloads and a `?test@` export that master does not produce; those
exports trace to the fork `juvlarN/vibranceDLL` instead. At vendoring time, all 12
exports that the C# proxy then actually bound were name-identical (same mangled
`__thiscall` signature) between the two, so this vendored source was a correct
replacement for build purposes. **That is no longer true as of item 8 below**: the
handle-width fix changed several of those 12 mangled names (an NvAPI handle
parameter that used to mangle as `int` now mangles as `void *`), so a mangled-name
comparison against the *original* 2017 binary would now show mismatches on the
methods item 8 touched - this was never a goal to preserve, since those exports
were replaced by the C# side's move to the C ABI wrapper (`vibrance_c.h`/`.cpp`)
regardless. A byte-identical rebuild of the old DLL was never possible from this
source and still is not the goal.

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
   `vibranceDLL::vibrance` instance and forwarding the call. At the time this wrapper
   layer was added, `vibrance.h` and `vibrance.cpp` themselves were untouched; item 8
   below has since touched both (the wrapper layer and the class it wraps were edited
   in separate changes, for separate reasons - undecorated `__cdecl` naming here,
   handle width there). The wrappers use `__cdecl` and export undecorated names
   (`vibrance_<originalName>`) so the same source produces an identical export-name
   set on both x86 and x64, and so the C# side can bind by a stable name instead of a
   32-bit-only mangled `__thiscall` symbol. See the comments in `vibrance_c.cpp` for
   the reasoning behind returning `int` instead of `bool`, and for why the forwarding
   instance is a local automatic rather than a function-local `static`.
5. Removed `.gitattributes`, `.gitignore` and `vibrance.vcxproj.filters` from the
   vendored copy; none of the three affect a command-line build, and the main repo
   already carries its own `.gitignore` at the root.
6. Replaced upstream's `README.md` with this file.
7. `vibrance/vibrance.cpp`: `vibrance::initializeLibrary()` hardcoded
   `LoadLibraryA("nvapi.dll")`, which is a 32-bit-only DLL - there is no 64-bit
   `nvapi.dll` anywhere on a stock Windows install (VERIFIED on this machine:
   `System32\nvapi64.dll` is 64-bit, `SysWOW64\nvapi.dll` is 32-bit, neither has a
   cross-copy). Wrapped the call in `#ifdef _WIN64` to load `"nvapi64.dll"` in a
   64-bit build and left the original `"nvapi.dll"` for 32-bit, so the x64
   configuration added in this change can actually find NvAPI at runtime. This is
   the one line in `vibrance.cpp` that is no longer bit-identical to upstream; see
   the Fidelity note above.
8. `vibrance/vibrance.h` and `vibrance/vibrance.cpp`: every NvAPI handle (GPU
   handle, display handle) that was typed `int` is now `void *`, and the two
   methods that returned a handle typed `int` (`enumerateNvidiaDisplayHandle`,
   `getAssociatedNvidiaDisplayHandle`) now return `void *`. NvAPI handles are
   genuine pointers - 4 bytes on x86, 8 on x64 - and typing one `int` is only
   correct by accident on x86; on x64 it silently truncates the upper 32 bits of
   whatever the driver hands back. MEASURED on this machine's RTX 5070 Ti in a
   64-bit process (`0xCC`-prefilled buffers before the call): `NvAPI_EnumPhysicalGPUs`
   writes 8-byte array elements, and `EnumNvidiaDisplayHandle` writes a full 8-byte
   value - an `int`-typed handle here is not a theoretical bug. `-1`-as-invalid-
   handle guards are equally affected: `void *(-1)` is all-bits-set regardless of
   pointer width, but a guard comparing against a 4-byte `int(-1)` widened to a
   64-bit slot does not necessarily match it (see the C# side's own `IntPtr(-1)`
   constant and its comment, `NvidiaDynamicVibranceProxy.cs`). Exactly nine lines in
   `vibrance.h` (the typedefs and method declarations that name a handle) and five
   signature definitions plus five body lines in `vibrance.cpp` changed; every
   argument/return value that was **already** pointer-typed (`int *gpuHandles[]`,
   `int *outputIds[]`, `getGpuSystemType`'s `int *gpuHandle`, and the output-mask
   values `getActiveOutputs` writes) needed no change here - a real pointer already
   widens correctly with the platform, which is exactly why those specific
   parameters were spared. `vibrance_c.h`/`.cpp` (item 4) were widened to match, via
   a new `vibrance_handle_t` (`void *`) typedef used consistently across that
   wrapper's declarations; the export *names* did not change, so §7.3's C# binding
   mechanism itself needed no further change, only the twelve `[DllImport]`
   declarations' own parameter/return types (see `vibrance.GUI/NVIDIA/`
   `NvidiaDynamicVibranceProxy.cs`). Also added a thirteenth, test-only export,
   `vibrance_abi_echoHandle`, purely so a fixture can round-trip a handle-shaped
   value through the real ABI boundary and catch a truncating C# signature that
   `Marshal.Prelink` cannot detect on its own - see that export's own comment in
   `vibrance_c.h`.
