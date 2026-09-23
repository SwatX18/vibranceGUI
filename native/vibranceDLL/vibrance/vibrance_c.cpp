#include "stdafx.h"
#include <windows.h>
#include "vibrance_c.h"
#include "vibrance.h"

// See vibrance_c.h for why this layer exists and why it uses __cdecl with
// undecorated names. Two further choices below are worth spelling out:
//
// - Each wrapper builds a *local automatic* vibranceDLL::vibrance and forwards the
//   call through it (vibranceDLL::vibrance v; return v.foo(...);), rather than a
//   function-local "static vibrance v;". The class has no data members (every piece
//   of state vibrance.cpp touches - the NvAPI function pointers, shouldRun,
//   defaultHandle - is a namespace-scope global, and the ctor/dtor are both empty
//   bodies), so the compiler emits no code for constructing or destroying "v": it
//   exists only to supply a "this" for the __thiscall methods. A function-local
//   static would instead add a thread-safe initialization guard plus an atexit
//   registration inside a DLL, for an object that does nothing - pure overhead with
//   no behavioural benefit. A null/dummy "this" was considered and rejected: these
//   methods never dereference "this" today, but passing one would be undefined
//   behaviour that just happens to work, and codifying UB in a new API surface is
//   exactly what this layer should not do.
//
// - Every wrapper returns int (0 or 1), never bool, even where the wrapped method
//   returns bool. A C++ bool return is one byte, conventionally left in AL; C#'s
//   default marshalling for a bool return value expects a 4-byte Win32 BOOL. The
//   existing (pre-this-change) bindings only get away with returning C++ bool into
//   a C# bool because MSVC happens to zero-extend AL into EAX on return. Returning
//   int here makes that exact instead of incidental, at no runtime cost - the C#
//   proxy's own method signatures are unaffected; they still return bool, per Task 5.

namespace
{
	// The struct layout is unchanged; the void* here just avoids exposing
	// vibrance.h's nested type name across the extern "C" boundary.
	typedef vibranceDLL::vibrance::NV_DISPLAY_DVC_INFO NvDisplayDvcInfo;
}

extern "C"
{
	int vibrance_initializeLibrary(void)
	{
		vibranceDLL::vibrance v;
		return v.initializeLibrary() ? 1 : 0;
	}

	int vibrance_unloadLibrary(void)
	{
		vibranceDLL::vibrance v;
		return v.unloadLibrary() ? 1 : 0;
	}

	int vibrance_getActiveOutputs(int **gpuHandles, int **outputIds)
	{
		vibranceDLL::vibrance v;
		return v.getActiveOutputs(gpuHandles, outputIds);
	}

	void vibrance_enumeratePhsyicalGPUs(int **gpuHandles)
	{
		vibranceDLL::vibrance v;
		v.enumeratePhsyicalGPUs(gpuHandles);
	}

	int vibrance_getGpuName(int **gpuHandles, char *szName)
	{
		vibranceDLL::vibrance v;
		return v.getGpuName(gpuHandles, szName) ? 1 : 0;
	}

	int vibrance_getDVCInfo(void *info, int defaultHandle)
	{
		vibranceDLL::vibrance v;
		return v.getDVCInfo(reinterpret_cast<NvDisplayDvcInfo *>(info), defaultHandle) ? 1 : 0;
	}

	int vibrance_enumerateNvidiaDisplayHandle(int index)
	{
		vibranceDLL::vibrance v;
		return v.enumerateNvidiaDisplayHandle(index);
	}

	int vibrance_setDVCLevel(int defaultHandle, int level)
	{
		vibranceDLL::vibrance v;
		return v.setDVCLevel(defaultHandle, level) ? 1 : 0;
	}

	int vibrance_isWindowActive(void **hwnd)
	{
		vibranceDLL::vibrance v;
		return v.isWindowActive(reinterpret_cast<HWND *>(hwnd)) ? 1 : 0;
	}

	int vibrance_equalsDVCLevel(int defaultHandle, int level)
	{
		vibranceDLL::vibrance v;
		return v.equalsDVCLevel(defaultHandle, level) ? 1 : 0;
	}

	int vibrance_getGpuSystemType(int *gpuHandle)
	{
		vibranceDLL::vibrance v;
		return v.getGpuSystemType(gpuHandle);
	}

	int vibrance_getAssociatedNvidiaDisplayHandle(const char *szDisplayName, int length)
	{
		vibranceDLL::vibrance v;
		return v.getAssociatedNvidiaDisplayHandle(szDisplayName, length);
	}
}
