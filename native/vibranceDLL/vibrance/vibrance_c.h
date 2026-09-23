// Purely additive extern "C" wrapper layer around vibranceDLL::vibrance.
//
// vibrance.h/.cpp are not touched by this file. Its methods are exported from the
// DLL under 32-bit-only C++ __thiscall-mangled names (e.g.
// "?initializeLibrary@vibrance@vibranceDLL@@QAE_NXZ"), which the C# side has so far
// bound with no "this" argument - a hack that only works on x86, where __thiscall
// happens to pass "this" in ECX and these particular methods never touch it. x64
// has no __thiscall at all, so that hack cannot carry over.
//
// This header instead declares one free function per bound method, using __cdecl
// and an undecorated exported name. __cdecl (rather than __stdcall) matters because
// it is the same calling convention, with the same export name, on both x86 and
// x64 - __stdcall would additionally mangle names as "_foo@N", where N depends on
// the argument list's byte size and so differs per signature and per architecture.
// With __cdecl the exact same source file produces an identical export-name set on
// both architectures, so the x64 port needed no change to this naming/calling-
// convention mechanism, nor to any export's name - only a rebuild for the new
// platform (native/vibranceDLL's own x64 configuration) plus the parameter-width
// fix below, which is a separate concern from the export mechanism this comment
// describes: NvAPI handles are pointers, 4 bytes on x86 and 8 on x64, and every
// parameter/return value below that carries one is now typed vibrance_handle_t
// (void *) rather than int, so it widens correctly instead of truncating on x64.
// See native/vibranceDLL/README.md for the measurements that made this necessary.
#pragma once

// An opaque NvAPI handle (GPU handle, display handle, ...), always pointer-sized.
// Every one of these was previously typed "int" below, which is correct only on
// x86, where a pointer happens to be 4 bytes; NvAPI itself always hands these out
// as real pointer values, so on x64 a 4-byte int silently truncates the upper 32
// bits of whatever the driver returns.
typedef void *vibrance_handle_t;

#ifdef __cplusplus
extern "C"
{
#endif

	__declspec(dllexport) int  __cdecl vibrance_initializeLibrary(void);
	__declspec(dllexport) int  __cdecl vibrance_unloadLibrary(void);
	__declspec(dllexport) int  __cdecl vibrance_getActiveOutputs(vibrance_handle_t *gpuHandles, unsigned int **outputIds);
	__declspec(dllexport) void __cdecl vibrance_enumeratePhsyicalGPUs(vibrance_handle_t *gpuHandles);
	__declspec(dllexport) int  __cdecl vibrance_getGpuName(vibrance_handle_t *gpuHandles, char *szName);
	__declspec(dllexport) int  __cdecl vibrance_getDVCInfo(void *info, vibrance_handle_t defaultHandle);
	__declspec(dllexport) vibrance_handle_t __cdecl vibrance_enumerateNvidiaDisplayHandle(int index);
	__declspec(dllexport) int  __cdecl vibrance_setDVCLevel(vibrance_handle_t defaultHandle, int level);
	__declspec(dllexport) int  __cdecl vibrance_isWindowActive(void **hwnd);
	__declspec(dllexport) int  __cdecl vibrance_equalsDVCLevel(vibrance_handle_t defaultHandle, int level);
	__declspec(dllexport) int  __cdecl vibrance_getGpuSystemType(vibrance_handle_t gpuHandle);
	__declspec(dllexport) vibrance_handle_t __cdecl vibrance_getAssociatedNvidiaDisplayHandle(const char *szDisplayName, int length);

	// Test-only: exists solely so NvidiaInteropFixture's N23 can round-trip a handle-shaped
	// value through the real __cdecl ABI boundary and catch a truncating x64 P/Invoke
	// signature. Marshal.Prelink cannot catch this on its own - it builds the marshalling
	// stub without calling it, and an undecorated __cdecl export carries no type information
	// to check a C# declaration against, so a wrong-but-marshalable width passes clean. Not
	// part of vibranceDLL::vibrance's own API surface - forwards nothing, touches no NvAPI
	// state, and is not counted among the twelve "real" exports N17/N18/N19 pin.
	__declspec(dllexport) vibrance_handle_t __cdecl vibrance_abi_echoHandle(vibrance_handle_t handle);

#ifdef __cplusplus
}
#endif
