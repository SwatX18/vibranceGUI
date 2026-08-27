using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace vibrance.GUI.AMD.vendor
{
    public interface IAmdAdapter : IDisposable
    {
        void SetSaturationOnAllDisplays(int vibranceLevel);

        /// <summary>
        /// True only when at least one display actually matched displayName (or, for the
        /// SetSaturationOnAllDisplays fan-out, at least one display existed at all) AND every ADL
        /// call for a matched display returned ADL_OK. "No display matched, so nothing was even
        /// attempted" must report false, not true - see the implementations for why that
        /// distinction was previously unbuildable (this method used to return void).
        /// </summary>
        bool SetSaturationOnDisplay(int vibranceLevel, string displayName);

        bool IsAvailable();

        void Init();
    }
}