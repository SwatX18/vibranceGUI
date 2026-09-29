using System;
using System.Collections.Generic;

namespace vibrance.GUI.NVIDIA
{
    /// <summary>
    /// Regression coverage for the GPU/display scaling toggle (NvScalingMap and
    /// DisplayScalingController, driven through a fake IDisplayScalingDevice). Scaffold only - the
    /// checks land with the implementation. Run by vibrance.GUI.exe --selftest-scaling.
    /// </summary>
    public static class DisplayScalingFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI display scaling self test");
            checklist.Lines.Add(string.Empty);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
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
