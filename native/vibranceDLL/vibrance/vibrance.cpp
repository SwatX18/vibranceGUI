#include "stdafx.h"
#include <windows.h>
#include <iostream>
#include <thread>
#include "vibrance.h"
#include "nvapi_display.h"
#include <vector>
using namespace std;

namespace vibranceDLL
{
	vibrance::NvAPI_QueryInterface_t					NvAPI_QueryInterface     = NULL;
	vibrance::NvAPI_Initialize_t						NvAPI_Initialize         = NULL;
	vibrance::NvAPI_Unload_t							NvAPI_Unload			 = NULL;
	vibrance::NvAPI_EnumPhysicalGPUs_t					NvAPI_EnumPhysicalGPUs   = NULL;
	vibrance::NvAPI_GPU_GetUsages_t						NvAPI_GPU_GetUsages      = NULL;
	vibrance::NvAPI_GPU_GetFullName_t					NvAPI_GPU_GetFullName	 = NULL;
	vibrance::NvAPI_GPU_GetActiveOutputs_t				NvAPI_GPU_GetActiveOutputs = NULL;
	vibrance::NvAPI_GetDVCInfo_t						NvAPI_GetDVCInfo		 = NULL;
	vibrance::NvAPI_GetDVCInfoEx_t						NvAPI_GetDVCInfoEx		 = NULL;
	vibrance::NvAPI_SetDVCLevel_t						NvAPI_SetDVCLevel		 = NULL;
	vibrance::NvAPI_EnumNvidiaDisplayHandle_t			NvAPI_EnumNvidiaDisplayHandle = NULL;
	vibrance::NvAPI_GetInterfaceVersionString_t			NvAPI_GetInterfaceVersionString = NULL;
	vibrance::NvAPI_GetErrorMessage_t					NvAPI_GetErrorMessage = NULL;
	vibrance::NvAPI_GetAssociatedNvidiaDisplayHandle_t	NvAPI_GetAssociatedNvidiaDisplayHandle = NULL;
	vibrance::NvAPI_GPU_GetSystemType_t					NvAPI_GPU_GetSystemType = NULL;

	nvapi_display::NvAPI_DISP_GetDisplayIdByDisplayName_t	NvAPI_DISP_GetDisplayIdByDisplayName = NULL;
	nvapi_display::NvAPI_DISP_GetDisplayConfig_t			NvAPI_DISP_GetDisplayConfig = NULL;
	nvapi_display::NvAPI_DISP_SetDisplayConfig_t			NvAPI_DISP_SetDisplayConfig = NULL;

	bool shouldRun;
	void *defaultHandle;

	void vibrance::printError(_NvAPI_Status status)
	{
		char szDesc[64] = {0};
		NvAPI_GetErrorMessage(status, szDesc);
	}


	void vibrance::handleDVC()
	{
		bool isChanged = false;
		while(shouldRun)
		{
			HWND hwnd = NULL;
			if(isCsgoStarted(&hwnd) && hwnd != NULL)
			{
				if(isWindowActive(&hwnd))
				{
					int nLen = GetWindowTextLength(hwnd);
					if(nLen > 0)
					{
						char *szTitle = (char*)malloc(nLen + 1);
						GetWindowTextA(hwnd, szTitle, nLen+1);
						if(szTitle != NULL)
						{
							if(!equalsDVCLevel(defaultHandle, NVAPI_MAX_LEVEL))
							{
								setDVCLevel(defaultHandle, NVAPI_MAX_LEVEL);
								isChanged = true;
							}
						}
					}
				}
				else
				{
					if(isChanged)
					{
						setDVCLevel(defaultHandle, NVAPI_DEFAULT_LEVEL);
						isChanged = false;
					}
				}
			}
			else
			{
				if(isChanged || !equalsDVCLevel(defaultHandle, NVAPI_DEFAULT_LEVEL))
				{
					setDVCLevel(defaultHandle, NVAPI_DEFAULT_LEVEL);
					isChanged = false;
				}

			}
			Sleep(5000);
		}
		if(!equalsDVCLevel(defaultHandle, NVAPI_DEFAULT_LEVEL))
			setDVCLevel(defaultHandle, NVAPI_DEFAULT_LEVEL);

		std::cout << "DVC Level Thread exited!" << std::endl;
	}


	bool vibrance::isCsgoStarted(HWND *hwnd)
	{
		HWND test = FindWindowW(0, L"Counter-Strike: Global Offensive");
		*hwnd = test;
		if (!hwnd)   // Process is not running
		{
			return false;
		}
		return true;
	}

	bool vibrance::isWindowActive(HWND *hwnd)
	{
		HWND activeWindow = GetForegroundWindow();
		if(activeWindow == NULL)
			return false;
		return *hwnd == activeWindow;
	}

	bool vibrance::equalsDVCLevel(void *defaultHandle, int level)
	{	
		NV_DISPLAY_DVC_INFO info = {};
		if(getDVCInfo(&info, defaultHandle))
		{
			return info.currentLevel == level;
		}
		return false;
	}

	bool vibrance::getDVCInfo(NV_DISPLAY_DVC_INFO *info, void *defaultHandle)
	{
		//NV_DISPLAY_DVC_INFO info2 = {};

		//info2.version = sizeof(NV_DISPLAY_DVC_INFO) | 0x10000;
		//int status2 = (*NvAPI_GetDVCInfo)(defaultHandle, NULL, &info2);
		//std::cerr << info2.currentLevel << std::endl;

		//info->currentLevel = info2.currentLevel; 
		//info->maxLevel = info2.maxLevel; 
		//info->minLevel = info2.minLevel; 

		info->version = sizeof(NV_DISPLAY_DVC_INFO) | 0x10000;
		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_GetDVCInfo)(defaultHandle, 0, info);


		if(status != NVAPI_OK)
		{
			return false;
		}
		return true;
	}

	bool vibrance::setDVCLevel(void *defaultHandle, int level)
	{
		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_SetDVCLevel)(defaultHandle, 0, level);
		if(status != NVAPI_OK)
		{
			return false;
		}
		return true;
	}

	void *vibrance::enumerateNvidiaDisplayHandle(int index)
	{
		void *defaultHandle = NULL;
		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_EnumNvidiaDisplayHandle)(index, &defaultHandle);
		if(status != 0 && status == NVAPI_END_ENUMERATION)
		{
			return NULL;
		}
		return defaultHandle;
	}

	bool vibrance::getInterfaceVersionString(char* szVersion)
	{
		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_GetInterfaceVersionString)(szVersion);
		if(status != 0)
		{
			return false;
		}
		return true;
	}

	void vibrance::enumeratePhsyicalGPUs(int *gpuHandles[])
	{
		int          gpuCount = 0;
		unsigned int gpuUsages[NVAPI_MAX_USAGES_PER_GPU] = { 0 };

		// gpuUsages[0] must be this value, otherwise NvAPI_GPU_GetUsages won't work
		gpuUsages[0] = (NVAPI_MAX_USAGES_PER_GPU * 4) | 0x10000;

		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_EnumPhysicalGPUs)(gpuHandles, &gpuCount);
		if(status != 0)
		{
			return;
		}
	}

	bool vibrance::getGpuName(int *gpuHandles[], char* szName)
	{
		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_GPU_GetFullName)(gpuHandles[0], szName);
		if(status != 0)
		{
			return false;
		}
		return true;
	}

	int vibrance::getActiveOutputs(int *gpuHandles[], int *outputIds[])
	{
		for(int i = 0; i < sizeof(gpuHandles)/sizeof(gpuHandles[0]); i++)
		{
			_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_GPU_GetActiveOutputs)(gpuHandles[0], outputIds[i]);
			if(status != 0)
			{
				return status;
			}
		}
		return *outputIds[0];
	}

	int vibrance::getGpuSystemType(int *gpuHandle)
	{
		NV_SYSTEM_TYPE nvSystemType;
		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_GPU_GetSystemType)(gpuHandle, &nvSystemType);
		if (status == 0)
		{
			return nvSystemType;
		}
		return NV_SYSTEM_TYPE::NV_SYSTEM_TYPE_UNKNOWN;
	}

	void *vibrance::getAssociatedNvidiaDisplayHandle(const char *szDisplayName, int length)
	{
		void *outputId = NULL;
		_NvAPI_Status status = (_NvAPI_Status)(*NvAPI_GetAssociatedNvidiaDisplayHandle)(szDisplayName, &outputId);
		if(status == 0)
		{
			return outputId;
		}
		return NULL;
	}

	namespace
	{
		using namespace nvapi_display;

		// NvAPI status codes used here (nvapi_lite_common.h _NvAPI_Status; also vibrance::_NvAPI_Status).
		const int ST_INVALID_ARGUMENT   = -5;
		const int ST_NO_IMPLEMENTATION  = -3;
		const int ST_DATA_NOT_FOUND     = -121;
		const int ST_OUT_OF_MEMORY      = -130;
		const int ST_INVALID_DISPLAY_ID = -187;

		// A whole display configuration as NvAPI_DISP_GetDisplayConfig hands it out. Every allocation
		// is owned here and released by release() (also from the destructor), whichever path the
		// caller leaves by.
		struct DisplayConfig
		{
			NvU32 count;
			NV_DISPLAYCONFIG_PATH_INFO *paths;
			std::vector<void *> blocks;   // every calloc'd block except paths itself

			DisplayConfig() : count(0), paths(NULL) {}
			~DisplayConfig() { release(); }

			void release()
			{
				for (size_t i = 0; i < blocks.size(); i++)
					free(blocks[i]);
				blocks.clear();
				free(paths);
				paths = NULL;
				count = 0;
			}

			void *alloc(size_t bytes)
			{
				void *p = calloc(1, bytes);
				if (p == NULL)
					return NULL;
				try { blocks.push_back(p); }
				catch (...) { free(p); return NULL; }
				return p;
			}

			// The documented three passes (nvapi.h:9033-9040): count, then paths (with sourceModeInfo),
			// then each path's targetInfo array and every target's advanced details.
			int load()
			{
				release();
				NvU32 n = 0;
				int st = (*NvAPI_DISP_GetDisplayConfig)(&n, NULL);
				if (st != 0) return st;
				if (n == 0) return ST_DATA_NOT_FOUND;

				paths = (NV_DISPLAYCONFIG_PATH_INFO *)calloc(n, sizeof(NV_DISPLAYCONFIG_PATH_INFO));
				if (paths == NULL) return ST_OUT_OF_MEMORY;
				count = n;
				for (NvU32 i = 0; i < n; i++)
				{
					paths[i].version = NVD_NV_DISPLAYCONFIG_PATH_INFO_VER;
					paths[i].sourceModeInfo = (NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1 *)alloc(sizeof(NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1));
					if (paths[i].sourceModeInfo == NULL) return ST_OUT_OF_MEMORY;
				}
				st = (*NvAPI_DISP_GetDisplayConfig)(&n, paths);
				if (st != 0) return st;
				if (n != count) return ST_INVALID_ARGUMENT;   // topology changed under us; caller may retry

				std::vector<NvU32> targetCounts(n);
				for (NvU32 i = 0; i < n; i++)
				{
					NvU32 t = paths[i].targetInfoCount;
					targetCounts[i] = t;
					if (t == 0) continue;
					paths[i].targetInfo = (NV_DISPLAYCONFIG_PATH_TARGET_INFO *)alloc(t * sizeof(NV_DISPLAYCONFIG_PATH_TARGET_INFO));
					NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO *details =
						(NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO *)alloc(t * sizeof(NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO));
					if (paths[i].targetInfo == NULL || details == NULL) return ST_OUT_OF_MEMORY;
					for (NvU32 j = 0; j < t; j++)
					{
						details[j].version = NVD_NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_VER;
						paths[i].targetInfo[j].details = &details[j];
					}
				}
				st = (*NvAPI_DISP_GetDisplayConfig)(&n, paths);
				if (st != 0) return st;
				// Same re-check as after pass 2: the buffers above are sized from pass 2's counts, so
				// a topology change since then must fail the load, never be read as if it still fit.
				if (n != count) return ST_INVALID_ARGUMENT;
				for (NvU32 i = 0; i < n; i++)
				{
					if (paths[i].targetInfoCount != targetCounts[i]) return ST_INVALID_ARGUMENT;
				}
				return 0;
			}

			// The advanced details of the target driving displayId, or NULL.
			NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO *find(NvU32 displayId)
			{
				for (NvU32 i = 0; i < count; i++)
					for (NvU32 j = 0; paths[i].targetInfo != NULL && j < paths[i].targetInfoCount; j++)
						if (paths[i].targetInfo[j].displayId == displayId)
							return paths[i].targetInfo[j].details;
				return NULL;
			}
		};

		bool displayScalingAvailable()
		{
			return NvAPI_DISP_GetDisplayIdByDisplayName != NULL && NvAPI_DISP_GetDisplayConfig != NULL && NvAPI_DISP_SetDisplayConfig != NULL;
		}

		bool isKnownScaling(int s)
		{
			return s == NV_SCALING_GPU_SCALING_TO_CLOSEST || s == NV_SCALING_GPU_SCALING_TO_NATIVE ||
				s == NV_SCALING_GPU_SCANOUT_TO_NATIVE || s == NV_SCALING_GPU_SCALING_TO_ASPECT_SCANOUT_TO_NATIVE ||
				s == NV_SCALING_GPU_SCALING_TO_ASPECT_SCANOUT_TO_CLOSEST || s == NV_SCALING_GPU_SCANOUT_TO_CLOSEST ||
				s == NV_SCALING_GPU_INTEGER_ASPECT_SCALING;
		}
	}

	int vibrance::getDisplayScaling(const char *gdiDisplayName, int *outScaling)
	{
		if (gdiDisplayName == NULL || outScaling == NULL) return ST_INVALID_ARGUMENT;
		if (!displayScalingAvailable()) return ST_NO_IMPLEMENTATION;

		NvU32 displayId = 0;
		int st = (*NvAPI_DISP_GetDisplayIdByDisplayName)(gdiDisplayName, &displayId);
		if (st != 0) return st;

		DisplayConfig cfg;
		st = cfg.load();
		if (st != 0) return st;
		NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO *d = cfg.find(displayId);
		if (d == NULL) return ST_INVALID_DISPLAY_ID;
		*outScaling = (int)d->scaling;
		return 0;
	}

	int vibrance::setDisplayScaling(const char *gdiDisplayName, int scaling)
	{
		if (gdiDisplayName == NULL || !isKnownScaling(scaling)) return ST_INVALID_ARGUMENT;
		if (!displayScalingAvailable()) return ST_NO_IMPLEMENTATION;

		NvU32 displayId = 0;
		int st = (*NvAPI_DISP_GetDisplayIdByDisplayName)(gdiDisplayName, &displayId);
		if (st != 0) return st;

		DisplayConfig cfg;
		st = cfg.load();
		if (st != 0) return st;
		NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO *d = cfg.find(displayId);
		if (d == NULL) return ST_INVALID_DISPLAY_ID;
		if ((int)d->scaling == scaling) return 0;   // already there: no modeset for a no-op

		d->scaling = (NV_SCALING)scaling;   // the only field changed; every path is replayed as read
		// SAVE_TO_PERSISTENCE (nvapi.h:1057) so the choice behaves like the NVIDIA Control Panel's, which
		// persists across reboot / driver reload; without it the driver treats the change as transient.
		// DRIVER_RELOAD_ALLOWED is deliberately NOT passed: it may blank every display to reload the driver.
		return (*NvAPI_DISP_SetDisplayConfig)(cfg.count, cfg.paths, NV_DISPLAYCONFIG_SAVE_TO_PERSISTENCE);
	}

	bool vibrance::unloadLibrary()
	{	
		int ret = (*NvAPI_Unload)();
		if(ret == 0)
			return true;
		return false;
	}

	bool vibrance::initializeLibrary()
	{
		// nvapi.dll only exists 32-bit; a 64-bit process must load nvapi64.dll instead.
#ifdef _WIN64
		HMODULE hmod = LoadLibraryA("nvapi64.dll");
#else
		HMODULE hmod = LoadLibraryA("nvapi.dll");
#endif
		if (hmod == NULL)
		{
			return false;
		}

		NvAPI_QueryInterface = (NvAPI_QueryInterface_t) GetProcAddress(hmod, "nvapi_QueryInterface");

		NvAPI_Initialize = (NvAPI_Initialize_t) (*NvAPI_QueryInterface)(0x0150E828);
		NvAPI_Unload = (NvAPI_Unload_t) (*NvAPI_QueryInterface)(0x0D22BDD7E);
		NvAPI_EnumPhysicalGPUs = (NvAPI_EnumPhysicalGPUs_t) (*NvAPI_QueryInterface)(0xE5AC921F);
		NvAPI_GPU_GetFullName = (NvAPI_GPU_GetFullName_t) (*NvAPI_QueryInterface)(0xCEEE8E9F);
		NvAPI_GPU_GetActiveOutputs = (NvAPI_GPU_GetActiveOutputs_t) (*NvAPI_QueryInterface)(0x0E3E89B6F);
		NvAPI_GetDVCInfo = (NvAPI_GetDVCInfo_t) (*NvAPI_QueryInterface)(0x4085DE45);
		NvAPI_SetDVCLevel = (NvAPI_SetDVCLevel_t) (*NvAPI_QueryInterface)(0x172409B4);
		NvAPI_EnumNvidiaDisplayHandle = (NvAPI_EnumNvidiaDisplayHandle_t) (*NvAPI_QueryInterface)(0x9ABDD40D);
		NvAPI_GetInterfaceVersionString = (NvAPI_GetInterfaceVersionString_t) (*NvAPI_QueryInterface)(0x1053FA5);
		NvAPI_GetErrorMessage = (NvAPI_GetErrorMessage_t) (*NvAPI_QueryInterface)(0x6C2D048C);
		NvAPI_GetDVCInfoEx = (NvAPI_GetDVCInfoEx_t) (*NvAPI_QueryInterface)(0x0E45002D);
		NvAPI_GetAssociatedNvidiaDisplayHandle = (NvAPI_GetAssociatedNvidiaDisplayHandle_t) (*NvAPI_QueryInterface)(0x35C29134);
		NvAPI_GPU_GetSystemType = (NvAPI_GPU_GetSystemType_t) (*NvAPI_QueryInterface)(0xBAAABFCC);

		// Display scaling. Ids are from nvapi_interface.h. Deliberately not part of the mandatory-pointer
		// check below: a driver without them must still initialise, and the display-scaling entry points
		// report NVAPI_NO_IMPLEMENTATION themselves.
		NvAPI_DISP_GetDisplayIdByDisplayName = (nvapi_display::NvAPI_DISP_GetDisplayIdByDisplayName_t) (*NvAPI_QueryInterface)(nvapi_display::ID_NvAPI_DISP_GetDisplayIdByDisplayName);
		NvAPI_DISP_GetDisplayConfig = (nvapi_display::NvAPI_DISP_GetDisplayConfig_t) (*NvAPI_QueryInterface)(nvapi_display::ID_NvAPI_DISP_GetDisplayConfig);
		NvAPI_DISP_SetDisplayConfig = (nvapi_display::NvAPI_DISP_SetDisplayConfig_t) (*NvAPI_QueryInterface)(nvapi_display::ID_NvAPI_DISP_SetDisplayConfig);

		if (NvAPI_Initialize == NULL || NvAPI_Unload == NULL ||
			NvAPI_EnumPhysicalGPUs == NULL ||NvAPI_GPU_GetFullName == NULL ||
			NvAPI_GPU_GetActiveOutputs == NULL || NvAPI_GetDVCInfo == NULL || 
			NvAPI_SetDVCLevel == NULL || NvAPI_EnumNvidiaDisplayHandle == NULL || 
			NvAPI_GetInterfaceVersionString == NULL || NvAPI_GetErrorMessage == NULL || 
			NvAPI_GetDVCInfoEx == NULL || NvAPI_GPU_GetSystemType == NULL)
		{
			return false;
		}

		int ret = (*NvAPI_Initialize)();
		if(ret == 0)
		{
			return true;
		}
		return false;
	}

	vibrance::vibrance(void)
	{
	}


	vibrance::~vibrance(void)
	{
	}

}