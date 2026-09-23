using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace vibrance.GUI.NVIDIA
{
    /// <summary>
    /// Binding-layer regression coverage for the vibranceDLL.dll-from-source rebuild (see
    /// native\vibranceDLL): proves the embedded resource is the right shape (§N2 is the tripwire a
    /// future x64 slice must trip if the DLL is not rebuilt for that platform too), that every
    /// NvidiaDynamicVibranceProxy [DllImport] still resolves and prelinks cleanly, that every one
    /// of them still declares CallingConvention.Cdecl (§N18 - resolving is not enough on its own,
    /// see that check's own comment), that there are still exactly 12 of them (§N19), and that the
    /// original 12 mangled __thiscall exports are still present - i.e. that native/vibranceDLL's
    /// vibrance_c.h/.cpp wrapper layer stayed additive and nothing was removed from vibrance.h/.cpp.
    ///
    /// No GUI, no live GPU driver, no display write: the DLL's own load-time imports are only
    /// KERNEL32/USER32 (VERIFIED for this build - no VCRUNTIME/MSVCP/api-ms-win-crt-*, see
    /// native/vibranceDLL's README.md item 3), and nvapi.dll is resolved dynamically inside
    /// initializeLibrary(), which this fixture never calls. Marshal.Prelink/PrelinkAll resolve
    /// entry points and build marshalling stubs; they do not invoke anything.
    ///
    /// Critically, this does NOT call CommonUtils.LoadUnmanagedLibraryFromResource - that writes
    /// %APPDATA%\vibranceGUI\vibranceDLL.dll, which a real vibranceGUI instance already running on
    /// this machine has open, so File.WriteAllBytes against it throws a sharing-violation
    /// IOException that has nothing to do with whether this binding layer is correct. A normal
    /// --selftest-nvapi run cannot actually hit that collision - the single-instance mutex
    /// (Program.cs:78, bail at :92) is checked before any --selftest-* flag dispatches (the first
    /// at :120), so a second launch bails out long before reaching this fixture. What DOES reach it
    /// while a real instance is running is the headless reflection harness (see the docs guide's
    /// §3.7): it calls NvidiaInteropFixture.Run() directly, bypassing Main() - and therefore the
    /// mutex - entirely, so the live instance's file lock on %APPDATA%\vibranceGUI\vibranceDLL.dll
    /// is reachable that way even though a normal second launch never gets there. Instead this
    /// extracts to a fixture-private directory and loads it by absolute path
    /// (kernel32!LoadLibrary(fullPath)) with no SetDllDirectory call at all: once a module is loaded
    /// under a given base file name, Windows' loader satisfies a later bare-name lookup - which is
    /// what DllImport("vibranceDLL.dll") on NvidiaDynamicVibranceProxy's own P/Invokes is - against
    /// that already-loaded module, without needing the private directory on any search path. (Load
    /// order matters: Prelinking before this load throws DllNotFoundException, because the private
    /// directory genuinely is not on the search path on its own.)
    ///
    /// Run by vibrance.GUI.exe --selftest-nvapi.
    /// </summary>
    public static class NvidiaInteropFixture
    {
        private const string EmbeddedDllResourceName = "vibrance.GUI.NVIDIA.vibranceDLL.dll";

        // The 12 mangled __thiscall export names NvidiaDynamicVibranceProxy.cs bound before
        // work/native-dll-from-source moved it to the undecorated vibrance_* names (see
        // docs/CODEBASE_GUIDE.md §7.3). Hardcoded rather than read live off the proxy - the proxy no
        // longer references them at all - specifically so N17 below proves the *native* side still
        // carries them (i.e. vibrance.h/.cpp were genuinely left untouched), not merely that nobody
        // deleted the new wrapper layer.
        private static readonly string[] OriginalMangledExportNames =
        {
            "?initializeLibrary@vibrance@vibranceDLL@@QAE_NXZ",
            "?unloadLibrary@vibrance@vibranceDLL@@QAE_NXZ",
            "?getActiveOutputs@vibrance@vibranceDLL@@QAEHQAPAH0@Z",
            "?enumeratePhsyicalGPUs@vibrance@vibranceDLL@@QAEXQAPAH@Z",
            "?getGpuName@vibrance@vibranceDLL@@QAE_NQAPAHPAD@Z",
            "?getDVCInfo@vibrance@vibranceDLL@@QAE_NPAUNV_DISPLAY_DVC_INFO@12@H@Z",
            "?enumerateNvidiaDisplayHandle@vibrance@vibranceDLL@@QAEHH@Z",
            "?setDVCLevel@vibrance@vibranceDLL@@QAE_NHH@Z",
            "?isWindowActive@vibrance@vibranceDLL@@QAE_NPAPAUHWND__@@@Z",
            "?equalsDVCLevel@vibrance@vibranceDLL@@QAE_NHH@Z",
            "?getGpuSystemType@vibrance@vibranceDLL@@QAEHPAH@Z",
            "?getAssociatedNvidiaDisplayHandle@vibrance@vibranceDLL@@QAEHPBDH@Z",
        };

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI NVIDIA interop self test");
            checklist.Lines.Add(string.Empty);

            byte[] resourceBytes = CheckResourceExists(checklist);
            if (resourceBytes == null)
            {
                // Nothing below can run without the DLL's own bytes - report what we have and stop,
                // rather than let every later check fail on a null/empty array for the same root cause.
                checklist.Lines.Add(string.Empty);
                checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
                return checklist.Lines;
            }

            CheckMachineTypeMatchesProcess(checklist, resourceBytes);

            string dllPath = TryExtractToPrivateDirectory(checklist, resourceBytes);
            if (dllPath == null)
            {
                // The extraction itself failed (e.g. a concurrent run's modal MessageBox is still
                // holding this same fixed path loaded, see the comment below) - nothing past this
                // point can run against a DLL that was never written, so stop here rather than let
                // LoadLibrary(null) or similar report a confusing secondary failure.
                checklist.Lines.Add(string.Empty);
                checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
                return checklist.Lines;
            }
            IntPtr hModule = CheckLoadLibraryByAbsolutePath(checklist, dllPath);

            CheckPrelinkEachBoundMethod(checklist);
            CheckPrelinkAll(checklist);
            CheckOriginalMangledExportsStillResolve(checklist, hModule);
            CheckEveryBoundMethodIsCdecl(checklist);
            CheckBoundMethodCount(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // N1.
        private static byte[] CheckResourceExists(Checklist checklist)
        {
            checklist.Lines.Add("N1: embedded vibranceDLL.dll resource exists and is non-empty:");
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedDllResourceName))
            {
                if (stream == null)
                {
                    checklist.Check(false, "GetManifestResourceStream(\"" + EmbeddedDllResourceName + "\") returned null");
                    return null;
                }

                byte[] bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        break;
                    }
                    offset += read;
                }
                checklist.Check(bytes.Length > 0, string.Format("resource is {0} bytes", bytes.Length));
                return bytes.Length > 0 ? bytes : null;
            }
        }

        // N2. The Slice 2 tripwire: a 64-bit vibrance.GUI build that still ships the x86 DLL (or
        // vice versa) fails here immediately and by name, rather than as a mysterious later
        // DllNotFoundException / BadImageFormatException from the real loader.
        private static void CheckMachineTypeMatchesProcess(Checklist checklist, byte[] image)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N2: PE machine type matches this process's bitness:");
            try
            {
                int peHeaderOffset = BitConverter.ToInt32(image, 0x3C);
                ushort machine = BitConverter.ToUInt16(image, peHeaderOffset + 4);
                const ushort ImageFileMachineI386 = 0x014C;
                const ushort ImageFileMachineAmd64 = 0x8664;
                ushort expected = IntPtr.Size == 4 ? ImageFileMachineI386 : ImageFileMachineAmd64;
                checklist.Check(machine == expected,
                    string.Format("machine=0x{0:X4}, expected 0x{1:X4} for a {2}-bit process (IntPtr.Size={3})",
                        machine, expected, IntPtr.Size * 8, IntPtr.Size));
            }
            catch (Exception ex)
            {
                checklist.Check(false, "failed to parse the PE header: " + ex.Message);
            }
        }

        // Not its own N-numbered check - it is bookkeeping, not a claim about the binding layer -
        // but still reported and counted: a sharing violation here must show up as a [FAIL] line in
        // the fixture's own output, not crash whatever process is running the fixture. Reachable in
        // practice: --selftest-nvapi's MessageBox is modal and holds this exact fixed path's file
        // loaded for as long as it is on screen, so a second concurrent run collides with the first.
        //
        // The extracted file is deliberately left behind afterwards, not deleted: the path is fixed
        // and gets overwritten on every run (so a stale copy cannot linger unnoticed), and while a
        // module is loaded from it Windows would not allow deleting it anyway.
        private static string TryExtractToPrivateDirectory(Checklist checklist, byte[] image)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("Extract the embedded DLL to a fixture-private path:");
            try
            {
                string privateDir = Path.Combine(Path.GetTempPath(), "vibranceGUI-NvidiaInteropFixture");
                if (!Directory.Exists(privateDir))
                {
                    Directory.CreateDirectory(privateDir);
                }
                string dllPath = Path.Combine(privateDir, "vibranceDLL.dll");
                File.WriteAllBytes(dllPath, image);
                checklist.Check(true, "wrote " + dllPath);
                return dllPath;
            }
            catch (Exception ex)
            {
                checklist.Check(false, "failed to extract to a private directory: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // N3.
        private static IntPtr CheckLoadLibraryByAbsolutePath(Checklist checklist, string dllPath)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N3: LoadLibrary by absolute path (no %APPDATA% write, no SetDllDirectory) succeeds:");
            IntPtr hModule = LoadLibrary(dllPath);
            checklist.Check(hModule != IntPtr.Zero,
                "LoadLibrary(\"" + dllPath + "\") returned a non-null module handle");
            return hModule;
        }

        // N4-N15, one check per bound method - resolves the entry point and builds its marshalling
        // stub without calling it, so a missing export and an unmarshalable signature are both
        // caught here, and a failure names the specific method rather than just "something broke".
        private static void CheckPrelinkEachBoundMethod(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N4-N15: Marshal.Prelink resolves each of the 12 bound methods individually:");
            foreach (MethodInfo method in GetBoundMethods().OrderBy(m => m.Name))
            {
                try
                {
                    Marshal.Prelink(method);
                    checklist.Check(true, method.Name + ": entry point resolved, marshalling stub built");
                }
                catch (Exception ex)
                {
                    checklist.Check(false, method.Name + ": Prelink threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // N16 - literally the Program.cs:344 call (inside the real NVIDIA startup branch), run here
        // in isolation.
        private static void CheckPrelinkAll(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N16: Marshal.PrelinkAll(typeof(NvidiaDynamicVibranceProxy)) succeeds:");
            try
            {
                Marshal.PrelinkAll(typeof(NvidiaDynamicVibranceProxy));
                checklist.Check(true, "PrelinkAll completed with no exception");
            }
            catch (Exception ex)
            {
                checklist.Check(false, "PrelinkAll threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // N17 - proves native/vibranceDLL/vibrance/vibrance_c.h/.cpp stayed purely additive: every
        // export NvidiaDynamicVibranceProxy used to bind before this work is still in the DLL, even
        // though nothing in C# references any of them by name any more.
        private static void CheckOriginalMangledExportsStillResolve(Checklist checklist, IntPtr hModule)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N17: all 12 original mangled exports still resolve (the change stayed additive):");
            if (hModule == IntPtr.Zero)
            {
                checklist.Check(false, "no module handle from N3 - cannot call GetProcAddress");
                return;
            }

            List<string> missing = new List<string>();
            foreach (string name in OriginalMangledExportNames)
            {
                if (GetProcAddress(hModule, name) == IntPtr.Zero)
                {
                    missing.Add(name);
                }
            }
            int resolvedCount = OriginalMangledExportNames.Length - missing.Count;
            string detail = missing.Count == 0
                ? string.Format("{0}/{1} original mangled exports resolved via GetProcAddress",
                    resolvedCount, OriginalMangledExportNames.Length)
                : string.Format("{0}/{1} original mangled exports resolved via GetProcAddress; missing: {2}",
                    resolvedCount, OriginalMangledExportNames.Length, string.Join(", ", missing.ToArray()));
            checklist.Check(missing.Count == 0, detail);
        }

        // N18 - a coverage gap in an earlier draft of this fixture: Marshal.Prelink on a
        // CallingConvention.StdCall-declared binding against a __cdecl export succeeds with no
        // exception (only a *renamed* export throws), so N4-N15 alone would let a Cdecl->StdCall
        // regression through with every check still green while corrupting the x86 stack on every
        // NVIDIA call. This reads the attribute itself instead of relying on Prelink to notice.
        private static void CheckEveryBoundMethodIsCdecl(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N18: every bound method declares CallingConvention.Cdecl:");
            foreach (MethodInfo method in GetBoundMethods().OrderBy(m => m.Name))
            {
                DllImportAttribute attr = (DllImportAttribute)Attribute.GetCustomAttribute(method, typeof(DllImportAttribute));
                checklist.Check(attr.CallingConvention == CallingConvention.Cdecl,
                    string.Format("{0}: CallingConvention.{1}", method.Name, attr.CallingConvention));
            }
        }

        // N19 - nothing above asserts how many methods GetBoundMethods() actually found, so
        // deleting a [DllImport] would otherwise just silently shrink N4-N15/N18 (e.g. to 11/11)
        // instead of failing anything.
        private static void CheckBoundMethodCount(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N19: exactly 12 methods are DllImport-bound on NvidiaDynamicVibranceProxy:");
            int count = GetBoundMethods().Count;
            checklist.Check(count == 12, string.Format("found {0}", count));
        }

        // Reads straight off the live [DllImport] declarations via reflection rather than a
        // hand-copied list of names, so N4-N15 cannot silently drift out of sync with
        // NvidiaDynamicVibranceProxy.cs the way a duplicated literal list could.
        private static List<MethodInfo> GetBoundMethods()
        {
            List<MethodInfo> methods = new List<MethodInfo>();
            foreach (MethodInfo method in typeof(NvidiaDynamicVibranceProxy).GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (Attribute.GetCustomAttribute(method, typeof(DllImportAttribute)) is DllImportAttribute)
                {
                    methods.Add(method);
                }
            }
            return methods;
        }

        private class Checklist
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Total;

            public void Check(bool condition, string description)
            {
                Total++;
                if (condition)
                    Passed++;
                Lines.Add(string.Format("[{0}] {1}", condition ? "PASS" : "FAIL", description));
            }
        }
    }
}
