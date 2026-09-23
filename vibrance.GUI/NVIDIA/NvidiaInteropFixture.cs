using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace vibrance.GUI.NVIDIA
{
    /// <summary>
    /// Binding-layer regression coverage for the vibranceDLL.dll-from-source rebuild (see
    /// native\vibranceDLL): proves the right one of the two embedded builds is picked for this
    /// process's bitness (§N0 tests the selection itself; §N2 is the complementary tripwire - a
    /// 64-bit build that still ships the x86 DLL, or vice versa, fails there even if N0's own logic
    /// were somehow wrong too), that every NvidiaDynamicVibranceProxy [DllImport] still resolves and
    /// prelinks cleanly, that every one of them still declares CallingConvention.Cdecl (§N18 -
    /// resolving is not enough on its own, see that check's own comment), that there are still
    /// exactly 12 of them (§N19), that every one of their parameter/return types is exactly what the
    /// handle-width fix expects (§N20 - resolving and prelinking cleanly is not enough here either;
    /// see that check's own comment for why), that a value actually round-trips intact through the
    /// real ABI boundary with no GPU attached (§N23 - the one check here Marshal.Prelink cannot
    /// substitute for), and that the original 12 mangled __thiscall exports are still present - i.e.
    /// that native/vibranceDLL's vibrance_c.h/.cpp wrapper layer stayed additive and nothing was
    /// removed from vibrance.h/.cpp.
    ///
    /// No GUI, no live GPU driver, no display write: the DLL's own load-time imports are only
    /// KERNEL32/USER32 (VERIFIED for this build - no VCRUNTIME/MSVCP/api-ms-win-crt-*, see
    /// native/vibranceDLL's README.md item 3), and nvapi.dll/nvapi64.dll is resolved dynamically
    /// inside initializeLibrary(), which this fixture never calls. Marshal.Prelink/PrelinkAll resolve
    /// entry points and build marshalling stubs; they do not invoke anything.
    ///
    /// Critically, this does NOT call CommonUtils.LoadUnmanagedLibraryFromResource - that writes
    /// %APPDATA%\vibranceGUI\x86\vibranceDLL.dll or \x64\vibranceDLL.dll (see Program.cs's NVIDIA
    /// startup branch for why the file name itself has to stay "vibranceDLL.dll" in both
    /// architecture-specific directories), which a real vibranceGUI instance already running on
    /// this machine has open, so File.WriteAllBytes against it throws a sharing-violation
    /// IOException that has nothing to do with whether this binding layer is correct. A normal
    /// --selftest-nvapi run cannot actually hit that collision - the single-instance mutex
    /// (Program.cs:78, bail at :92) is checked before any --selftest-* flag dispatches (the first
    /// at :120), so a second launch bails out long before reaching this fixture. What DOES reach it
    /// while a real instance is running is the headless reflection harness (see the docs guide's
    /// §3.7): it calls NvidiaInteropFixture.Run() directly, bypassing Main() - and therefore the
    /// mutex - entirely, so the live instance's file lock on its own architecture-specific
    /// %APPDATA%\vibranceGUI\x86\vibranceDLL.dll or \x64\vibranceDLL.dll is reachable that way even
    /// though a normal second launch never gets there. Instead this
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
        // Which of the two embedded builds (see native/vibranceDLL/README.md) this process should
        // use - computed the same way Program.cs's real NVIDIA startup branch does, by calling the
        // same method, not a hand-copied mirror of it. CheckResourceNameMatchesProcessBitness below
        // is the check that this selection itself is correct.
        private static readonly string EmbeddedDllFileName = Program.ResolveNvidiaAdapterResourceName();
        private static readonly string EmbeddedDllResourceName = "vibrance.GUI.NVIDIA." + EmbeddedDllFileName;

        // The 12 mangled __thiscall export names NvidiaDynamicVibranceProxy.cs bound before
        // work/native-dll-from-source moved it to the undecorated vibrance_* names (see
        // docs/CODEBASE_GUIDE.md §7.3). Hardcoded rather than read live off the proxy - the proxy no
        // longer references them at all - specifically so N17 below proves the *native* side still
        // carries them (i.e. vibrance.h/.cpp were genuinely left untouched), not merely that nobody
        // deleted the new wrapper layer.
        //
        // __thiscall's own mangling is pointer-size-dependent - "QAE" (32-bit) vs "QEAA" (64-bit),
        // and every pointer/reference argument gains an extra "E" (e.g. "PAH" -> "PEAH") - so x86
        // and x64 builds of the identical C++ source produce two different, equally valid name
        // sets. Both were read directly off this build's own rebuilt DLL (see native/vibranceDLL's
        // README.md) with the PE export-table parser used to verify Task 2 of the x64 port, not
        // hand-derived from the mangling rules, so a transcription mistake here would show up as a
        // [FAIL] rather than silently passing against itself.
        // Re-pinned for the handle-width fix (native/vibranceDLL/README.md item 8): an NvAPI handle
        // that used to mangle as "int" now mangles as "void *" ("H" -> "PAX"/"PEAX"), which changed
        // several of these names from what a pre-width-fix rebuild produced. Still read directly off
        // this build's own rebuilt DLL, not hand-derived from the mangling rules.
        private static readonly string[] OriginalMangledExportNamesX86 =
        {
            "?initializeLibrary@vibrance@vibranceDLL@@QAE_NXZ",
            "?unloadLibrary@vibrance@vibranceDLL@@QAE_NXZ",
            "?getActiveOutputs@vibrance@vibranceDLL@@QAEHQAPAH0@Z",
            "?enumeratePhsyicalGPUs@vibrance@vibranceDLL@@QAEXQAPAH@Z",
            "?getGpuName@vibrance@vibranceDLL@@QAE_NQAPAHPAD@Z",
            "?getDVCInfo@vibrance@vibranceDLL@@QAE_NPAUNV_DISPLAY_DVC_INFO@12@PAX@Z",
            "?enumerateNvidiaDisplayHandle@vibrance@vibranceDLL@@QAEPAXH@Z",
            "?setDVCLevel@vibrance@vibranceDLL@@QAE_NPAXH@Z",
            "?isWindowActive@vibrance@vibranceDLL@@QAE_NPAPAUHWND__@@@Z",
            "?equalsDVCLevel@vibrance@vibranceDLL@@QAE_NPAXH@Z",
            "?getGpuSystemType@vibrance@vibranceDLL@@QAEHPAH@Z",
            "?getAssociatedNvidiaDisplayHandle@vibrance@vibranceDLL@@QAEPAXPBDH@Z",
        };

        private static readonly string[] OriginalMangledExportNamesX64 =
        {
            "?initializeLibrary@vibrance@vibranceDLL@@QEAA_NXZ",
            "?unloadLibrary@vibrance@vibranceDLL@@QEAA_NXZ",
            "?getActiveOutputs@vibrance@vibranceDLL@@QEAAHQEAPEAH0@Z",
            "?enumeratePhsyicalGPUs@vibrance@vibranceDLL@@QEAAXQEAPEAH@Z",
            "?getGpuName@vibrance@vibranceDLL@@QEAA_NQEAPEAHPEAD@Z",
            "?getDVCInfo@vibrance@vibranceDLL@@QEAA_NPEAUNV_DISPLAY_DVC_INFO@12@PEAX@Z",
            "?enumerateNvidiaDisplayHandle@vibrance@vibranceDLL@@QEAAPEAXH@Z",
            "?setDVCLevel@vibrance@vibranceDLL@@QEAA_NPEAXH@Z",
            "?isWindowActive@vibrance@vibranceDLL@@QEAA_NPEAPEAUHWND__@@@Z",
            "?equalsDVCLevel@vibrance@vibranceDLL@@QEAA_NPEAXH@Z",
            "?getGpuSystemType@vibrance@vibranceDLL@@QEAAHPEAH@Z",
            "?getAssociatedNvidiaDisplayHandle@vibrance@vibranceDLL@@QEAAPEAXPEBDH@Z",
        };

        private static readonly string[] OriginalMangledExportNames =
            IntPtr.Size == 4 ? OriginalMangledExportNamesX86 : OriginalMangledExportNamesX64;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI NVIDIA interop self test");
            checklist.Lines.Add(string.Empty);

            CheckResourceNameMatchesProcessBitness(checklist);

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
            CheckDllImportTypesMatchExpectedSignatures(checklist);
            CheckAbiEchoHandleRoundTrips(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // N0 - Slice 2: proves the *selection* itself picks the right resource for this process's
        // bitness, not merely that whichever resource happens to end up embedded loads correctly.
        // Calls Program.ResolveNvidiaAdapterResourceName() directly - it is internal, not private,
        // so (unlike CliOptionsFixture's reflection-based call into buildFormTitleText) no
        // reflection is needed here - the same reasoning as MatchingFixture calling
        // ApplicationSettingMatcher directly: a hand-copied mirror of the bitness check here could
        // drift from what Program.cs actually runs and this fixture would never notice.
        private static void CheckResourceNameMatchesProcessBitness(Checklist checklist)
        {
            checklist.Lines.Add("N0: the embedded resource picked matches this process's bitness:");
            string expected = Environment.Is64BitProcess ? "vibranceDLL64.dll" : "vibranceDLL.dll";
            string actual = Program.ResolveNvidiaAdapterResourceName();
            checklist.Check(actual == expected,
                string.Format("Program.ResolveNvidiaAdapterResourceName()=\"{0}\" expected=\"{1}\" (Is64BitProcess={2})",
                    actual, expected, Environment.Is64BitProcess));
        }

        // N1.
        private static byte[] CheckResourceExists(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N1: embedded " + EmbeddedDllFileName + " resource exists and is non-empty:");
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

        // N20 - Slice 2 (handle-width fix, native/vibranceDLL/README.md item 8): Marshal.Prelink
        // (N4-N15) cannot catch a wrong-but-marshalable parameter width on its own - it JITs the
        // marshalling stub without calling it, and an undecorated __cdecl export carries no type
        // information to check a C# declaration against, so a handle parameter left "int" instead
        // of widened to "IntPtr" would resolve and prelink cleanly and every check above would stay
        // green. This reflects over the same [DllImport]-bound methods N4-N15 already found and
        // asserts each one's parameter and return types are exactly what they should be post-fix -
        // the table this mirrors is the one in docs/CODEBASE_GUIDE.md's §7.3.
        private static void CheckDllImportTypesMatchExpectedSignatures(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N20: every bound method's parameter and return types match the expected (post-width-fix) signature:");

            Dictionary<string, ExpectedSignature> expected = BuildExpectedSignatures();
            foreach (MethodInfo method in GetBoundMethods().OrderBy(m => m.Name))
            {
                ExpectedSignature signature;
                if (!expected.TryGetValue(method.Name, out signature))
                {
                    checklist.Check(false, method.Name + ": no expected signature recorded for this method - update this check");
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                bool returnTypeMatches = method.ReturnType == signature.ReturnType;
                bool parameterCountMatches = parameters.Length == signature.ParameterTypes.Length;
                bool parameterTypesMatch = parameterCountMatches;
                if (parameterCountMatches)
                {
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (parameters[i].ParameterType != signature.ParameterTypes[i])
                        {
                            parameterTypesMatch = false;
                        }
                    }
                }

                checklist.Check(returnTypeMatches && parameterTypesMatch,
                    string.Format("{0}: return={1} params=({2}), expected return={3} params=({4})",
                        method.Name,
                        method.ReturnType.Name,
                        string.Join(", ", parameters.Select(p => p.ParameterType.Name).ToArray()),
                        signature.ReturnType.Name,
                        string.Join(", ", signature.ParameterTypes.Select(t => t.Name).ToArray())));
            }
        }

        // One (returnType, parameterTypes) pair per bound method name, keyed by the C# method name
        // (not the native EntryPoint) since that is what GetBoundMethods()/MethodInfo.Name expose.
        // IntPtr appears everywhere a handle crosses the boundary; a plain "int" is only ever
        // correct for a genuinely 32-bit value (an index, a level, or getActiveOutputs'/
        // getDVCInfo's own output-mask-shaped values, none of which are handles).
        private static Dictionary<string, ExpectedSignature> BuildExpectedSignatures()
        {
            Dictionary<string, ExpectedSignature> expected = new Dictionary<string, ExpectedSignature>();
            expected["initializeLibrary"] = new ExpectedSignature(typeof(bool));
            expected["unloadLibrary"] = new ExpectedSignature(typeof(bool));
            expected["getActiveOutputs"] = new ExpectedSignature(typeof(int), typeof(IntPtr[]), typeof(IntPtr[]));
            expected["enumeratePhsyicalGPUs"] = new ExpectedSignature(typeof(void), typeof(IntPtr[]));
            expected["getGpuName"] = new ExpectedSignature(typeof(bool), typeof(IntPtr[]), typeof(StringBuilder));
            expected["getDVCInfo"] = new ExpectedSignature(typeof(bool), typeof(NvDisplayDvcInfo).MakeByRefType(), typeof(IntPtr));
            expected["enumerateNvidiaDisplayHandle"] = new ExpectedSignature(typeof(IntPtr), typeof(int));
            expected["setDVCLevel"] = new ExpectedSignature(typeof(bool), typeof(IntPtr), typeof(int));
            expected["isWindowActive"] = new ExpectedSignature(typeof(bool), typeof(IntPtr).MakeByRefType());
            expected["equalsDVCLevel"] = new ExpectedSignature(typeof(bool), typeof(IntPtr), typeof(int));
            expected["getGpuSystemType"] = new ExpectedSignature(typeof(NvSystemType), typeof(IntPtr));
            expected["getAssociatedNvidiaDisplayHandle"] = new ExpectedSignature(typeof(IntPtr), typeof(string), typeof(int));
            return expected;
        }

        private class ExpectedSignature
        {
            public ExpectedSignature(Type returnType, params Type[] parameterTypes)
            {
                ReturnType = returnType;
                ParameterTypes = parameterTypes;
            }

            public Type ReturnType { get; private set; }
            public Type[] ParameterTypes { get; private set; }
        }

        // Test-only P/Invoke into vibrance_abi_echoHandle (see that export's own comment in
        // vibrance_c.h) - deliberately declared here, not on NvidiaDynamicVibranceProxy, so it is
        // never counted among N19's twelve production bindings.
        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "vibrance_abi_echoHandle",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr vibrance_abi_echoHandle(IntPtr handle);

        // N23 - the one check in this fixture that can catch a genuine handle-width regression with
        // no GPU attached. N20 only pins the *declared* C# types; Marshal.Prelink itself proves
        // those resolve and marshal without throwing even when the declared width is wrong (see
        // N20's own comment) - it never actually calls anything. This instead calls the trivial
        // native passthrough and checks that the actual bytes crossing the real __cdecl ABI
        // boundary survive intact, using a value with the high 32 bits set on x64 - exactly what a
        // C# "int" parameter/return (instead of IntPtr) would silently truncate.
        private static void CheckAbiEchoHandleRoundTrips(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("N23: vibrance_abi_echoHandle round-trips a handle-shaped value with the high bits set intact:");

            IntPtr testValue = IntPtr.Size == 8
                ? new IntPtr(unchecked((long)0x1122334455667788))
                : new IntPtr(unchecked((int)0x12345678));
            try
            {
                IntPtr echoed = vibrance_abi_echoHandle(testValue);
                checklist.Check(echoed == testValue,
                    string.Format("sent 0x{0:X}, got back 0x{1:X}", testValue.ToInt64(), echoed.ToInt64()));
            }
            catch (Exception ex)
            {
                checklist.Check(false, "vibrance_abi_echoHandle threw " + ex.GetType().Name + ": " + ex.Message);
            }
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
