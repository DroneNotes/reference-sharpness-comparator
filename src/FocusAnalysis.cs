using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PhotoFocus
{
    public sealed class GrayPlane
    {
        public readonly int Width, Height;
        public readonly float[] Pixels;
        public GrayPlane(int width, int height, float[] pixels)
        {
            if (width < 1 || height < 1 || pixels.Length != checked(width * height)) throw new ArgumentException("The pixel array does not match the image dimensions.");
            Width = width; Height = height; Pixels = pixels;
        }
        public Rectangle Bounds { get { return new Rectangle(0, 0, Width, Height); } }
        public GrayPlane Crop(Rectangle r, CancellationToken token = default(CancellationToken))
        {
            if (!Bounds.Contains(r) || r.Width < 1 || r.Height < 1) throw new ArgumentException("The region extends beyond the image boundaries.");
            float[] p = new float[r.Width * r.Height];
            for (int y = 0; y < r.Height; y++) { if ((y & 31) == 0) token.ThrowIfCancellationRequested(); Array.Copy(Pixels, (r.Y + y) * Width + r.X, p, y * r.Width, r.Width); }
            return new GrayPlane(r.Width, r.Height, p);
        }
        public GrayPlane Blur(double sigma, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            if (sigma <= 0) return this;
            int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
            double[] kernel = new double[2 * radius + 1]; double sum = 0;
            for (int k = -radius; k <= radius; k++) { double v = Math.Exp(-k * k / (2 * sigma * sigma)); kernel[k + radius] = v; sum += v; }
            for (int k = 0; k < kernel.Length; k++) kernel[k] /= sum;
            float[] tmp = new float[Pixels.Length], dst = new float[Pixels.Length];
            Action<int> horizontal = delegate(int y)
            {
                token.ThrowIfCancellationRequested();
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    double value = Pixels[row + x] * kernel[radius];
                    for (int k = 1; k <= radius; k++)
                    {
                        int l = x - k, r = x + k;
                        if (l < 0) l = Reflect(l, Width); if (r >= Width) r = Reflect(r, Width);
                        value += ((double)Pixels[row + l] + Pixels[row + r]) * kernel[k + radius];
                    }
                    tmp[row + x] = (float)value;
                }
            };
            Action<int> vertical = delegate(int y)
            {
                token.ThrowIfCancellationRequested();
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    double value = tmp[row + x] * kernel[radius];
                    for (int k = 1; k <= radius; k++)
                    {
                        int u = y - k, d = y + k;
                        if (u < 0) u = Reflect(u, Height); if (d >= Height) d = Reflect(d, Height);
                        value += ((double)tmp[u * Width + x] + tmp[d * Width + x]) * kernel[k + radius];
                    }
                    dst[row + x] = (float)value;
                }
            };
            if (Pixels.Length >= 300000) { var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)) }; Parallel.For(0, Height, options, horizontal); Parallel.For(0, Height, options, vertical); }
            else { for (int y = 0; y < Height; y++) horizontal(y); for (int y = 0; y < Height; y++) vertical(y); }
            return new GrayPlane(Width, Height, dst);
        }
        static int Reflect(int x, int n)
        {
            if (n == 1) return 0;
            while (x < 0 || x >= n) x = x < 0 ? -x : 2 * n - 2 - x;
            return x;
        }
    }

    public sealed class Photo : IDisposable
    {
        public string FileName;
        public Bitmap Bitmap;
        public GrayPlane Gray;
        public string Sha256;
        public List<string> Warnings = new List<string>();
        public static Photo Open(string file)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("File not found.", file);
            // GDI+ silently reduces some 16-bit PNGs to 8-bit: reject explicitly.
            using (FileStream stream = File.OpenRead(file))
            {
                byte[] header = new byte[26]; int count = stream.Read(header, 0, header.Length);
                if (count == 26 && header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71 && header[24] == 16)
                    throw new ArgumentException("16-bit PNG is not supported. Use an 8-bit display image without resizing it.");
            }
            Photo result = new Photo(); result.FileName = Path.GetFullPath(file);
            try
            {
                using (Image src = Decode(file))
                {
                    if ((long)src.Width * src.Height > 24000000 || src.Width > 16000 || src.Height > 16000)
                        throw new ArgumentException("The limit is 24 million pixels and 16,000 pixels per side. Select an original-resolution crop instead of resizing.");
                    int bits = Image.GetPixelFormatSize(src.PixelFormat);
                    if (bits > 32 || src.PixelFormat == PixelFormat.Format16bppGrayScale)
                        throw new ArgumentException("High bit depths are not supported: measurements use 8-bit images, not raw sensor data.");
                    if (src.PropertyIdList.Contains(0x102))
                    {
                        byte[] b = src.GetPropertyItem(0x102).Value;
                        for (int k = 0; k + 1 < b.Length; k += 2) if (BitConverter.ToUInt16(b, k) > 8)
                            throw new ArgumentException("TIFF channels above 8 bits are not supported.");
                    }
                    if (src.GetFrameCount(new FrameDimension(src.FrameDimensionsList[0])) > 1)
                        result.Warnings.Add("Multi-page or animated file: only the first frame was loaded.");
                    if (src.PropertyIdList.Contains(0x112))
                    {
                        int orientation = BitConverter.ToUInt16(src.GetPropertyItem(0x112).Value, 0);
                        RotateFlipType[] types = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone,
                            RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX,
                            RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
                        if (orientation >= 2 && orientation <= 8) src.RotateFlip(types[orientation]);
                    }
                    result.Bitmap = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(result.Bitmap))
                    {
                        g.Clear(Color.White); g.CompositingMode = CompositingMode.SourceOver;
                        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
                    }
                    if (Image.IsAlphaPixelFormat(src.PixelFormat)) result.Warnings.Add("Transparency was composited onto white. Exclude transparency boundaries from the measurement region.");
                }
                float[] values = new float[result.Bitmap.Width * result.Bitmap.Height];
                BitmapData data = result.Bitmap.LockBits(new Rectangle(0, 0, result.Bitmap.Width, result.Bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[result.Bitmap.Width * 3];
                    for (int y = 0; y < result.Bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < result.Bitmap.Width; x++) values[y * result.Bitmap.Width + x] = (float)(0.114 * row[3 * x] + 0.587 * row[3 * x + 1] + 0.299 * row[3 * x + 2]);
                    }
                }
                finally { result.Bitmap.UnlockBits(data); }
                result.Gray = new GrayPlane(result.Bitmap.Width, result.Bitmap.Height, values);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".jpg" || ext == ".jpeg") result.Warnings.Add("JPEG compression can change fine detail. PNG or BMP is preferable for repeatable measurements.");
                using (var hash = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(file))
                    result.Sha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        static Image Decode(string file)
        {
            try { return Image.FromFile(file, false); }
            catch (OutOfMemoryException ex) { throw new ArgumentException("The image could not be decoded. Check its format, file integrity and available memory.", ex); }
            catch (ArgumentException ex) { throw new ArgumentException("The file was not recognized as a supported image.", ex); }
        }
        public void Dispose() { if (Bitmap != null) { Bitmap.Dispose(); Bitmap = null; } }
    }

    public sealed class OperatorValues
    {
        public double EOG, Tenengrad, SML, LaplacianVariance, Brenner, EOL;
        public double[] Array() { return new[] { EOG, Tenengrad, SML, LaplacianVariance, Brenner, EOL }; }
    }
    public sealed class ImageMeasures
    {
        public OperatorValues Raw, Filtered;
        public int Width, Height;
        public double Mean, StandardDeviation, ContrastP95P05, NoiseSigmaEstimate, ClippedPercent, DetailIndex;
        public double[] BlurSigmas, BlurEnergy;
        public bool HasStructure;
        public bool NoiseDominated;
        public double NoiseEnergyFractionEstimate, BlurRetention;
        public List<TileMeasure> Tiles = new List<TileMeasure>();
        public List<string> Warnings = new List<string>();
    }
    public sealed class TileMeasure
    {
        public Rectangle Area;
        public double Contrast, DetailIndex;
        public bool Informative;
        public OperatorValues Filtered;
    }
    public sealed class TileComparison
    {
        public Rectangle Area;
        public double? RelativePercent;
        public bool Informative;
    }
    public sealed class MetricComparison
    {
        public string Metric, Unit;
        public double ReferenceRaw, WorkingRaw, ReferenceFiltered, WorkingFiltered;
        public double? RawAmplitudePercent, AdjustedAmplitudePercent;
        public bool UsedForSummary;
    }
    public sealed class AlignmentResult
    {
        // A reference pixel (x,y) corresponds to a working pixel (x+Dx,y+Dy).
        public int Dx, Dy, SearchRadius;
        public double Correlation, SecondPeakCorrelation, RoiCorrelation, FractionalDx, FractionalDy;
        public double ResidualShiftSpread;
        public double AlternateRoiCorrelation;
        public int AlternateRoiDx, AlternateRoiDy;
        public int GeometrySamples;
        public bool GeometryMismatch;
        public Rectangle ContextArea;
        public bool Enabled, Ambiguous, AtSearchLimit;
        public Rectangle ReferenceArea, WorkingArea;
    }
    public sealed class AnalysisReport
    {
        public string ToolVersion, Mode, ReferenceFile, WorkingFile, Status, Interpretation;
        public DateTime CreatedLocal = DateTime.Now;
        public Rectangle RequestedArea;
        public ImageMeasures Reference, Working;
        public AlignmentResult Alignment;
        public List<MetricComparison> Metrics = new List<MetricComparison>();
        public List<string> Warnings = new List<string>();
        public bool ContrastAdjusted, ReliableComparison;
        public bool NoiseMismatch;
        public double? RelativeDetailPercent, LossPercent;
        public double TolerancePercent, AnalysisMilliseconds;
        public List<TileComparison> Tiles = new List<TileComparison>();
        public double? LocalMedianPercent, LocalP25Percent, LocalP75Percent;
        public double? MetricMinPercent, MetricMaxPercent;
        public int InformativeTiles, LowerDetailTiles;
        public bool CachedReference, CachedWorking, CachedAlignment;
        public string ReferenceSha256, WorkingSha256;
    }

    public static class FocusMath
    {
        public static readonly string[] Names = { "EOG", "Tenengrad", "SML", "LAPV", "Brenner 2D", "EOL (thermal)" };
        public static readonly string[] Units = { "levels²/px", "levels²/px", "levels/px", "levels²", "levels²/px", "levels²/px" };
        public static readonly string[] Descriptions = {
            "Mean squared forward differences along X and Y.",
            "Mean Sobel X/Y energy; each kernel is divided by 8.",
            "Mean absolute second differences, step 1.",
            "Variance of the four-neighbor Laplacian.",
            "Mean squared step-2 differences along X and Y.",
            "Mean energy of the thermal-image 20/-4/-1 mask." };
        public const double FilterSigma = 0.7;
        public static void ValidateArea(GrayPlane image, Rectangle area)
        {
            if (!image.Bounds.Contains(area) || area.Width < 16 || area.Height < 16)
                throw new ArgumentException("The region must be inside the image and at least 16×16 pixels.");
        }
        public static OperatorValues Operators(GrayPlane image, CancellationToken token = default(CancellationToken))
        { return Operators(image, image.Bounds, token); }
        struct OperatorSums { public double Eog, Ten, Sml, Eol, Lap, LapSquare, Bx, By; }
        public static OperatorValues Operators(GrayPlane image, Rectangle area, CancellationToken token = default(CancellationToken))
        {
            int w = area.Width, h = area.Height, stride = image.Width, left = area.Left, right = area.Right, top = area.Top, bottom = area.Bottom;
            if (w < 3 || h < 3 || !image.Bounds.Contains(area)) throw new ArgumentException("Metrics require a region inside the image, at least 3×3 pixels.");
            float[] p = image.Pixels; int blocks = (h + 63) / 64; var sums = new OperatorSums[blocks];
            Action<int> calculate = delegate(int block)
            {
                OperatorSums s = new OperatorSums(); int end = Math.Min(bottom, top + (block + 1) * 64);
                for (int y = top + block * 64; y < end; y++)
                {
                    if ((y & 15) == 0) token.ThrowIfCancellationRequested();
                    int start = y * stride; bool forwardY = y < bottom - 1, brennerY = y < bottom - 2, interiorY = y > top && y < bottom - 1;
                    for (int x = left; x < right; x++)
                    {
                        int i = start + x; double c = p[i];
                        if (x < right - 1 && forwardY) { double dx = p[i + 1] - c, dy = p[i + stride] - c; s.Eog += dx * dx + dy * dy; }
                        if (x < right - 2) { double d = p[i + 2] - c; s.Bx += d * d; }
                        if (brennerY) { double d = p[i + 2 * stride] - c; s.By += d * d; }
                        if (!interiorY || x == left || x == right - 1) continue;
                        double l = p[i - 1], r = p[i + 1], u = p[i - stride], dwn = p[i + stride];
                        double nw = p[i - stride - 1], ne = p[i - stride + 1], sw = p[i + stride - 1], se = p[i + stride + 1];
                        double lap = l + r + u + dwn - 4 * c; s.Lap += lap; s.LapSquare += lap * lap;
                        s.Sml += Math.Abs(2 * c - l - r) + Math.Abs(2 * c - u - dwn);
                        double gx = (ne + 2 * r + se - nw - 2 * l - sw) / 8.0, gy = (sw + 2 * dwn + se - nw - 2 * u - ne) / 8.0;
                        s.Ten += gx * gx + gy * gy;
                        double thermalLap = 20 * c - 4 * (l + r + u + dwn) - nw - ne - sw - se; s.Eol += thermalLap * thermalLap;
                    }
                }
                sums[block] = s;
            };
            if (w * h >= 300000 && blocks >= 4) Parallel.For(0, blocks, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)) }, calculate);
            else for (int k = 0; k < blocks; k++) calculate(k);
            OperatorSums total = new OperatorSums();
            foreach (OperatorSums s in sums) { total.Eog += s.Eog; total.Ten += s.Ten; total.Sml += s.Sml; total.Eol += s.Eol; total.Lap += s.Lap; total.LapSquare += s.LapSquare; total.Bx += s.Bx; total.By += s.By; }
            double n = (w - 2) * (h - 2);
            var v = new OperatorValues { EOG = total.Eog / ((w - 1) * (h - 1)), Tenengrad = total.Ten / n, SML = total.Sml / n, EOL = total.Eol / n,
                LaplacianVariance = Math.Max(0, total.LapSquare / n - (total.Lap / n) * (total.Lap / n)), Brenner = .5 * (total.Bx / (h * (w - 2)) + total.By / ((h - 2) * w)) };
            return v;
        }
        public static double GradientEnergy(GrayPlane image, Rectangle area, CancellationToken token = default(CancellationToken))
        {
            float[] p = image.Pixels; int stride = image.Width; double eog = 0;
            for (int y = area.Top; y < area.Bottom - 1; y++)
            {
                if ((y & 31) == 0) token.ThrowIfCancellationRequested();
                for (int x = area.Left; x < area.Right - 1; x++) { int i = y * stride + x; double dx = p[i + 1] - p[i], dy = p[i + stride] - p[i]; eog += dx * dx + dy * dy; }
            }
            return eog / ((area.Width - 1) * (area.Height - 1));
        }
        public static ImageMeasures Measure(GrayPlane p, CancellationToken token = default(CancellationToken))
        {
            ValidateArea(p, p.Bounds);
            ImageMeasures m = new ImageMeasures(); m.Width = p.Width; m.Height = p.Height;
            int[] histogram = new int[256]; double sum = 0, square = 0; int clipped = 0;
            for (int k = 0; k < p.Pixels.Length; k++)
            {
                if ((k & 65535) == 0) token.ThrowIfCancellationRequested(); float v = p.Pixels[k];
                if (float.IsNaN(v) || float.IsInfinity(v) || v < 0 || v > 255) throw new ArgumentException("Luminance values must be between 0 and 255.");
                histogram[Math.Max(0, Math.Min(255, (int)Math.Round(v)))]++; sum += v; square += v * (double)v;
                if (v <= 1 || v >= 254) clipped++;
            }
            m.Mean = sum / p.Pixels.Length; m.StandardDeviation = Math.Sqrt(Math.Max(0, square / p.Pixels.Length - m.Mean * m.Mean));
            m.ContrastP95P05 = Quantile(histogram, p.Pixels.Length, .95) - Quantile(histogram, p.Pixels.Length, .05);
            m.ClippedPercent = 100.0 * clipped / p.Pixels.Length;
            List<double> residuals = new List<double>(); int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(p.Pixels.Length / 50000.0)));
            for (int y = 1; y < p.Height - 1; y += step) for (int x = 1; x < p.Width - 1; x += step)
            {
                int i = y * p.Width + x, w = p.Width;
                double d = p.Pixels[i - w - 1] - 2 * p.Pixels[i - w] + p.Pixels[i - w + 1]
                    - 2 * p.Pixels[i - 1] + 4 * p.Pixels[i] - 2 * p.Pixels[i + 1]
                    + p.Pixels[i + w - 1] - 2 * p.Pixels[i + w] + p.Pixels[i + w + 1];
                residuals.Add(Math.Abs(d));
            }
            m.NoiseSigmaEstimate = Median(residuals) / (6 * 0.67448975);
            GrayPlane filtered = p.Blur(FilterSigma, token);
            m.Raw = Operators(p, token); m.Filtered = Operators(filtered, token);
            m.NoiseEnergyFractionEstimate = Math.Min(1, 4 * m.NoiseSigmaEstimate * m.NoiseSigmaEstimate / Math.Max(1e-10, m.Raw.EOG));
            m.BlurRetention = m.Raw.EOG > 1e-10 ? m.Filtered.EOG / m.Raw.EOG : 0;
            m.NoiseDominated = m.NoiseEnergyFractionEstimate > .9 && m.BlurRetention < .12;
            m.HasStructure = m.ContrastP95P05 >= 5 && m.StandardDeviation >= 1 && m.Filtered.EOG > 0.0025;
            if (m.NoiseDominated) m.HasStructure = false;
            m.DetailIndex = m.HasStructure ? 100 * Math.Sqrt(m.Filtered.Tenengrad) / m.ContrastP95P05 : 0;
            m.BlurSigmas = new[] { 0.0, 0.7, 1.4, 2.8 };
            m.BlurEnergy = new double[4]; m.BlurEnergy[0] = m.Raw.EOG; m.BlurEnergy[1] = m.Filtered.EOG;
            for (int k = 2; k < 4; k++) { GrayPlane blurred = p.Blur(m.BlurSigmas[k], token); m.BlurEnergy[k] = GradientEnergy(blurred, blurred.Bounds, token); }
            int columns = Math.Min(4, Math.Max(1, p.Width / 32)), rows = Math.Min(4, Math.Max(1, p.Height / 32));
            for (int row = 0; row < rows; row++) for (int col = 0; col < columns; col++)
            {
                token.ThrowIfCancellationRequested();
                Rectangle area = Rectangle.FromLTRB(col * p.Width / columns, row * p.Height / rows, (col + 1) * p.Width / columns, (row + 1) * p.Height / rows);
                int[] hist = new int[256];
                for (int y = area.Top; y < area.Bottom; y++) for (int x = area.Left; x < area.Right; x++) hist[(int)Math.Round(p.Pixels[y * p.Width + x])]++;
                double contrast = Quantile(hist, area.Width * area.Height, .95) - Quantile(hist, area.Width * area.Height, .05);
                OperatorValues values = Operators(filtered, area, token);
                // A smooth intensity ramp has contrast and gradient energy but
                // carries almost no information about the focus of fine detail.
                bool informative = contrast >= 5 && values.EOG > .0025 && values.SML >= Math.Max(.01, .0025 * contrast) && !m.NoiseDominated;
                m.Tiles.Add(new TileMeasure { Area = area, Contrast = contrast, Filtered = values, Informative = informative, DetailIndex = informative ? 100 * Math.Sqrt(values.Tenengrad) / contrast : 0 });
            }
            if (!m.HasStructure) m.Warnings.Add("Insufficient contrast or detail to assess focus in this region. Select a target or a textured object.");
            if (m.NoiseSigmaEstimate > Math.Max(2, .06 * m.ContrastP95P05)) m.Warnings.Add("High noise or fine-texture estimate: high frequencies may inflate sharpness. A noise estimate from one image is approximate.");
            if (m.NoiseDominated) m.Warnings.Add("Noise or unstable detail near the pixel-grid limit appears dominant. The index is not used as a focus assessment.");
            if (m.ClippedPercent > 10) m.Warnings.Add("Many values are near the luminance limits. Check highlight/shadow clipping; a binary target can also produce this pattern.");
            if (Math.Min(p.Width, p.Height) < 48) m.Warnings.Add("Small region: the result is sensitive to individual pixels and boundaries.");
            return m;
        }
        static double Quantile(int[] histogram, int count, double q)
        {
            int goal = (int)(q * (count - 1)), total = 0;
            for (int k = 0; k < histogram.Length; k++) { total += histogram[k]; if (total > goal) return k; }
            return 255;
        }
        public static double Median(IEnumerable<double> values)
        {
            double[] v = values.ToArray(); if (v.Length == 0) return 0; System.Array.Sort(v);
            return v.Length % 2 != 0 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
        }
        public static AnalysisReport Single(Photo photo, Rectangle area, CancellationToken token = default(CancellationToken), AnalysisSession session = null)
        {
            ValidateArea(photo.Gray, area); var timer = System.Diagnostics.Stopwatch.StartNew();
            AnalysisReport report = new AnalysisReport { ToolVersion = ProductInfo.DisplayName, Mode = "single", WorkingFile = photo.FileName, WorkingSha256 = photo.Sha256, RequestedArea = area };
            bool cached = false;
            report.Working = session == null ? Measure(photo.Gray.Crop(area, token), token) : session.Measure(photo, area, false, token, out cached);
            report.CachedWorking = cached; report.Warnings.AddRange(photo.Warnings); report.Warnings.AddRange(report.Working.Warnings);
            report.Status = report.Working.HasStructure ? "Detail index" : "Insufficient data";
            report.Interpretation = "Index = 100 × √Tenengrad(σ=0.7) / (P95−P05). This is an unbounded relative scale, not a percentage of correct focus. One image cannot confirm optimal focus; scene content, noise and processing affect the metrics.";
            report.AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds; return report;
        }
        public static AnalysisReport Compare(Photo reference, Photo working, Rectangle area, bool align, int radius, bool contrastAdjusted, double tolerance, CancellationToken token = default(CancellationToken), AnalysisSession session = null)
        {
            ValidateArea(reference.Gray, area);
            if (radius < 0 || radius > 256 || tolerance < 0 || tolerance > 50) throw new ArgumentException("Invalid comparison settings.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            AnalysisReport report = new AnalysisReport { ToolVersion = ProductInfo.DisplayName, Mode = "compare", ReferenceFile = reference.FileName, WorkingFile = working.FileName,
                ReferenceSha256 = reference.Sha256, WorkingSha256 = working.Sha256, RequestedArea = area, ContrastAdjusted = contrastAdjusted, TolerancePercent = tolerance };
            report.Warnings.AddRange(reference.Warnings.Select(x => "Reference: " + x)); report.Warnings.AddRange(working.Warnings.Select(x => "Test: " + x));
            if (contrastAdjusted) report.Warnings.Add("Contrast correction is enabled. For some patterns it can mask contrast loss caused by defocus. Compare this result with the original metric ratios.");
            bool cached = false;
            report.Alignment = session == null ? Registration.Find(reference.Gray, working.Gray, area, align, radius, token) : session.Align(reference, working, area, align, radius, token, out cached);
            report.CachedAlignment = cached;
            AlignmentResult reg = report.Alignment;
            report.Reference = session == null ? Measure(reference.Gray.Crop(reg.ReferenceArea, token), token) : session.Measure(reference, reg.ReferenceArea, true, token, out cached); report.CachedReference = session != null && cached;
            report.Working = session == null ? Measure(working.Gray.Crop(reg.WorkingArea, token), token) : session.Measure(working, reg.WorkingArea, false, token, out cached); report.CachedWorking = session != null && cached;
            report.Warnings.AddRange(report.Reference.Warnings.Select(x => "Reference: " + x)); report.Warnings.AddRange(report.Working.Warnings.Select(x => "Test: " + x));
            ImageMeasures noisier = report.Working.NoiseSigmaEstimate >= report.Reference.NoiseSigmaEstimate ? report.Working : report.Reference;
            ImageMeasures quieter = ReferenceEquals(noisier, report.Working) ? report.Reference : report.Working;
            report.NoiseMismatch = noisier.NoiseSigmaEstimate > Math.Max(3, 2 * quieter.NoiseSigmaEstimate + .5) && noisier.NoiseEnergyFractionEstimate > .5 && noisier.BlurRetention < .3;
            if (report.NoiseMismatch) report.Warnings.Add("The estimated noise contribution differs substantially. The summary percentage is withheld so that noise is not interpreted as improved focus. Raw metrics remain available.");
            if (reference.Gray.Width != working.Gray.Width || reference.Gray.Height != working.Gray.Height)
                report.Warnings.Add("Image dimensions differ. The common region is measured in original pixels. Check that the object has the same pixel scale; no automatic rescaling is applied.");
            if (reg.AtSearchLimit) report.Warnings.Add("The best shift is at the search boundary. Increase the radius or refine the region; this comparison is provisional.");
            if (reg.Ambiguous) report.Warnings.Add("Several target positions match similarly. Alignment is ambiguous. Refine the region or disable alignment for images that are already aligned.");
            if (reg.GeometryMismatch) report.Warnings.Add("Local shifts differ substantially: possible rotation, scale change, perspective or independent object motion. The summary percentage is withheld; select a smaller common object.");
            if (align && Math.Max(Math.Abs(reg.FractionalDx - reg.Dx), Math.Abs(reg.FractionalDy - reg.Dy)) > .25) report.Warnings.Add("The correlation peak suggests a fractional pixel-grid offset. Measurements remain uninterpolated; very fine bars may show a pixel-phase error.");
            if (reg.Correlation < .65) report.Warnings.Add("Weak structural match: possible scene, scale or rotation change, or strong blur. No summary percentage is reported.");
            else if (reg.RoiCorrelation < .65) report.Warnings.Add("Alignment was found from the surrounding context, but the selected region changed substantially. Strong blur or occlusion can cause this; check that it is the same object.");
            if (reg.ReferenceArea.Width * (double)reg.ReferenceArea.Height < area.Width * (double)area.Height * .8)
                report.Warnings.Add("Alignment removed more than 20% of the selected region.");
            double[] rr = report.Reference.Raw.Array(), wr = report.Working.Raw.Array(), rf = report.Reference.Filtered.Array(), wf = report.Working.Filtered.Array();
            double contrastFactor = contrastAdjusted && report.Working.ContrastP95P05 >= 5 ? report.Reference.ContrastP95P05 / report.Working.ContrastP95P05 : 1;
            List<double> ratios = new List<double>();
            for (int k = 0; k < Names.Length; k++)
            {
                bool summary = k == 0 || k == 1 || k == 2 || k == 4;
                double? raw = Ratio(wr[k], rr[k], k == 2), adjusted = Ratio(wf[k], rf[k], k == 2);
                if (adjusted.HasValue) adjusted *= contrastFactor;
                report.Metrics.Add(new MetricComparison { Metric = Names[k], Unit = Units[k], ReferenceRaw = rr[k], WorkingRaw = wr[k],
                    ReferenceFiltered = rf[k], WorkingFiltered = wf[k], RawAmplitudePercent = raw, AdjustedAmplitudePercent = adjusted, UsedForSummary = summary });
                if (summary && adjusted.HasValue) ratios.Add(adjusted.Value);
            }
            report.ReliableComparison = reg.Correlation >= .65 && !reg.AtSearchLimit && !reg.Ambiguous && !reg.GeometryMismatch && !report.NoiseMismatch && report.Reference.HasStructure && report.Working.HasStructure && ratios.Count == 4;
            // A uniform working image is a valid loss of detail when no registration
            // is requested and the user explicitly guarantees the common scene.
            if (!align && report.Reference.HasStructure && !report.Working.HasStructure && report.Working.StandardDeviation < .5)
                report.Warnings.Add("The test region is almost uniform. It may be strongly blurred or contain a different scene; a focus percentage cannot be determined.");
            if (report.ReliableComparison)
            {
                report.RelativeDetailPercent = Median(ratios); report.LossPercent = 100 - report.RelativeDetailPercent;
                report.MetricMinPercent = ratios.Min(); report.MetricMaxPercent = ratios.Max();
                report.Status = report.RelativeDetailPercent < 100 - tolerance ? "Less detail than the reference" : report.RelativeDetailPercent > 100 + tolerance ? "More high-frequency content than the reference" : "Metrics are close to the reference";
                if (ratios.Max() - ratios.Min() > 20) report.Warnings.Add("The metrics differ by more than 20 percentage points. Inspect individual values instead of relying on the median alone.");
            }
            else report.Status = "Reliable comparison unavailable";
            for (int k = 0; k < report.Reference.Tiles.Count; k++)
            {
                TileMeasure rt = report.Reference.Tiles[k], wt = report.Working.Tiles[k]; List<double> tileRatios = new List<double>();
                if (rt.Informative)
                {
                    double[] rv = rt.Filtered.Array(), wv = wt.Filtered.Array();
                    foreach (int metric in new[] { 0, 1, 2, 4 })
                    { double? ratio = Ratio(wv[metric], rv[metric], metric == 2); if (ratio.HasValue) tileRatios.Add(ratio.Value * (contrastAdjusted && wt.Contrast >= 5 ? rt.Contrast / wt.Contrast : 1)); }
                }
                bool informative = rt.Informative && tileRatios.Count == 4;
                double? score = informative ? (double?)Median(tileRatios) : null;
                report.Tiles.Add(new TileComparison { Area = rt.Area, Informative = informative, RelativePercent = score });
            }
            double[] local = report.Tiles.Where(x => x.Informative).Select(x => x.RelativePercent.Value).OrderBy(x => x).ToArray();
            report.InformativeTiles = local.Length; report.LowerDetailTiles = local.Count(x => x < 100 - tolerance);
            if (local.Length > 0)
            {
                report.LocalMedianPercent = Median(local); report.LocalP25Percent = QuantileSorted(local, .25); report.LocalP75Percent = QuantileSorted(local, .75);
                if (report.ReliableComparison && local.Length >= 4 && report.LocalP75Percent - report.LocalP25Percent > 20)
                    report.Warnings.Add("Uneven detail loss: tiles differ by more than 20 percentage points. Inspect the spatial map; a global score can hide local blur.");
            }
            report.Interpretation = "100% is the level of the selected reference, not ideal optical focus. The summary is the median amplitude ratio of EOG, Tenengrad, SML and Brenner after the same Gaussian σ=0.7 px filter" +
                (contrastAdjusted ? " and normalization by P95−P05 contrast" : " without contrast correction") + ". Energy metrics are compared through square roots. Values above 100% are allowed. Changes in scene, scale, AGC/DDE, noise or digital sharpening can imitate a focus change. Alignment compensates integer translation only, not rotation or scale.";
            report.AnalysisMilliseconds = timer.Elapsed.TotalMilliseconds; return report;
        }
        public static double? Ratio(double working, double reference, bool linear)
        { return reference <= 1e-10 ? (double?)null : 100 * (linear ? working / reference : Math.Sqrt(Math.Max(0, working) / reference)); }
        public static double QuantileSorted(double[] sorted, double q)
        { if (sorted.Length == 0) return 0; double i = q * (sorted.Length - 1); int lo = (int)i; return sorted[lo] + (sorted[Math.Min(sorted.Length - 1, lo + 1)] - sorted[lo]) * (i - lo); }
    }

    public static class Registration
    {
        struct Candidate { public int X, Y; public double Score; }
        public static AlignmentResult Find(GrayPlane reference, GrayPlane working, Rectangle roi, bool enabled, int radius, CancellationToken token = default(CancellationToken))
        {
            FocusMath.ValidateArea(reference, roi); int r = enabled ? radius : 0;
            int padding = enabled ? Math.Min(32, Math.Max(8, Math.Min(roi.Width, roi.Height) / 4)) : 0;
            Rectangle context = Rectangle.Intersect(reference.Bounds, Rectangle.Inflate(roi, padding, padding));
            Rectangle workSearch = Rectangle.Intersect(working.Bounds, Rectangle.Inflate(context, r + 5, r + 5));
            if (workSearch.Width < 16 || workSearch.Height < 16) throw new ArgumentException("No common image region. Select a corresponding region in reference-image coordinates.");
            GrayPlane a = reference.Crop(context, token).Blur(2, token), b = working.Crop(workSearch, token).Blur(2, token);
            int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(context.Width, context.Height) / 40.0));
            List<Candidate> candidates = new List<Candidate>();
            int coarse = r == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(r / 12.0));
            Candidate best = new Candidate { Score = -2 };
            // Include zero and both search boundaries, even if radius % coarse != 0.
            SortedSet<int> offsets = new SortedSet<int>(); offsets.Add(0); offsets.Add(-r); offsets.Add(r);
            for (int d = -r; d <= r; d += coarse) offsets.Add(d);
            foreach (int dy in offsets) foreach (int dx in offsets)
            {
                token.ThrowIfCancellationRequested();
                Candidate c = new Candidate { X = dx, Y = dy, Score = Ncc(a, b, context.X + dx - workSearch.X, context.Y + dy - workSearch.Y, stride) };
                candidates.Add(c); if (Better(c, best)) best = c;
            }
            // Refine several spatially separated coarse candidates. A single seed
            // can lock onto an alias of a regular four-bar target.
            var seeds = new List<Candidate>();
            foreach (Candidate c in candidates.OrderByDescending(c => c.Score))
            { if (!seeds.Any(s => Math.Abs(s.X - c.X) + Math.Abs(s.Y - c.Y) < Math.Max(2, coarse))) seeds.Add(c); if (seeds.Count == 4) break; }
            int fineStride = Math.Max(1, (int)Math.Ceiling(Math.Max(context.Width, context.Height) / 72.0));
            best = new Candidate { Score = -2 }; candidates.Clear(); var visited = new HashSet<long>();
            foreach (Candidate seed in seeds) for (int dy = Math.Max(-r, seed.Y - coarse); dy <= Math.Min(r, seed.Y + coarse); dy++)
                for (int dx = Math.Max(-r, seed.X - coarse); dx <= Math.Min(r, seed.X + coarse); dx++)
                {
                    token.ThrowIfCancellationRequested(); if (!visited.Add(((long)(dx + 1024) << 32) | (uint)(dy + 1024))) continue;
                    Candidate c = new Candidate { X = dx, Y = dy, Score = Ncc(a, b, context.X + dx - workSearch.X, context.Y + dy - workSearch.Y, fineStride) };
                    candidates.Add(c); if (Better(c, best)) best = c;
                }
            double second = -1;
            foreach (Candidate c in candidates) if (Math.Abs(c.X - best.X) + Math.Abs(c.Y - best.Y) > Math.Max(6, coarse * 2)) second = Math.Max(second, c.Score);
            Rectangle validWorkingInReference = new Rectangle(-best.X, -best.Y, working.Width, working.Height);
            Rectangle common = Rectangle.Intersect(roi, validWorkingInReference); FocusMath.ValidateArea(reference, common);
            Rectangle localRoi = new Rectangle(roi.X - context.X, roi.Y - context.Y, roi.Width, roi.Height);
            int ox = context.X + best.X - workSearch.X, oy = context.Y + best.Y - workSearch.Y;
            AlignmentResult result = new AlignmentResult { Enabled = enabled, SearchRadius = radius, Dx = best.X, Dy = best.Y, Correlation = Math.Max(-1, best.Score), SecondPeakCorrelation = second,
                Ambiguous = enabled && best.Score > .65 && second > .65 && best.Score - second < .015,
                AtSearchLimit = enabled && r > 0 && (Math.Abs(best.X) == r || Math.Abs(best.Y) == r), ReferenceArea = common,
                WorkingArea = new Rectangle(common.X + best.X, common.Y + best.Y, common.Width, common.Height), ContextArea = context,
                RoiCorrelation = Ncc(a, b, ox, oy, Math.Max(1, Math.Min(roi.Width, roi.Height) / 40), localRoi) };
            result.FractionalDx = best.X; result.FractionalDy = best.Y;
            if (enabled && !result.AtSearchLimit)
            {
                result.FractionalDx += PeakOffset(Ncc(a, b, ox - 1, oy, fineStride), best.Score, Ncc(a, b, ox + 1, oy, fineStride));
                result.FractionalDy += PeakOffset(Ncc(a, b, ox, oy - 1, fineStride), best.Score, Ncc(a, b, ox, oy + 1, fineStride));
            }
            if (enabled && best.Score >= .65 && result.RoiCorrelation < .85)
            {
                Candidate alternate = new Candidate { X = best.X, Y = best.Y, Score = result.RoiCorrelation };
                int step = Math.Max(1, Math.Min(roi.Width, roi.Height) / 32);
                foreach (int dy in offsets) foreach (int dx in offsets)
                {
                    token.ThrowIfCancellationRequested();
                    Candidate c = new Candidate { X = dx, Y = dy, Score = Ncc(a, b, context.X + dx - workSearch.X, context.Y + dy - workSearch.Y, step, localRoi) };
                    if (Better(c, alternate)) alternate = c;
                }
                Candidate seed = alternate;
                for (int dy = Math.Max(-r, seed.Y - coarse); dy <= Math.Min(r, seed.Y + coarse); dy++) for (int dx = Math.Max(-r, seed.X - coarse); dx <= Math.Min(r, seed.X + coarse); dx++)
                {
                    Candidate c = new Candidate { X = dx, Y = dy, Score = Ncc(a, b, context.X + dx - workSearch.X, context.Y + dy - workSearch.Y, step, localRoi) };
                    if (Better(c, alternate)) alternate = c;
                }
                result.AlternateRoiCorrelation = alternate.Score; result.AlternateRoiDx = alternate.X; result.AlternateRoiDy = alternate.Y;
                if (alternate.Score >= .85 && alternate.Score - result.RoiCorrelation > .12 && Math.Abs(alternate.X - best.X) + Math.Abs(alternate.Y - best.Y) > 3) result.GeometryMismatch = true;
            }
            if (enabled && best.Score >= .65 && roi.Width >= 128 && roi.Height >= 128)
            {
                var localShifts = new List<Point>();
                for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
                {
                    token.ThrowIfCancellationRequested();
                    Rectangle tile = Rectangle.FromLTRB(localRoi.X + col * roi.Width / 3, localRoi.Y + row * roi.Height / 3, localRoi.X + (col + 1) * roi.Width / 3, localRoi.Y + (row + 1) * roi.Height / 3);
                    Candidate local = new Candidate { Score = -2 }; double runner = -1;
                    for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
                    {
                        Candidate c = new Candidate { X = dx, Y = dy, Score = Ncc(a, b, ox + dx, oy + dy, Math.Max(1, tile.Width / 24), tile) };
                        if (Better(c, local)) { runner = local.Score; local = c; } else runner = Math.Max(runner, c.Score);
                    }
                    if (local.Score >= .85 && local.Score - runner >= .002) localShifts.Add(new Point(local.X, local.Y));
                }
                result.GeometrySamples = localShifts.Count;
                foreach (Point p in localShifts) foreach (Point q in localShifts) result.ResidualShiftSpread = Math.Max(result.ResidualShiftSpread, Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)));
                result.GeometryMismatch |= result.GeometrySamples >= 4 && result.ResidualShiftSpread > 3.5;
            }
            return result;
        }
        static double PeakOffset(double left, double center, double right)
        { double d = left - 2 * center + right; return d < -1e-8 && center >= left && center >= right ? Math.Max(-.5, Math.Min(.5, .5 * (left - right) / d)) : 0; }
        static bool Better(Candidate c, Candidate best)
        { return c.Score > best.Score + 1e-9 || (Math.Abs(c.Score - best.Score) <= 1e-9 && Math.Abs(c.X) + Math.Abs(c.Y) < Math.Abs(best.X) + Math.Abs(best.Y)); }
        static double Ncc(GrayPlane a, GrayPlane b, int dx, int dy, int step)
        { return Ncc(a, b, dx, dy, step, a.Bounds); }
        static double Ncc(GrayPlane a, GrayPlane b, int dx, int dy, int step, Rectangle area)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; int n = 0, possible = 0;
            for (int y = area.Top + 4; y < area.Bottom - 4; y += step) for (int x = area.Left + 4; x < area.Right - 4; x += step)
            {
                possible++; int bx = x + dx, by = y + dy;
                if (bx < 4 || by < 4 || bx >= b.Width - 4 || by >= b.Height - 4) continue;
                double u = a.Pixels[y * a.Width + x], v = b.Pixels[by * b.Width + bx];
                sx += u; sy += v; sxx += u * u; syy += v * v; sxy += u * v; n++;
            }
            if (n < 32 || n < possible * .7) return -1;
            double vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
            if (vx < 1e-8 || vy < 1e-8) return 0;
            return Math.Max(-1, Math.Min(1, (sxy - sx * sy / n) / Math.Sqrt(vx * vy)));
        }
    }

    public static class ReportFiles
    {
        public static string Json(AnalysisReport report)
        { return new JavaScriptSerializer { MaxJsonLength = 16000000 }.Serialize(report); }
        public static string Text(AnalysisReport r)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine(r.Mode == "compare" ? "REFERENCE COMPARISON" : "SINGLE-IMAGE SHARPNESS");
            b.AppendLine(r.ToolVersion + " · " + r.CreatedLocal.ToString("yyyy-MM-dd HH:mm"));
            b.AppendLine(r.Status); b.AppendLine((r.Reference == null ? "Image: " : "Test: ") + r.WorkingFile);
            if (r.Reference != null) b.AppendLine("Reference: " + r.ReferenceFile);
            if (r.RelativeDetailPercent.HasValue) b.AppendLine("Relative detail: " + F(r.RelativeDetailPercent.Value) + "%; change: " + F(r.RelativeDetailPercent.Value - 100) + "%");
            if (r.Mode == "single") b.AppendLine("Detail index: " + (r.Working.HasStructure ? F(r.Working.DetailIndex) : "unavailable"));
            if (r.Alignment != null) b.AppendLine("Test-image shift: dx=" + r.Alignment.Dx + ", dy=" + r.Alignment.Dy + " px; NCC=" + F(r.Alignment.Correlation));
            if (r.Alignment != null) b.AppendLine("ROI NCC=" + F(r.Alignment.RoiCorrelation) + "; fractional estimate=" + F(r.Alignment.FractionalDx) + "," + F(r.Alignment.FractionalDy) + "; local-shift spread=" + F(r.Alignment.ResidualShiftSpread) + " px.");
            if (r.LocalMedianPercent.HasValue) b.AppendLine("Local detail: median=" + F(r.LocalMedianPercent.Value) + "%; P25–P75=" + F(r.LocalP25Percent.Value) + "–" + F(r.LocalP75Percent.Value) + "%; below tolerance=" + r.LowerDetailTiles + "/" + r.InformativeTiles + ". This is variation across tiles, not a confidence interval.");
            b.AppendLine(r.Interpretation); b.AppendLine();
            if (r.Reference == null)
            {
                double[] raw = r.Working.Raw.Array(), filtered = r.Working.Filtered.Array();
                for (int k = 0; k < raw.Length; k++) b.AppendLine(FocusMath.Names[k] + ": raw=" + F(raw[k]) + "; σ0.7=" + F(filtered[k]));
            }
            else foreach (MetricComparison m in r.Metrics) b.AppendLine(m.Metric + ": reference=" + F(m.ReferenceRaw) + "; test=" + F(m.WorkingRaw) + "; ratio=" + Nullable(m.RawAmplitudePercent) + "%; processed=" + Nullable(m.AdjustedAmplitudePercent) + "%");
            b.AppendLine(); b.AppendLine("Region diagnostics: " + r.Working.Width + "×" + r.Working.Height + " px; contrast=" + F(r.Working.ContrastP95P05) + "; noise≈" + F(r.Working.NoiseSigmaEstimate) + "; clipping=" + F(r.Working.ClippedPercent) + "%.");
            foreach (string s in r.Warnings) b.AppendLine("• " + s);
            b.AppendLine((r.Reference == null ? "Image SHA-256: " : "Test SHA-256: ") + r.WorkingSha256); if (r.Reference != null) b.AppendLine("Reference SHA-256: " + r.ReferenceSha256);
            b.AppendLine("Analysis time: " + F(r.AnalysisMilliseconds) + " ms (excluding file reading and UI rendering)."); return b.ToString();
        }
        public static void Save(string file, AnalysisReport r)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant(); string content;
            string full = Path.GetFullPath(file);
            if (string.Equals(full, r.WorkingFile, StringComparison.OrdinalIgnoreCase) || string.Equals(full, r.ReferenceFile, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A report cannot overwrite a source image.");
            if (ext != ".json" && ext != ".csv" && ext != ".txt" && ext != ".html") throw new ArgumentException("Save the report as .html, .json, .csv or .txt.");
            if (ext == ".html") content = ReportHtml.Render(r);
            else if (ext == ".json") content = Json(r);
            else if (ext == ".csv")
            {
                StringBuilder b = new StringBuilder("metric,reference_raw,working_raw,reference_filtered,working_filtered,raw_amplitude_percent,adjusted_amplitude_percent,used_for_summary,tool_version,contrast_adjusted,tolerance_percent,requested_roi,reference_roi,working_roi,reference_sha256,working_sha256\r\n");
                string metadata = "," + string.Join(",", new[] { r.ToolVersion, r.ContrastAdjusted ? "1" : "0", r.Reference == null ? "" : Inv(r.TolerancePercent), Rect(r.RequestedArea), r.Alignment == null ? "" : Rect(r.Alignment.ReferenceArea), r.Alignment == null ? Rect(r.RequestedArea) : Rect(r.Alignment.WorkingArea), r.ReferenceSha256 ?? "", r.WorkingSha256 ?? "" });
                if (r.Reference != null) foreach (MetricComparison m in r.Metrics)
                    b.AppendLine(string.Join(",", new[] { m.Metric, Inv(m.ReferenceRaw), Inv(m.WorkingRaw), Inv(m.ReferenceFiltered), Inv(m.WorkingFiltered), m.RawAmplitudePercent.HasValue ? Inv(m.RawAmplitudePercent.Value) : "", m.AdjustedAmplitudePercent.HasValue ? Inv(m.AdjustedAmplitudePercent.Value) : "", m.UsedForSummary ? "1" : "0" }) + metadata);
                else
                {
                    double[] raw = r.Working.Raw.Array(), filtered = r.Working.Filtered.Array();
                    for (int k = 0; k < raw.Length; k++) b.AppendLine(FocusMath.Names[k] + ",," + Inv(raw[k]) + ",," + Inv(filtered[k]) + ",,,0" + metadata);
                }
                content = b.ToString();
            }
            else content = Text(r);
            File.WriteAllText(file, content, new UTF8Encoding(true));
        }
        public static string F(double x) { return x.ToString("0.###", CultureInfo.InvariantCulture); }
        static string Inv(double x) { return x.ToString("G17", CultureInfo.InvariantCulture); }
        static string Rect(Rectangle r) { return r.X + ":" + r.Y + ":" + r.Width + ":" + r.Height; }
        public static string Nullable(double? x) { return x.HasValue ? F(x.Value) : "—"; }
    }
}
