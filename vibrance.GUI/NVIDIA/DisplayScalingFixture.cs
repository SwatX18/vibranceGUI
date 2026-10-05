using System;
using System.Collections.Generic;

namespace vibrance.GUI.NVIDIA
{
    /// <summary>
    /// Regression coverage for the GPU/display scaling toggle (NvScalingMap and
    /// DisplayScalingController, driven through a fake IDisplayScalingDevice). The NV_SCALING values
    /// used here are the ones in NVIDIA's nvapi.h (1,2,3,5,6,7,8), not remembered ones.
    /// Run by vibrance.GUI.exe --selftest-scaling.
    /// </summary>
    public static class DisplayScalingFixture
    {
        private const string Primary = @"\\.\DISPLAY1";

        // (GPU value, Display value) pairs: Full screen, Aspect, No scaling.
        private static readonly int[][] Pairs = new int[][]
        {
            new int[] { 2, 1 },
            new int[] { 5, 6 },
            new int[] { 3, 7 }
        };

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI display scaling self test");
            checklist.Lines.Add(string.Empty);

            CheckMap(checklist);
            CheckController(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        private static void CheckMap(Checklist c)
        {
            c.Lines.Add("Map: classification and retargeting");
            foreach (int[] pair in Pairs)
            {
                int gpu = pair[0];
                int display = pair[1];
                c.Check(NvScalingMap.Classify(gpu) == ScalingTarget.Gpu, gpu + " classifies as Gpu");
                c.Check(NvScalingMap.Classify(display) == ScalingTarget.Display, display + " classifies as Display");
                c.Check(NvScalingMap.Retarget(gpu, ScalingTarget.Display) == display, gpu + " -> Display is " + display);
                c.Check(NvScalingMap.Retarget(display, ScalingTarget.Gpu) == gpu, display + " -> Gpu is " + gpu);
                c.Check(NvScalingMap.Retarget(gpu, ScalingTarget.Gpu) == gpu, gpu + " -> Gpu stays " + gpu);
                c.Check(NvScalingMap.Retarget(display, ScalingTarget.Display) == display, display + " -> Display stays " + display);
            }

            c.Check(NvScalingMap.Classify(8) == ScalingTarget.Gpu, "8 (integer scaling) classifies as Gpu");
            c.Check(NvScalingMap.Retarget(8, ScalingTarget.Display) == null, "8 (integer scaling) has no Display equivalent");
            c.Check(NvScalingMap.Retarget(8, ScalingTarget.Gpu) == 8, "8 -> Gpu stays 8");

            foreach (int unknown in new int[] { 0, 4, 9, 10, 11, 12, 99, 255, -1 })
            {
                c.Check(NvScalingMap.Classify(unknown) == null, unknown + " classifies as null");
                c.Check(NvScalingMap.Retarget(unknown, ScalingTarget.Gpu) == null, unknown + " -> Gpu is null");
                c.Check(NvScalingMap.Retarget(unknown, ScalingTarget.Display) == null, unknown + " -> Display is null");
            }
        }

        private static void CheckController(Checklist c)
        {
            c.Lines.Add(string.Empty);
            c.Lines.Add("Controller: state machine");

            // Refresh reads and classifies.
            FakeDevice device = new FakeDevice(2);
            bool applied = false;
            DisplayScalingController ctl = Make(device, () => applied);
            c.Check(ctl.State == ScalingControlState.Unavailable && ctl.Current == null, "starts Unavailable with no Current");
            int events = 0;
            ctl.StateChanged += delegate { events++; };
            c.Check(ctl.Refresh(), "Refresh on a GPU-scaled display returns true");
            c.Check(ctl.State == ScalingControlState.Ready && ctl.Current == ScalingTarget.Gpu, "Ready, Current=Gpu");
            c.Check(ctl.LastRawValue == 2, "LastRawValue is the raw driver value 2");
            c.Check(events > 0, "StateChanged raised by the refresh");
            c.Check(device.LastReadName == Primary, "read targeted the primary display's name");

            int eventsBefore = events;
            ctl.Refresh();
            c.Check(events == eventsBefore + 2 && ctl.State == ScalingControlState.Ready && ctl.Current == ScalingTarget.Gpu,
                "a re-read raises exactly the Reading and Ready transitions and ends where it began");

            device = new FakeDevice(1);
            ctl = Make(device, () => false);
            ctl.Refresh();
            c.Check(ctl.Current == ScalingTarget.Display, "raw 1 reads as Current=Display");

            // Failed read.
            device = new FakeDevice(2);
            device.ReadOk = false;
            ctl = Make(device, () => false);
            c.Check(!ctl.Refresh(), "failed read: Refresh returns false");
            c.Check(ctl.State == ScalingControlState.Unavailable && ctl.Current == null, "failed read: Unavailable");
            c.Check(ctl.LastRawValue == null, "failed read: no raw value");
            c.Check(!ctl.Request(ScalingTarget.Display) && device.Writes.Count == 0, "Unavailable: Request refused, no write");

            // Unclassifiable values.
            foreach (int raw in new int[] { 0, 99 })
            {
                device = new FakeDevice(raw);
                ctl = Make(device, () => false);
                c.Check(!ctl.Refresh() && ctl.State == ScalingControlState.Unavailable, "raw " + raw + ": Unavailable");
                c.Check(ctl.LastRawValue == raw, "raw " + raw + ": LastRawValue kept for the tooltip");
            }

            // Device throws.
            device = new FakeDevice(2);
            device.ThrowOnRead = true;
            ctl = Make(device, () => false);
            bool threw = false;
            try { ctl.Refresh(); } catch (Exception) { threw = true; }
            c.Check(!threw && ctl.State == ScalingControlState.Unavailable, "a throwing read leaves it Unavailable without throwing");

            // Missing device name.
            device = new FakeDevice(2);
            ctl = new DisplayScalingController(device, () => null, () => false);
            c.Check(!ctl.Refresh() && ctl.State == ScalingControlState.Unavailable && device.Reads == 0, "null primary device name: Unavailable, no driver read");

            // Accepted request: exactly one write, mode kept, state re-read.
            foreach (int[] pair in Pairs)
            {
                device = new FakeDevice(pair[0]);
                ctl = Make(device, () => false);
                ctl.Refresh();
                bool ok = ctl.Request(ScalingTarget.Display);
                c.Check(ok, pair[0] + " -> Display accepted");
                c.Check(device.Writes.Count == 1 && device.Writes[0] == pair[1] && device.LastWriteName == Primary,
                    "exactly one write of " + pair[1] + " to the primary display");
                c.Check(ctl.State == ScalingControlState.Ready && ctl.Current == ScalingTarget.Display, "Ready with Current=Display after the write");
                c.Check(ctl.Request(ScalingTarget.Gpu) && device.Writes.Count == 2 && device.Writes[1] == pair[0], "and back to Gpu writes " + pair[0]);
            }

            // Already current.
            device = new FakeDevice(2);
            ctl = Make(device, () => false);
            ctl.Refresh();
            c.Check(!ctl.Request(ScalingTarget.Gpu) && device.Writes.Count == 0, "request for the current side: refused, no write");

            // No display equivalent (integer scaling).
            device = new FakeDevice(8);
            ctl = Make(device, () => false);
            ctl.Refresh();
            c.Check(ctl.Current == ScalingTarget.Gpu, "integer scaling reads as Gpu");
            c.Check(!ctl.Request(ScalingTarget.Display) && device.Writes.Count == 0, "integer scaling -> Display: refused, no write");

            // Game resolution applied.
            device = new FakeDevice(2);
            applied = true;
            ctl = Make(device, () => applied);
            ctl.Refresh();
            c.Check(!ctl.Request(ScalingTarget.Display) && device.Writes.Count == 0, "game resolution applied: refused, no write");
            applied = false;
            c.Check(ctl.Request(ScalingTarget.Display) && device.Writes.Count == 1, "once the game resolution is gone the same request is accepted");

            // Re-entrancy: the device calls Request again while the write is in flight.
            device = new FakeDevice(2);
            ctl = Make(device, () => false);
            ctl.Refresh();
            bool reentrantResult = true;
            ScalingControlState stateDuringWrite = ScalingControlState.Ready;
            DisplayScalingController captured = ctl;
            device.OnWrite = delegate
            {
                stateDuringWrite = captured.State;
                reentrantResult = captured.Request(ScalingTarget.Gpu);
                captured.Refresh();
            };
            c.Check(ctl.Request(ScalingTarget.Display), "outer request accepted");
            c.Check(stateDuringWrite == ScalingControlState.Writing, "State is Writing while the device write is in flight");
            c.Check(!reentrantResult, "a request while Writing is refused");
            c.Check(device.Writes.Count == 1, "re-entrant request and Refresh caused no extra write");
            c.Check(device.Reads == 2, "the in-flight Refresh did not read (only the initial and the post-write reads happened)");
            c.Check(ctl.State == ScalingControlState.Ready && ctl.Current == ScalingTarget.Display, "settles Ready with Current=Display");

            // Failed write shows what the driver really has.
            device = new FakeDevice(2);
            device.WriteOk = false;
            ctl = Make(device, () => false);
            ctl.Refresh();
            c.Check(!ctl.Request(ScalingTarget.Display), "failed write: Request returns false");
            c.Check(device.Writes.Count == 1, "failed write was attempted once");
            c.Check(ctl.State == ScalingControlState.Ready && ctl.Current == ScalingTarget.Gpu, "failed write: re-read shows the driver's real value (Gpu)");

            // Write "succeeds" but the driver ends elsewhere; the control follows the driver.
            device = new FakeDevice(2);
            device.WriteStores = false;
            ctl = Make(device, () => false);
            ctl.Refresh();
            ctl.Request(ScalingTarget.Display);
            c.Check(ctl.Current == ScalingTarget.Gpu, "driver ignored the write: Current still reflects the driver (Gpu)");

            // Throwing write.
            device = new FakeDevice(2);
            device.ThrowOnWrite = true;
            ctl = Make(device, () => false);
            ctl.Refresh();
            threw = false;
            bool result = true;
            try { result = ctl.Request(ScalingTarget.Display); } catch (Exception) { threw = true; }
            c.Check(!threw && !result, "a throwing write neither throws nor reports success");
            c.Check(ctl.State == ScalingControlState.Ready && ctl.Current == ScalingTarget.Gpu, "a throwing write re-reads and stays Ready");

            // Read fails after the write: Unavailable rather than a stale Current.
            device = new FakeDevice(2);
            ctl = Make(device, () => false);
            ctl.Refresh();
            device.OnWrite = delegate { device.ReadOk = false; };
            ctl.Request(ScalingTarget.Display);
            c.Check(ctl.State == ScalingControlState.Unavailable && ctl.Current == null, "read failing after a write ends Unavailable, not stale");
            c.Check(ctl.Refresh() == false, "and Refresh stays false while reads keep failing");
            device.ReadOk = true;
            c.Check(ctl.Refresh() && ctl.State == ScalingControlState.Ready, "and recovers once reads succeed again");
        }

        private static DisplayScalingController Make(FakeDevice device, Func<bool> applied)
        {
            return new DisplayScalingController(device, () => Primary, applied);
        }

        private sealed class FakeDevice : IDisplayScalingDevice
        {
            public int Value;
            public bool ReadOk = true;
            public bool WriteOk = true;
            public bool WriteStores = true;
            public bool ThrowOnRead;
            public bool ThrowOnWrite;
            public int Reads;
            public string LastReadName;
            public string LastWriteName;
            public readonly List<int> Writes = new List<int>();
            public Action OnWrite;

            public FakeDevice(int value)
            {
                Value = value;
            }

            public bool TryGetScaling(string gdiDeviceName, out int nvScaling)
            {
                Reads++;
                LastReadName = gdiDeviceName;
                if (ThrowOnRead)
                {
                    throw new InvalidOperationException("fake read failure");
                }
                nvScaling = ReadOk ? Value : 0;
                return ReadOk;
            }

            public bool TrySetScaling(string gdiDeviceName, int nvScaling)
            {
                LastWriteName = gdiDeviceName;
                Writes.Add(nvScaling);
                if (OnWrite != null)
                {
                    OnWrite();
                }
                if (ThrowOnWrite)
                {
                    throw new InvalidOperationException("fake write failure");
                }
                if (WriteOk && WriteStores)
                {
                    Value = nvScaling;
                }
                return WriteOk;
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

            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
