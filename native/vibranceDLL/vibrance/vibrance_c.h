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
// both architectures, so the x64 port (a later slice) needs no ABI change here at
// all - only a rebuild for the new platform.
#pragma once

#ifdef __cplusplus
extern "C"
{
#endif

	__declspec(dllexport) int  __cdecl vibrance_initializeLibrary(void);
	__declspec(dllexport) int  __cdecl vibrance_unloadLibrary(void);
	__declspec(dllexport) int  __cdecl vibrance_getActiveOutputs(int **gpuHandles, int **outputIds);
	__declspec(dllexport) void __cdecl vibrance_enumeratePhsyicalGPUs(int **gpuHandles);
	__declspec(dllexport) int  __cdecl vibrance_getGpuName(int **gpuHandles, char *szName);
	__declspec(dllexport) int  __cdecl vibrance_getDVCInfo(void *info, int defaultHandle);
	__declspec(dllexport) int  __cdecl vibrance_enumerateNvidiaDisplayHandle(int index);
	__declspec(dllexport) int  __cdecl vibrance_setDVCLevel(int defaultHandle, int level);
	__declspec(dllexport) int  __cdecl vibrance_isWindowActive(void **hwnd);
	__declspec(dllexport) int  __cdecl vibrance_equalsDVCLevel(int defaultHandle, int level);
	__declspec(dllexport) int  __cdecl vibrance_getGpuSystemType(int *gpuHandle);
	__declspec(dllexport) int  __cdecl vibrance_getAssociatedNvidiaDisplayHandle(const char *szDisplayName, int length);

#ifdef __cplusplus
}
#endif
