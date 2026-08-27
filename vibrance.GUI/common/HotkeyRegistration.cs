using System;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Owns the lifecycle of the toggle hotkey's single OS registration. Apply/Release below are
    /// the only two operations - deliberately not split into a "SetXxx" plus a separate
    /// "RestoreXxx"-shaped pair: a two-call contract here could be invoked out of order, or with
    /// the release half skipped, and silently leave a second registration behind.
    /// </summary>
    internal class HotkeyRegistration
    {
        // RegisterHotKey/UnregisterHotKey's own id parameter. One fixed value is enough - this
        // application only ever registers a single hotkey; VibranceGUI.cs's WndProc dispatches
        // WM_HOTKEY on wParam == HotkeyId.
        internal const int HotkeyId = 1;

        /// <summary>
        /// The single source of truth for "should a real OS registration exist right now" -
        /// VibranceGUI.ApplyToggleHotkey calls this instead of inlining the ternary itself, so a
        /// regression test can reach the actual gating expression production code runs (a fixture
        /// cannot instantiate a real Form to reflect into ApplyToggleHotkey directly). The
        /// checkbox gates registration, not just the presence of a saved binding - a binding can
        /// be fully configured (and shown in the textbox) while the checkbox is still unchecked,
        /// and must register nothing until the user turns it on.
        /// </summary>
        internal static HotkeyBinding EffectiveBinding(bool enabled, HotkeyBinding binding)
        {
            return (enabled && binding.IsSet) ? binding : HotkeyBinding.None;
        }

        private readonly IHotkeyRegistrar _registrar;

        // The handle Apply last registered against - deliberately cached here rather than read
        // back from the owning form when Release runs. See Release's own comment for why: the
        // form's Handle is not stable for the form's lifetime.
        private IntPtr _registeredHandle = IntPtr.Zero;
        private bool _isRegistered;

        internal HotkeyRegistration(IHotkeyRegistrar registrar)
        {
            _registrar = registrar;
        }

        internal bool IsRegistered
        {
            get { return _isRegistered; }
        }

        /// <summary>
        /// Releases whatever this instance currently has registered, then attempts to bind
        /// against hWnd. Never leaves a stale registration behind: on any outcome other than
        /// Registered, IsRegistered is false and nothing is registered - even if the PRIOR
        /// binding (just released above) had succeeded.
        /// </summary>
        internal HotkeyRegistrationResult Apply(IntPtr hWnd, HotkeyBinding binding)
        {
            Release();

            if (!binding.IsSet)
            {
                return HotkeyRegistrationResult.NotConfigured;
            }

            HotkeyRegistrationResult result = _registrar.Register(hWnd, HotkeyId, binding.Modifiers, binding.VirtualKey);

            if (result == HotkeyRegistrationResult.Failed && (binding.Modifiers & HotkeyBindingParser.ModNoRepeat) != 0)
            {
                // Compatibility retry: some Windows builds reject MOD_NOREPEAT combined with
                // certain virtual keys (ERROR_INVALID_PARAMETER, 87). IHotkeyRegistrar has no
                // channel for the raw Win32 error code (see its own header comment), so this
                // retries once, without the bit, on every generic Failed result - never on
                // AlreadyOwnedByAnotherApplication, which is its own outcome and would fail the
                // same way again regardless of MOD_NOREPEAT.
                uint modifiersWithoutNoRepeat = binding.Modifiers & ~HotkeyBindingParser.ModNoRepeat;
                result = _registrar.Register(hWnd, HotkeyId, modifiersWithoutNoRepeat, binding.VirtualKey);
            }

            if (result == HotkeyRegistrationResult.Registered)
            {
                _registeredHandle = hWnd;
                _isRegistered = true;
            }

            return result;
        }

        /// <summary>
        /// Unregisters against the handle captured AT REGISTRATION TIME - never against a handle
        /// passed in here, and never against the owning form's current Handle. Defensive, not a
        /// fix for a confirmed live defect: a Form's Handle is not stable for its lifetime in
        /// general - any property whose setter forces RecreateHandle (WinForms documents several)
        /// would silently orphan a registration made against the old handle if Release ever read
        /// a fresh one instead of the one Apply actually used. Caching it here is strictly more
        /// correct than reading it fresh, independent of whether anything in THIS codebase
        /// currently triggers a recreate. A no-op when nothing is currently registered.
        /// </summary>
        internal void Release()
        {
            if (!_isRegistered)
            {
                return;
            }

            _registrar.Unregister(_registeredHandle, HotkeyId);
            _isRegistered = false;
            _registeredHandle = IntPtr.Zero;
        }
    }
}
