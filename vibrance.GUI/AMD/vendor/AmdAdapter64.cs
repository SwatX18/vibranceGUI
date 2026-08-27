using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using vibrance.GUI.AMD.vendor.adl64;

namespace vibrance.GUI.AMD.vendor
{
    public class AmdAdapter64 : IAmdAdapter
    {
        private List<Display> displays;
        private Disposer disposer;

        public void Init()
        {
            displays = new List<Display>();
            disposer = new Disposer();
            
            int numberOfAdapters = 0;

            Adl.AdlMainControlCreate(Adl.AdlMainMemoryAlloc, 1);

            if (Adl.AdlAdapterNumberOfAdaptersGet != null)
            {
                Adl.AdlAdapterNumberOfAdaptersGet(ref numberOfAdapters);
            }

            Adl.AdlMainControlCreate(Adl.AdlMainMemoryAlloc, 1);

            if (numberOfAdapters > 0)
            {
                AdlAdapterInfoArray osAdapterInfoData = new AdlAdapterInfoArray();

                if (Adl.AdlAdapterAdapterInfoGet != null)
                {
                    int size = Marshal.SizeOf(osAdapterInfoData);
                    IntPtr adapterBuffer = Marshal.AllocCoTaskMem(size);
                    Marshal.StructureToPtr(osAdapterInfoData, adapterBuffer, false);

                    int adlRet = Adl.AdlAdapterAdapterInfoGet(adapterBuffer, size);
                    if (adlRet == Adl.AdlSuccess)
                    {
                        osAdapterInfoData = (AdlAdapterInfoArray)Marshal.PtrToStructure(adapterBuffer, osAdapterInfoData.GetType());
                        int isActive = 0;

                        for (int i = 0; i < numberOfAdapters; i++)
                        {
                            AdlAdapterInfo adlAdapterInfo = osAdapterInfoData.ADLAdapterInfo[i];

                            int adapterIndex = adlAdapterInfo.AdapterIndex;

                            if (Adl.AdlAdapterActiveGet != null)
                            {
                                adlRet = Adl.AdlAdapterActiveGet(adlAdapterInfo.AdapterIndex, ref isActive);
                            }

                            if (Adl.AdlSuccess == adlRet)
                            {
                                AdlDisplayInfo oneDisplayInfo = new AdlDisplayInfo();

                                if (Adl.AdlDisplayDisplayInfoGet != null)
                                {
                                    IntPtr displayBuffer = IntPtr.Zero;

                                    int numberOfDisplays = 0;
                                    adlRet = Adl.AdlDisplayDisplayInfoGet(adlAdapterInfo.AdapterIndex, ref numberOfDisplays, out displayBuffer, 1);
                                    if (Adl.AdlSuccess == adlRet)
                                    {
                                        List<AdlDisplayInfo> displayInfoData = new List<AdlDisplayInfo>();
                                        for (int j = 0; j < numberOfDisplays; j++)
                                        {
                                            oneDisplayInfo = (AdlDisplayInfo)Marshal.PtrToStructure(new IntPtr(displayBuffer.ToInt64() + j * Marshal.SizeOf(oneDisplayInfo)), oneDisplayInfo.GetType());
                                            displayInfoData.Add(oneDisplayInfo);
                                        }

                                        for (int j = 0; j < numberOfDisplays; j++)
                                        {
                                            AdlDisplayInfo adlDisplayInfo = displayInfoData[j];

                                            if (adlDisplayInfo.DisplayID.DisplayLogicalAdapterIndex == -1)
                                            {
                                                continue;
                                            }

                                            displays.Add(new Display
                                            {
                                                DisplayInfo = adlDisplayInfo,
                                                AdapterInfo = adlAdapterInfo,
                                                Index = adapterIndex,
                                            });
                                        }
                                    }

                                    disposer.DisplayBufferList.Add(displayBuffer);
                                }
                            }
                        }
                    }

                    disposer.AdapterBuffer = adapterBuffer;
                }
            }
        }

        public bool IsAvailable()
        {
            if (Adl.AdlMainControlCreate != null)
            {
                if (Adl.AdlSuccess == Adl.AdlMainControlCreate(Adl.AdlMainMemoryAlloc, 1))
                {
                    if (Adl.AdlMainControlDestroy != null)
                    {
                        Adl.AdlMainControlDestroy();
                    }

                    return true;
                }
            }

            return false;
        }

        public void SetSaturationOnAllDisplays(int vibranceLevel)
        {
            this.SetSaturationOnDisplay(vibranceLevel, null);
        }

        public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
        {
            // matchedAny/allSucceeded are closed over by the handler below, the same way the
            // pre-existing lambda already closes over vibranceLevel/displayName - SetSaturation
            // itself stays a void-returning Action, only what its handler does with the result
            // changes. "No display matched" (matchedAny stays false) must report false, not the
            // vacuous "true" an empty loop would otherwise imply - see IAmdAdapter's own comment.
            bool matchedAny = false;
            bool allSucceeded = true;
            SetSaturation((adlDisplayInfo, adlAdapterInfo, adapterIndex) =>
            {
                int infoValue = adlDisplayInfo.DisplayID.DisplayLogicalIndex;
                bool adapterIsAssociatedWithDisplay = adapterIndex == adlDisplayInfo.DisplayID.DisplayLogicalAdapterIndex;
                if (adapterIsAssociatedWithDisplay && (adlAdapterInfo.DisplayName == displayName || displayName == null))
                {
                    matchedAny = true;

                    // Adl.AdlDisplayColorSet can be null - IsFunctionValid (ADLCheckLibrary.cs)
                    // failed to resolve "ADL_Display_Color_Set" from the driver's DLL. The
                    // pre-existing call below was unguarded against that (a latent NRE); guarded
                    // here since this line is already being touched for the status-code fix.
                    if (Adl.AdlDisplayColorSet == null)
                    {
                        allSucceeded = false;
                        return;
                    }

                    // AdlSuccess (= 0) is ADL_OK - reusing the constant this file already defines
                    // and already checks every other ADL return code against, rather than adding
                    // a second name for the same value.
                    int adlStatus = Adl.AdlDisplayColorSet(
                        adapterIndex,
                        infoValue,
                        Adl.AdlDisplayColorSaturation,
                        vibranceLevel);
                    if (adlStatus != Adl.AdlSuccess)
                    {
                        allSucceeded = false;
                    }
                }
            });
            return matchedAny && allSucceeded;
        }

        private void SetSaturation(Action<AdlDisplayInfo, AdlAdapterInfo, int> handle)
        {
            foreach (var display in displays)
            {
                handle(display.DisplayInfo, display.AdapterInfo, display.Index);
            }
        }

        public void Dispose()
        {
            disposer?.Dispose();
        }

        class Display
        {
            public AdlDisplayInfo DisplayInfo { get; set; }

            public AdlAdapterInfo AdapterInfo { get; set; }

            public int Index { get; set; }
        }

        class Disposer : IDisposable
        {
            public Disposer()
            {
                DisplayBufferList = new List<IntPtr>();
            }

            public List<IntPtr> DisplayBufferList { get; set; }
            public IntPtr AdapterBuffer { get; set; }

            public void Dispose()
            {
                foreach (var intPtr in DisplayBufferList)
                {
                    if (intPtr != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(intPtr);
                    }
                }

                if (AdapterBuffer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(AdapterBuffer);
                }

                if (Adl.AdlMainControlDestroy != null)
                {
                    Adl.AdlMainControlDestroy();
                }
            }
        }
    }
}