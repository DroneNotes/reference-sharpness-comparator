using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace PhotoFocus
{
    static class Program
    {
        static readonly bool CompareMode = ProductInfo.IsComparison;
        [STAThread]
        static int Main(string[] args)
        {
            CultureInfo english = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentCulture = english;
            CultureInfo.DefaultThreadCurrentUICulture = english;
            Thread.CurrentThread.CurrentCulture = english;
            Thread.CurrentThread.CurrentUICulture = english;
            try
            {
                Dictionary<string, string> options = Parse(args);
                if (options.ContainsKey("--batch"))
                {
                    if (!options.ContainsKey("--out")) throw new ArgumentException("For a batch, specify --out summary.csv or results.json.");
                    if (!Directory.Exists(options["--batch"])) throw new DirectoryNotFoundException("The image folder was not found.");
                    string[] extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
                    string[] files = Directory.GetFiles(options["--batch"]).Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (files.Length == 0) throw new ArgumentException("The folder contains no supported images.");
                    Photo reference = null;
                    try
                    {
                        if (CompareMode) { if (!options.ContainsKey("--reference")) throw new ArgumentException("--reference is required."); reference = Photo.Open(options["--reference"]); }
                        Rectangle? roi = options.ContainsKey("--roi") ? (Rectangle?)Area(options, Rectangle.Empty) : null;
                        var rows = BatchAnalysis.Run(files, reference, roi, !options.ContainsKey("--no-align"), Integer(options, "--radius", 32), options.ContainsKey("--normalize"), Integer(options, "--tolerance", 5), CancellationToken.None, null);
                        BatchAnalysis.Save(options["--out"], rows); return rows.Any(r => r.Error != null) ? 1 : 0;
                    }
                    finally { if (reference != null) reference.Dispose(); }
                }
                if (options.ContainsKey("--image") && !options.ContainsKey("--smoke"))
                {
                    if (!options.ContainsKey("--out")) throw new ArgumentException("For command-line analysis, specify --out report.html, .json, .csv or .txt.");
                    using (Photo working = Photo.Open(options["--image"]))
                    {
                        AnalysisReport report;
                        if (CompareMode)
                        {
                            if (!options.ContainsKey("--reference")) throw new ArgumentException("--reference must specify the reference-image path.");
                            using (Photo reference = Photo.Open(options["--reference"]))
                            {
                                Rectangle area = Area(options, reference.Gray.Bounds);
                                report = FocusMath.Compare(reference, working, area, !options.ContainsKey("--no-align"), Integer(options, "--radius", 32), options.ContainsKey("--normalize"), Integer(options, "--tolerance", 5));
                            }
                        }
                        else report = FocusMath.Single(working, Area(options, working.Gray.Bounds));
                        ReportFiles.Save(options["--out"], report); return 0;
                    }
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (options.ContainsKey("--smoke-batch"))
                {
                    string folder = options["--smoke-batch"];
                    using (Photo reference = CompareMode ? Photo.Open(Path.Combine(folder, "reference.png")) : null)
                    using (var form = new BatchForm(reference, null, true, 32, false, 5, folder)) Application.Run(form);
                    return Environment.ExitCode;
                }
                using (PhotoForm form = new PhotoForm(CompareMode, options.ContainsKey("--smoke") ? options["--smoke"] : null)) Application.Run(form);
                return Environment.ExitCode;
            }
            catch (Exception ex)
            {
                if (args.Length > 0) File.WriteAllText(Path.Combine(Path.GetTempPath(), ProductInfo.Name + "-error.txt"), ex.ToString());
                else MessageBox.Show(ex.Message, ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }
        static Dictionary<string, string> Parse(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(new[] { "--no-align", "--no-normalize", "--normalize" }, StringComparer.OrdinalIgnoreCase);
            var valued = new HashSet<string>(new[] { "--image", "--reference", "--out", "--roi", "--radius", "--tolerance", "--smoke", "--smoke-batch", "--batch" }, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (d.ContainsKey(key)) throw new ArgumentException("Duplicate option: " + key);
                if (flags.Contains(key)) d.Add(key, "true");
                else if (valued.Contains(key) && i + 1 < args.Length) d.Add(key, args[++i]);
                else throw new ArgumentException("Unknown option or missing value: " + key);
            }
            if (d.Count > 0 && !d.ContainsKey("--image") && !d.ContainsKey("--smoke") && !d.ContainsKey("--smoke-batch") && !d.ContainsKey("--batch")) throw new ArgumentException("Specify --image / --batch, or start the application without arguments.");
            if (new[] { "--image", "--batch", "--smoke", "--smoke-batch" }.Count(d.ContainsKey) > 1) throw new ArgumentException("Batch, single-image and test modes must be run separately.");
            if (d.ContainsKey("--normalize") && d.ContainsKey("--no-normalize")) throw new ArgumentException("Select only one contrast-correction mode.");
            if (!CompareMode && (d.ContainsKey("--reference") || d.ContainsKey("--no-align") || d.ContainsKey("--radius") || d.ContainsKey("--normalize") || d.ContainsKey("--no-normalize") || d.ContainsKey("--tolerance"))) throw new ArgumentException("Reference-comparison options are not supported by this application.");
            return d;
        }
        static int Integer(Dictionary<string, string> args, string name, int fallback)
        { return args.ContainsKey(name) ? int.Parse(args[name], CultureInfo.InvariantCulture) : fallback; }
        static Rectangle Area(Dictionary<string, string> args, Rectangle fallback)
        {
            if (!args.ContainsKey("--roi")) return fallback;
            string[] v = args["--roi"].Split(','); if (v.Length != 4) throw new ArgumentException("--roi must use x,y,width,height.");
            return new Rectangle(int.Parse(v[0], CultureInfo.InvariantCulture), int.Parse(v[1], CultureInfo.InvariantCulture), int.Parse(v[2], CultureInfo.InvariantCulture), int.Parse(v[3], CultureInfo.InvariantCulture));
        }
    }
}
