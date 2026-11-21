using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using PhotoFocus;

static class PhotoBenchmark
{
    static GrayPlane Scene(int w, int h)
    {
        float[] p = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            p[y * w + x] = (float)(110 + 30 * Math.Sin(x * .11) * Math.Cos(y * .07) + ((x / 17 + y / 23) % 2 == 0 ? 35 : -35));
        return new GrayPlane(w, h, p);
    }
    static int Main(string[] args)
    {
        var rows = new List<object>();
        FocusMath.Single(new Photo { Gray = Scene(48, 48), FileName = "benchmark" }, new Rectangle(0, 0, 48, 48));
        foreach (Size size in new[] { new Size(640, 512), new Size(3840, 2400) })
        {
            Photo reference = new Photo { Gray = Scene(size.Width, size.Height), FileName = "benchmark-reference" };
            Photo working = new Photo { Gray = reference.Gray.Blur(1.0), FileName = "benchmark-working" };
            foreach (string mode in new[] { "single", "compare" })
            {
                List<double> times = new List<double>();
                for (int run = 0; run < 3; run++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    Stopwatch sw = Stopwatch.StartNew();
                    AnalysisReport report = mode == "single" ? FocusMath.Single(working, working.Gray.Bounds) : FocusMath.Compare(reference, working, reference.Gray.Bounds, true, 32, false, 5);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    if (report.Working.Raw.EOG <= 0) throw new Exception("Empty benchmark result.");
                }
                rows.Add(new { width = size.Width, height = size.Height, mode, milliseconds = times, medianMs = times.OrderBy(x => x).ElementAt(1) });
                Console.WriteLine(mode + " " + size + ": " + times.OrderBy(x => x).ElementAt(1).ToString("0.0") + " ms");
            }
        }
#if PHOTO_V2
        Photo cachedReference = new Photo { Gray = Scene(640, 512), FileName = "cache-reference" }, cachedWorking = new Photo { Gray = Scene(640, 512).Blur(1), FileName = "cache-working" };
        var session = new AnalysisSession(); session.Compare(cachedReference, cachedWorking, cachedReference.Gray.Bounds, true, 32, false, 5);
        var warm = new List<double>();
        for (int i = 0; i < 25; i++) { Stopwatch sw = Stopwatch.StartNew(); session.Compare(cachedReference, cachedWorking, cachedReference.Gray.Bounds, true, 32, false, 5 + i % 3); warm.Add(sw.Elapsed.TotalMilliseconds); }
        rows.Add(new { width = 640, height = 512, mode = "compare_cached_settings", milliseconds = warm, medianMs = warm.OrderBy(x => x).ElementAt(12) });
#endif
        File.WriteAllText(args[0], new JavaScriptSerializer().Serialize(new { machine = Environment.MachineName, processors = Environment.ProcessorCount, runtime = Environment.Version.ToString(), measurements = rows }));
        return 0;
    }
}
