using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using PhotoFocus;

static class PhotoTests
{
    static int passed; static List<object> measurements = new List<object>();
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    static Photo Wrap(GrayPlane p) { return new Photo { Gray = p, FileName = "test-" + Guid.NewGuid().ToString("N") + ".png" }; }
    static GrayPlane Scene(int w, int h)
    {
        float[] p = new float[w * h]; Random random = new Random(72);
        // Non-periodic scene: a purely periodic target would make registration ambiguous.
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double v = 70 + 15 * Math.Sin(x / 37.0) + 12 * Math.Cos(y / 29.0);
            if (x > w * .14 && x < w * .46 && y > h * .18 && y < h * .75) v += ((x / 5) % 2 == 0 ? 130 : 10);
            if (x > w * .6 && x < w * .84 && y > h * .28 && y < h * .64) v += ((y / 8) % 2 == 0 ? 110 : 20);
            if ((x - .73 * w) * (x - .73 * w) + (y - .8 * h) * (y - .8 * h) < h * h * .007) v = 225;
            if (x > .2 * w + .3 * y && x < .2 * w + .3 * y + 3) v = 220;
            p[y * w + x] = (float)Math.Max(5, Math.Min(245, v + random.NextDouble() * .4));
        }
        return new GrayPlane(w, h, p);
    }
    static GrayPlane Change(GrayPlane p, double gain, double offset, double noise, int dx, int dy)
    {
        Random random = new Random(91); float[] values = new float[p.Pixels.Length];
        for (int y = 0; y < p.Height; y++) for (int x = 0; x < p.Width; x++)
        {
            int sx = x - dx, sy = y - dy; double v = sx >= 0 && sy >= 0 && sx < p.Width && sy < p.Height ? p.Pixels[sy * p.Width + sx] : 65;
            values[y * p.Width + x] = (float)Math.Max(0, Math.Min(255, v * gain + offset + noise * (random.NextDouble() + random.NextDouble() + random.NextDouble() + random.NextDouble() - 2) * Math.Sqrt(3)));
        }
        return new GrayPlane(p.Width, p.Height, values);
    }
    static GrayPlane Warp(GrayPlane p, double degrees, double scale, double dx, double dy)
    {
        float[] values = new float[p.Pixels.Length]; double c = Math.Cos(degrees * Math.PI / 180), s = Math.Sin(degrees * Math.PI / 180), cx = (p.Width - 1) * .5, cy = (p.Height - 1) * .5;
        for (int y = 0; y < p.Height; y++) for (int x = 0; x < p.Width; x++)
        {
            double xx = (x - cx - dx) / scale, yy = (y - cy - dy) / scale, sx = c * xx + s * yy + cx, sy = -s * xx + c * yy + cy;
            int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy); double fx = sx - ix, fy = sy - iy;
            values[y * p.Width + x] = ix >= 0 && iy >= 0 && ix + 1 < p.Width && iy + 1 < p.Height ? (float)((1 - fy) * ((1 - fx) * p.Pixels[iy * p.Width + ix] + fx * p.Pixels[iy * p.Width + ix + 1]) + fy * ((1 - fx) * p.Pixels[(iy + 1) * p.Width + ix] + fx * p.Pixels[(iy + 1) * p.Width + ix + 1])) : 65;
        }
        return new GrayPlane(p.Width, p.Height, values);
    }
    static void Save(GrayPlane p, string file)
    {
        using (Bitmap bmp = new Bitmap(p.Width, p.Height, PixelFormat.Format24bppRgb))
        {
            for (int y = 0; y < p.Height; y++) for (int x = 0; x < p.Width; x++) { int v = Math.Max(0, Math.Min(255, (int)Math.Round(p.Pixels[y * p.Width + x]))); bmp.SetPixel(x, y, Color.FromArgb(v, v, v)); }
            bmp.Save(file, ImageFormat.Png);
        }
    }
    static int Main(string[] args)
    {
        try
        {
            string output = args[0]; Directory.CreateDirectory(output);
            GrayPlane p = Scene(320, 240); Rectangle roi = new Rectangle(30, 22, 260, 194);
            Photo a = Wrap(p);
            AnalysisReport identity = FocusMath.Compare(a, a, roi, true, 32, true, 5);
            Check(identity.ReliableComparison && Math.Abs(identity.RelativeDetailPercent.Value - 100) < 1e-9, "identical images give 100 percent");
            Check(identity.Alignment.Dx == 0 && identity.Alignment.Dy == 0, "identity registration has zero shift");
            foreach (MetricComparison m in identity.Metrics) Check(Math.Abs(m.RawAmplitudePercent.Value - 100) < 1e-9, "identity " + m.Metric);
            double previous = 101;
            foreach (double sigma in new[] { .5, 1.0, 1.8, 3.0 })
            {
                AnalysisReport r = FocusMath.Compare(a, Wrap(p.Blur(sigma)), roi, true, 32, true, 5);
                Check(r.ReliableComparison && r.RelativeDetailPercent < previous, "progressive Gaussian blur decreases detail: " + sigma);
                measurements.Add(new { sigma, score = r.RelativeDetailPercent, ncc = r.Alignment.Correlation }); previous = r.RelativeDetailPercent.Value;
            }
            foreach (Point shift in new[] { new Point(11, -7), new Point(-17, 12), new Point(0, 19) })
            {
                AnalysisReport shifted = FocusMath.Compare(a, Wrap(Change(p, 1, 0, 0, shift.X, shift.Y)), roi, true, 32, true, 5);
                Check(shifted.ReliableComparison && shifted.Alignment.Dx == shift.X && shifted.Alignment.Dy == shift.Y && Math.Abs(shifted.RelativeDetailPercent.Value - 100) < .01, "exact shift compensation " + shift);
            }
            AnalysisReport gain = FocusMath.Compare(a, Wrap(Change(p, .65, 25, 0, 0, 0)), roi, false, 32, true, 5);
            Check(gain.ReliableComparison && Math.Abs(gain.RelativeDetailPercent.Value - 100) < 1.5, "global contrast correction preserves detail");
            AnalysisReport rawGain = FocusMath.Compare(a, Wrap(Change(p, .65, 25, 0, 0, 0)), roi, false, 32, false, 5);
            Check(rawGain.RelativeDetailPercent > 64 && rawGain.RelativeDetailPercent < 66, "uncorrected amplitude reflects gain");
            AnalysisReport sharper = FocusMath.Compare(Wrap(p.Blur(1.8)), a, roi, true, 32, true, 5);
            Check(sharper.ReliableComparison && sharper.RelativeDetailPercent > 110, "higher detail is not clipped to 100 percent");
            Photo flat = Wrap(new GrayPlane(320, 240, Enumerable.Repeat(90f, 320 * 240).ToArray()));
            AnalysisReport empty = FocusMath.Single(flat, roi);
            Check(!empty.Working.HasStructure && empty.Working.Raw.Array().All(v => v == 0), "uniform image is insufficient data, not focused");
            Check(!FocusMath.Compare(flat, flat, roi, true, 32, true, 5).RelativeDetailPercent.HasValue, "uniform reference cannot define a comparison");
            GrayPlane other = Change(p, 1, 0, 0, 0, 0); Array.Reverse(other.Pixels);
            Check(!FocusMath.Compare(a, Wrap(other), roi, true, 32, true, 5).ReliableComparison, "different structure is rejected");
            AnalysisReport limit = FocusMath.Compare(a, Wrap(Change(p, 1, 0, 0, 32, 0)), roi, true, 32, true, 5);
            Check(limit.Alignment.AtSearchLimit && !limit.ReliableComparison, "search boundary is provisional");
            Photo noisy = Wrap(Change(new GrayPlane(320, 240, Enumerable.Repeat(110f, 320 * 240).ToArray()), 1, 0, 12, 0, 0));
            AnalysisReport noise = FocusMath.Single(noisy, roi);
            Check(noise.Warnings.Any(s => s.Contains("noise")), "noise-only image is flagged");
            Check(!FocusMath.Compare(a, noisy, roi, true, 32, true, 5).ReliableComparison, "uncorrelated noise does not pass reference comparison");
            bool invalid = false; try { FocusMath.Single(a, new Rectangle(-1, 0, 100, 100)); } catch (ArgumentException) { invalid = true; }
            Check(invalid, "out-of-bounds ROI is rejected");
            invalid = false; try { FocusMath.Single(a, new Rectangle(0, 0, 8, 8)); } catch (ArgumentException) { invalid = true; }
            Check(invalid, "tiny ROI is rejected");
            // Linear ramp with dx 2 and dy 3: EOG and Sobel energy is 13, second derivatives are 0.
            float[] ramp = new float[20 * 18]; for (int y = 0; y < 18; y++) for (int x = 0; x < 20; x++) ramp[y * 20 + x] = 2 * x + 3 * y;
            OperatorValues vRamp = FocusMath.Operators(new GrayPlane(20, 18, ramp));
            Check(Math.Abs(vRamp.EOG - 13) < 1e-9 && Math.Abs(vRamp.Tenengrad - 13) < 1e-9, "gradient operators match analytic linear ramp");
            Check(vRamp.SML == 0 && vRamp.EOL == 0 && vRamp.LaplacianVariance == 0, "second differences vanish on analytic linear ramp");
            Check(Math.Abs(vRamp.Brenner - 26) < 1e-9, "Brenner 2D matches analytic linear ramp");
            Save(p, Path.Combine(output, "reference.png")); Save(p.Blur(1.4), Path.Combine(output, "working.png"));
            Save(Change(p.Blur(1.2), .8, 15, 1, 11, -7), Path.Combine(output, "shifted.png"));
            using (Photo loaded = Photo.Open(Path.Combine(output, "reference.png")))
            {
                Check(loaded.Gray.Width == 320 && Math.Abs(loaded.Gray.Pixels[1234] - Math.Round(p.Pixels[1234])) < .001, "PNG loader preserves native luminance and dimensions");
                string moved = Path.Combine(output, "rename-check.png"); File.Move(loaded.FileName, moved); File.Move(moved, loaded.FileName);
                Check(File.Exists(loaded.FileName), "loaded image does not keep source file locked");
                AnalysisReport lr = FocusMath.Single(loaded, loaded.Gray.Bounds);
                invalid = false; try { ReportFiles.Save(loaded.FileName, lr); } catch (ArgumentException) { invalid = true; }
                Check(invalid, "export cannot overwrite source image");
            }
            ReportFiles.Save(Path.Combine(output, "identity.json"), identity); ReportFiles.Save(Path.Combine(output, "identity.csv"), identity); ReportFiles.Save(Path.Combine(output, "identity.txt"), identity);
            Check(File.ReadAllText(Path.Combine(output, "identity.csv")).Split('\n').Length >= 7, "CSV contains every metric");
            object decoded = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(output, "identity.json")));
            Check(decoded != null, "JSON report serializes without non-finite values or cycles");
            GrayPlane changedOutside = Change(p, 1, 0, 0, 0, 0);
            for (int y = 0; y < p.Height; y++) for (int x = 0; x < p.Width; x++) if (!roi.Contains(x, y)) changedOutside.Pixels[y * p.Width + x] = 255;
            ImageMeasures originalRoi = FocusMath.Single(a, roi).Working, changedRoi = FocusMath.Single(Wrap(changedOutside), roi).Working;
            Check(originalRoi.Raw.Array().SequenceEqual(changedRoi.Raw.Array()) && originalRoi.Filtered.Array().SequenceEqual(changedRoi.Filtered.Array()), "measurement excludes background outside selected ROI");
            float[] bars = new float[320 * 240];
            for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) bars[y * 320 + x] = (x / 4) % 2 == 0 ? 60 : 190;
            AnalysisReport ambiguous = FocusMath.Compare(Wrap(new GrayPlane(320, 240, bars)), Wrap(new GrayPlane(320, 240, (float[])bars.Clone())), roi, true, 32, false, 5);
            Check(ambiguous.Alignment.Ambiguous && !ambiguous.ReliableComparison, "repeated pattern reports ambiguous registration");
            float[] sine = new float[320 * 240];
            for (int y = 0; y < 240; y++) for (int x = 0; x < 320; x++) sine[y * 320 + x] = (float)(127 + 70 * Math.Sin(x * 2 * Math.PI / 10));
            Photo sineRef = Wrap(new GrayPlane(320, 240, sine)); Photo sineBlur = Wrap(sineRef.Gray.Blur(1.4));
            AnalysisReport sineRaw = FocusMath.Compare(sineRef, sineBlur, roi, false, 32, false, 5);
            AnalysisReport sineNorm = FocusMath.Compare(sineRef, sineBlur, roi, false, 32, true, 5);
            Check(sineRaw.RelativeDetailPercent < 80 && sineNorm.RelativeDetailPercent > 95, "normalization can hide sinusoidal blur, so it must remain optional");
            Check(sineNorm.Warnings.Any(s => s.Contains("mask")), "contrast normalization explicitly warns about hidden defocus contrast");
            float[] padded = Enumerable.Repeat(65f, 350 * 260).ToArray();
            for (int y = 0; y < p.Height; y++) Array.Copy(p.Pixels, y * p.Width, padded, (y + 5) * 350 + 9, p.Width);
            AnalysisReport paddedResult = FocusMath.Compare(a, Wrap(new GrayPlane(350, 260, padded)), roi, true, 32, false, 5);
            Check(paddedResult.ReliableComparison && paddedResult.Alignment.Dx == 9 && paddedResult.Alignment.Dy == 5 && Math.Abs(paddedResult.RelativeDetailPercent.Value - 100) < .01, "different canvas dimensions preserve original pixel scale");
            string colorFile = Path.Combine(output, "color-test.png");
            using (Bitmap color = new Bitmap(40, 40)) { using (Graphics g = Graphics.FromImage(color)) g.Clear(Color.FromArgb(200, 100, 50)); color.Save(colorFile, ImageFormat.Png); }
            using (Photo color = Photo.Open(colorFile)) Check(Math.Abs(color.Gray.Pixels[300] - (200 * .299 + 100 * .587 + 50 * .114)) < .001, "ordinary color photos use defined luminance conversion");
            GrayPlane blurredCheck = p.Blur(.7); double filterError = 0; int radius = 3; double norm = 0;
            for (int ky = -radius; ky <= radius; ky++) for (int kx = -radius; kx <= radius; kx++) norm += Math.Exp(-(kx * kx + ky * ky) / (2 * .7 * .7));
            foreach (Point q in new[] { new Point(0, 0), new Point(1, 230), new Point(318, 239), new Point(130, 86) })
            {
                double sum = 0;
                for (int ky = -radius; ky <= radius; ky++) for (int kx = -radius; kx <= radius; kx++)
                {
                    int x = q.X + kx, y = q.Y + ky; if (x < 0) x = -x; if (y < 0) y = -y; if (x >= p.Width) x = 2 * p.Width - 2 - x; if (y >= p.Height) y = 2 * p.Height - 2 - y;
                    sum += p.Pixels[y * p.Width + x] * Math.Exp(-(kx * kx + ky * ky) / (2 * .7 * .7)) / norm;
                }
                filterError = Math.Max(filterError, Math.Abs(sum - blurredCheck.Pixels[q.Y * p.Width + q.X]));
            }
            Check(filterError < .001, "optimized Gaussian matches independent direct 2D convolution");
            Check(noise.Working.NoiseDominated && !noise.Working.HasStructure, "noise-dominated single photo does not receive a focus index");
            var session = new AnalysisSession();
            AnalysisReport firstCached = session.Compare(a, a, roi, true, 32, false, 5);
            AnalysisReport secondCached = session.Compare(a, a, roi, true, 32, true, 10);
            Check(!firstCached.CachedReference && secondCached.CachedReference && secondCached.CachedWorking && secondCached.CachedAlignment && session.MeasureComputations == 2 && session.AlignmentComputations == 1, "changing tolerance or contrast mode reuses bounded image and alignment caches");
            session.Clear();
            Check(!session.Single(a, roi).CachedWorking, "cache clear invalidates previous image measurements");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); bool observed = false;
                try { session.Single(a, roi, canceled.Token); } catch (OperationCanceledException) { observed = true; }
                Check(observed, "cancellation is respected even on a cache hit");
            }
            GrayPlane blurPartial = p.Blur(3); float[] partialValues = (float[])p.Pixels.Clone();
            for (int y = 0; y < p.Height; y++) for (int x = 0; x < p.Width / 2; x++) partialValues[y * p.Width + x] = blurPartial.Pixels[y * p.Width + x];
            AnalysisReport partial = FocusMath.Compare(a, Wrap(new GrayPlane(p.Width, p.Height, partialValues)), p.Bounds, false, 32, false, 5);
            Check(partial.LocalP75Percent - partial.LocalP25Percent > 20 && partial.LowerDetailTiles > 0 && partial.LowerDetailTiles < partial.InformativeTiles, "spatial map reveals local blur hidden by a global average");
            Check(partial.Tiles.Count == 16 && partial.Reference.Tiles.Count == partial.Working.Tiles.Count, "reference and working spatial grids are identical");
            OperatorValues view = FocusMath.Operators(p, roi), cropped = FocusMath.Operators(p.Crop(roi));
            Check(view.Array().Zip(cropped.Array(), (u, v) => Math.Abs(u - v) / Math.Max(1, Math.Abs(v))).Max() < 1e-12, "strided ROI operators equal an independently cropped image");
            AnalysisReport noiseChange = FocusMath.Compare(Wrap(p.Blur(1.4)), Wrap(Change(p.Blur(1.4), 1, 0, 18, 0, 0)), roi, false, 32, false, 5);
            Check(noiseChange.NoiseMismatch && !noiseChange.RelativeDetailPercent.HasValue, "added noise cannot be presented as better focus");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.CancelAfter(10); bool observed = false;
                try { FocusMath.Single(Wrap(Scene(1024, 768)), new Rectangle(0, 0, 1024, 768), canceled.Token); } catch (OperationCanceledException) { observed = true; }
                Check(observed, "large analysis can be canceled during calculation");
            }
            ReportFiles.Save(Path.Combine(output, "comparison.html"), partial);
            string exportedHtml = File.ReadAllText(Path.Combine(output, "comparison.html"));
            Check(exportedHtml.Contains("<svg") && exportedHtml.Contains("Tile 16") && !exportedHtml.Contains("<script") && !exportedHtml.Contains("<img"), "portable HTML contains metrics and all tiles without scripts or source photos");
            string dangerous = partial.WorkingFile; partial.WorkingFile = "photo & name.png"; partial.Warnings.Add("<script>alert(1)</script>");
            string escapedHtml = ReportHtml.Render(partial);
            Check(escapedHtml.Contains("photo &amp; name.png") && !escapedHtml.Contains("<script>alert"), "HTML report escapes user-controlled file names and warning text"); partial.WorkingFile = dangerous; partial.Warnings.RemoveAt(partial.Warnings.Count - 1);
            List<BatchItem> batch = BatchAnalysis.Run(new[] { Path.Combine(output, "reference.png"), Path.Combine(output, "missing.png"), Path.Combine(output, "working.png") }, null, null, false, 32, false, 5, CancellationToken.None, null);
            Check(batch.Count == 3 && batch[0].Report != null && batch[1].Error != null && batch[2].Report != null, "batch continues after a broken or missing image");
            BatchAnalysis.Save(Path.Combine(output, "batch.csv"), batch); BatchAnalysis.Save(Path.Combine(output, "batch.json"), batch);
            Check(File.ReadAllText(Path.Combine(output, "batch.csv")).Contains("missing.png") && new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(output, "batch.json"))) != null, "batch exports retain per-file errors and successful results");
            using (Photo loaded = Photo.Open(Path.Combine(output, "reference.png"))) Check(loaded.Sha256 != null && loaded.Sha256.Length == 64, "source hashes are included for repeatable reports");
            Save(Scene(640, 512), Path.Combine(output, "large-native.png"));
            AnalysisReport rotated = FocusMath.Compare(a, Wrap(Warp(p, 3, 1, 0, 0)), roi, true, 32, false, 5);
            Console.WriteLine("Rotation diagnostics: samples=" + rotated.Alignment.GeometrySamples + " spread=" + rotated.Alignment.ResidualShiftSpread + " ncc=" + rotated.Alignment.Correlation);
            Check(rotated.Alignment.GeometryMismatch && !rotated.RelativeDetailPercent.HasValue, "rotation is detected through inconsistent local shifts");
            AnalysisReport scaled = FocusMath.Compare(a, Wrap(Warp(p, 0, 1.05, 0, 0)), roi, true, 32, false, 5);
            Console.WriteLine("Scale diagnostics: samples=" + scaled.Alignment.GeometrySamples + " spread=" + scaled.Alignment.ResidualShiftSpread + " ncc=" + scaled.Alignment.Correlation);
            Check(scaled.Alignment.GeometryMismatch && !scaled.RelativeDetailPercent.HasValue, "scale mismatch is not interpreted as ordinary defocus");
            AnalysisReport fractional = FocusMath.Compare(a, Wrap(Warp(p, 0, 1, .4, -.3)), roi, true, 32, false, 5);
            Check(Math.Abs(fractional.Alignment.FractionalDx - .4) < .18 && Math.Abs(fractional.Alignment.FractionalDy + .3) < .18, "fractional-shift diagnostic follows the correlation peak without resampling metrics");
            Random localRandom = new Random(103); float[] movingSource = new float[320 * 240];
            for (int by = 0; by < 240; by += 8) for (int bx = 0; bx < 320; bx += 8)
            { float value = localRandom.Next(2) == 0 ? 45 : 210; for (int y = by; y < by + 8; y++) for (int x = bx; x < bx + 8; x++) movingSource[y * 320 + x] = value; }
            Rectangle movingRoi = new Rectangle(120, 88, 64, 64);
            for (int by = movingRoi.Top; by < movingRoi.Bottom; by += 8) for (int bx = movingRoi.Left; bx < movingRoi.Right; bx += 8)
            { float value = localRandom.Next(2) == 0 ? 100 : 156; for (int y = by; y < by + 8; y++) for (int x = bx; x < bx + 8; x++) movingSource[y * 320 + x] = value; }
            float[] movedValues = (float[])movingSource.Clone();
            for (int y = movingRoi.Top; y < movingRoi.Bottom; y++) for (int x = movingRoi.Left; x < movingRoi.Right; x++) movedValues[y * 320 + x] = 128;
            for (int y = movingRoi.Top; y < movingRoi.Bottom; y++) for (int x = movingRoi.Left; x < movingRoi.Right; x++) movedValues[y * 320 + x + 8] = movingSource[y * 320 + x];
            AnalysisReport objectMoved = FocusMath.Compare(Wrap(new GrayPlane(320, 240, movingSource)), Wrap(new GrayPlane(320, 240, movedValues)), movingRoi, true, 16, false, 5);
            Check(objectMoved.Alignment.GeometryMismatch && !objectMoved.RelativeDetailPercent.HasValue, "foreground movement cannot be hidden by a matching static background");
            AnalysisReport flatCompare = FocusMath.Compare(flat, flat, roi, false, 32, false, 5);
            string flatHtml = ReportHtml.Render(flatCompare);
            Check(!flatHtml.Contains("NaN") && !flatHtml.Contains("Infinity"), "undefined flat-image ratios remain missing in HTML instead of invented zeroes");
            float[] backgroundRamp = new float[64 * 64]; for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) backgroundRamp[y * 64 + x] = x + y;
            Check(FocusMath.Measure(new GrayPlane(64, 64, backgroundRamp)).Tiles.All(t => !t.Informative), "smooth background gradients do not vote as informative focus tiles");
            var summary = new { passed, measurements, identity = identity.RelativeDetailPercent, gain = gain.RelativeDetailPercent, sharper = sharper.RelativeDetailPercent, noise = noise.Working.NoiseSigmaEstimate };
            File.WriteAllText(Path.Combine(output, "test-results.json"), new JavaScriptSerializer().Serialize(summary));
            Console.WriteLine("All " + passed + " checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
