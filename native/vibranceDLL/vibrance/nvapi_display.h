// Minimal NvAPI display-config types, copied from NVIDIA's official NvAPI SDK:
//   https://github.com/NVIDIA/nvapi  (nvapi.h, nvapi_interface.h; fetched from `main`, 2026-09-29)
// Licence: MIT, SPDX-FileCopyrightText: Copyright (c) 2019-2026 NVIDIA CORPORATION & AFFILIATES.
// The licence text ships beside the vibrance project as native/vibranceDLL/NVAPI_LICENSE.txt and its
// notice ("The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software") applies to the declarations below.
//
// Only what NvAPI_DISP_GetDisplayConfig / NvAPI_DISP_SetDisplayConfig need is reproduced. Struct
// layouts, enum values and the version macro are verbatim from those headers - the driver
// validates the version's embedded sizeof, so a wrong layout fails with
// NVAPI_INCOMPATIBLE_STRUCT_VERSION at best. Do not "tidy" any field. Line numbers below are
// nvapi.h's (approximate where marked ~).
#pragma once

// nvapi.h:47 "#pragma pack(push,8) // Make sure we have consistent structure packings"
#pragma pack(push,8)

namespace nvapi_display
{
	typedef unsigned int   NvU32;   // nvapi_lite_common.h
	typedef unsigned short NvU16;
	typedef unsigned char  NvU8;
	typedef int            NvS32;

	// nvapi_lite_common.h:248
#define NVD_MAKE_NVAPI_VERSION(typeName,ver) (NvU32)(sizeof(typeName) | ((ver)<<16))

	// nvapi.h:508-528 (_NV_SCALING). Values are verbatim; the legacy aliases are omitted.
	typedef enum _NV_SCALING
	{
		NV_SCALING_DEFAULT                                  = 0,
		NV_SCALING_GPU_SCALING_TO_CLOSEST                   = 1,  // Balanced  - Full Screen
		NV_SCALING_GPU_SCALING_TO_NATIVE                    = 2,  // Force GPU - Full Screen
		NV_SCALING_GPU_SCANOUT_TO_NATIVE                    = 3,  // Force GPU - Centered\No Scaling
		NV_SCALING_GPU_SCALING_TO_ASPECT_SCANOUT_TO_NATIVE  = 5,  // Force GPU - Aspect Ratio
		NV_SCALING_GPU_SCALING_TO_ASPECT_SCANOUT_TO_CLOSEST = 6,  // Balanced  - Aspect Ratio
		NV_SCALING_GPU_SCANOUT_TO_CLOSEST                   = 7,  // Balanced  - Centered\No Scaling
		NV_SCALING_GPU_INTEGER_ASPECT_SCALING               = 8,  // Force GPU - Integer Scaling
		NV_SCALING_CUSTOMIZED                               = 255
	} NV_SCALING;

	// nvapi.h:~533-540 (_NV_ROTATE)
	typedef enum _NV_ROTATE
	{
		NV_ROTATE_0 = 0, NV_ROTATE_90 = 1, NV_ROTATE_180 = 2, NV_ROTATE_270 = 3, NV_ROTATE_IGNORED = 4
	} NV_ROTATE;

	// NV_GPU_CONNECTOR_TYPE (nvapi.h:~370-397), NV_DISPLAY_TV_FORMAT (:~400-479), NV_TIMING_OVERRIDE
	// (:578-597) and NV_FORMAT (:~550-560) are plain C enums, 4 bytes / 4-aligned. This code never
	// reads or writes them (they are only carried through Get -> Set untouched), so only their size
	// and alignment matter; they are typed NvU32 rather than reproducing ~100 enumerators.
	typedef NvU32 NV_GPU_CONNECTOR_TYPE;
	typedef NvU32 NV_DISPLAY_TV_FORMAT;
	typedef NvU32 NV_TIMING_OVERRIDE;
	typedef NvU32 NV_FORMAT;

	// nvapi.h:610-619
	typedef struct tagNV_TIMINGEXT
	{
		NvU32   flag;
		NvU16   rr;
		NvU32   rrx1k;
		NvU32   aspect;
		NvU16   rep;
		NvU32   status;
		NvU8    name[40];
	} NV_TIMINGEXT;

	// nvapi.h:~648-672
	typedef struct _NV_TIMING
	{
		NvU16 HVisible;
		NvU16 HBorder;
		NvU16 HFrontPorch;
		NvU16 HSyncWidth;
		NvU16 HTotal;
		NvU8  HSyncPol;

		NvU16 VVisible;
		NvU16 VBorder;
		NvU16 VFrontPorch;
		NvU16 VSyncWidth;
		NvU16 VTotal;
		NvU8  VSyncPol;

		NvU16 interlaced;
		NvU32 pclk;

		NV_TIMINGEXT etc;
	} NV_TIMING;

	// nvapi.h:~880-897
	typedef struct _NV_RESOLUTION
	{
		NvU32 width;
		NvU32 height;
		NvU32 colorDepth;
	} NV_RESOLUTION;

	typedef struct _NV_POSITION
	{
		NvS32 x;
		NvS32 y;
	} NV_POSITION;

	// nvapi.h:~905-950 (NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1). NV_PAN_AND_SCAN_DEFINED is
	// not defined by the SDK by default, so the third bit is "reservedBit1".
	typedef struct _NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1
	{
		NvU32                   version;
		NV_ROTATE               rotation;
		NV_SCALING              scaling;
		NvU32                   refreshRate1K;
		NvU32                   interlaced:1;
		NvU32                   primary:1;
		NvU32                   reservedBit1:1;
		NvU32                   disableVirtualModeSupport:1;
		NvU32                   isPreferredUnscaledTarget:1;
		NvU32                   reserved:27;
		NV_GPU_CONNECTOR_TYPE   connector;
		NV_DISPLAY_TV_FORMAT    tvFormat;
		NV_TIMING_OVERRIDE      timingOverride;
		NV_TIMING               timing;
	} NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1;
	typedef NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1 NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO;
	// nvapi.h:952-955
#define NVD_NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_VER NVD_MAKE_NVAPI_VERSION(NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1,1)

	// nvapi.h:965-970 (NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2 == current NV_DISPLAYCONFIG_PATH_TARGET_INFO)
	typedef struct _NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2
	{
		NvU32                                           displayId;
		NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO*     details;
		NvU32                                           targetId;
	} NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2;
	typedef NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2 NV_DISPLAYCONFIG_PATH_TARGET_INFO;

	// nvapi.h:987-998 (NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1)
	typedef struct _NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1
	{
		NV_RESOLUTION   resolution;
		NV_FORMAT       colorFormat;
		NV_POSITION     position;
		NvU32           spanningOrientation;   // NV_DISPLAYCONFIG_SPANNING_ORIENTATION, a 4-byte enum
		NvU32           bGDIPrimary : 1;
		NvU32           bSLIFocus : 1;
		NvU32           reserved : 30;
	} NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1;

	// nvapi.h:1019-1034 (NV_DISPLAYCONFIG_PATH_INFO_V2)
	typedef struct _NV_DISPLAYCONFIG_PATH_INFO_V2
	{
		NvU32                                   version;
		union {
			NvU32                               sourceId;
			NvU32                               reserved_sourceId;
		};
		NvU32                                   targetInfoCount;
		NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2*   targetInfo;
		NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1*   sourceModeInfo;
		NvU32                                   IsNonNVIDIAAdapter : 1;
		NvU32                                   reserved : 31;
		void                                    *pOSAdapterID;
	} NV_DISPLAYCONFIG_PATH_INFO_V2;
	typedef NV_DISPLAYCONFIG_PATH_INFO_V2 NV_DISPLAYCONFIG_PATH_INFO;
	// nvapi.h:1040,1046
#define NVD_NV_DISPLAYCONFIG_PATH_INFO_VER NVD_MAKE_NVAPI_VERSION(NV_DISPLAYCONFIG_PATH_INFO_V2,2)

	// nvapi.h:1054-1061 (NV_DISPLAYCONFIG_FLAGS)
	enum
	{
		NV_DISPLAYCONFIG_VALIDATE_ONLY          = 0x00000001,
		NV_DISPLAYCONFIG_SAVE_TO_PERSISTENCE    = 0x00000002,
		NV_DISPLAYCONFIG_DRIVER_RELOAD_ALLOWED  = 0x00000004,
		NV_DISPLAYCONFIG_FORCE_MODE_ENUMERATION = 0x00000008,
		NV_FORCE_COMMIT_VIDPN                   = 0x00000010
	};

	// nvapi_interface.h:183,185,186 - NvAPI_QueryInterface ids
	const NvU32 ID_NvAPI_DISP_GetDisplayIdByDisplayName = 0xae457190;
	const NvU32 ID_NvAPI_DISP_GetDisplayConfig          = 0x11abccf8;
	const NvU32 ID_NvAPI_DISP_SetDisplayConfig          = 0x5d8cf8de;

	// nvapi.h:9025, 9058, 9091 signatures (NVAPI_INTERFACE is NvAPI_Status __cdecl)
	typedef int (*NvAPI_DISP_GetDisplayIdByDisplayName_t)(const char *displayName, NvU32 *displayId);
	typedef int (*NvAPI_DISP_GetDisplayConfig_t)(NvU32 *pathInfoCount, NV_DISPLAYCONFIG_PATH_INFO *pathInfo);
	typedef int (*NvAPI_DISP_SetDisplayConfig_t)(NvU32 pathInfoCount, NV_DISPLAYCONFIG_PATH_INFO *pathInfo, NvU32 flags);
}

#pragma pack(pop)
