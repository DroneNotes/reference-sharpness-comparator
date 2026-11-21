using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PhotoFocus
{
    public sealed class BatchItem
    {
        public string File, Error, ToolVersion;
        public AnalysisReport Report;
    }
    public static class BatchAnalysis
    {
        public static List<BatchItem> Run(IEnumerable<string> files, Photo reference, Rectangle? roi, bool align, int radius, bool normalize, double tolerance, CancellationToken token, Action<BatchItem, int, int> progress)
        {
            string[] paths = files.ToArray(); var results = new List<BatchItem>(); var session = new AnalysisSession();
            if (reference != null)
            {
                if (radius < 0 || radius > 256 || tolerance < 0 || tolerance > 50) throw new ArgumentException("Invalid comparison settings.");
                FocusMath.ValidateArea(reference.Gray, roi ?? reference.Gray.Bounds);
            }
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); BatchItem item = new BatchItem { File = paths[i], ToolVersion = ProductInfo.DisplayName };
                    try
                    {
                        using (Photo working = Photo.Open(paths[i]))
                        {
                            token.ThrowIfCancellationRequested();
                            item.Report = reference == null ? session.Single(working, roi ?? working.Gray.Bounds, token) : session.Compare(reference, working, roi ?? reference.Gray.Bounds, align, radius, normalize, tolerance, token);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { item.Error = ex.Message; }
                    results.Add(item); if (progress != null) progress(item, i + 1, paths.Length);
                }
            }
            finally { session.Clear(); }
            return results;
        }
        public static void Save(string file, IList<BatchItem> rows)
        {
            string full = Path.GetFullPath(file);
            if (rows.Any(r => string.Equals(full, Path.GetFullPath(r.File), StringComparison.OrdinalIgnoreCase) || (r.Report != null && string.Equals(full, r.Report.ReferenceFile, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("The result cannot overwrite a source image.");
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".json") { File.WriteAllText(file, new JavaScriptSerializer { MaxJsonLength = 64000000 }.Serialize(rows), new UTF8Encoding(true)); return; }
            if (ext != ".csv") throw new ArgumentException("Save a batch as .csv or .json.");
            var b = new StringBuilder("file,status,relative_detail_percent,detail_index,local_p25,local_median,local_p75,lower_detail_tiles,informative_tiles,ncc,dx,dy,roi_x,roi_y,roi_width,roi_height,contrast_adjusted,tolerance_percent,tool_version,warnings,error\r\n");
            foreach (BatchItem item in rows)
            {
                AnalysisReport r = item.Report;
                if (r == null) { var fields = new string[21]; fields[0] = Csv(item.File); fields[1] = "error"; fields[18] = item.ToolVersion; fields[20] = Csv(item.Error); b.AppendLine(string.Join(",", fields)); continue; }
                b.AppendLine(string.Join(",", new[] { Csv(item.File), Csv(r.Status), Num(r.RelativeDetailPercent), r.Working.HasStructure ? Num(r.Working.DetailIndex) : "", Num(r.LocalP25Percent), Num(r.LocalMedianPercent), Num(r.LocalP75Percent),
                    r.LowerDetailTiles.ToString(), r.InformativeTiles.ToString(), r.Alignment == null ? "" : Num(r.Alignment.Correlation), r.Alignment == null ? "" : r.Alignment.Dx.ToString(), r.Alignment == null ? "" : r.Alignment.Dy.ToString(),
                    r.RequestedArea.X.ToString(), r.RequestedArea.Y.ToString(), r.RequestedArea.Width.ToString(), r.RequestedArea.Height.ToString(), r.ContrastAdjusted ? "1" : "0", r.Reference == null ? "" : Num(r.TolerancePercent), r.ToolVersion, Csv(string.Join(" | ", r.Warnings)), "" }));
            }
            File.WriteAllText(file, b.ToString(), new UTF8Encoding(true));
        }
        static string Num(double? value) { return value.HasValue ? value.Value.ToString("G17", CultureInfo.InvariantCulture) : ""; }
        static string Csv(string value)
        {
            value = value ?? ""; if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    public sealed class BatchForm : Form
    {
        readonly Photo reference; readonly Rectangle? roi; readonly bool align, normalize; readonly int radius; readonly double tolerance;
        readonly BackgroundWorker worker = new BackgroundWorker { WorkerReportsProgress = true };
        readonly DataGridView table = new DataGridView(); readonly List<BatchItem> results = new List<BatchItem>();
        readonly Button select = new Button { Text = "Select images and start…", AutoSize = true }, cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false }, save = new Button { Text = "Save table…", AutoSize = true, Enabled = false };
        readonly Label status = new Label { Dock = DockStyle.Fill, AutoSize = true }; readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill };
        CancellationTokenSource cancellation; bool closing;
        public BatchForm(Photo referencePhoto, Rectangle? area, bool register, int searchRadius, bool adjustContrast, double acceptedTolerance, string smoke = null)
        {
            reference = referencePhoto; roi = area; align = register; radius = searchRadius; normalize = adjustContrast; tolerance = acceptedTolerance;
            Text = reference == null ? "Batch image analysis" : "Batch reference comparison"; Font = new Font("Segoe UI", 9.5f); Size = new Size(1150, 650); MinimumSize = new Size(850, 480); StartPosition = FormStartPosition.CenterParent;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; controls.Controls.Add(select); controls.Controls.Add(cancel); controls.Controls.Add(save); root.Controls.Add(controls, 0, 0);
            string coordinates = roi.HasValue ? roi.Value.X + "," + roi.Value.Y + " · " + roi.Value.Width + "×" + roi.Value.Height + " px" : "full image";
            root.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8), Text = (reference == null ? "Same region for all files: " : "Reference: " + Path.GetFileName(reference.FileName) + " · Region: ") + coordinates + ". Do not rank unrelated scenes by a single focus index." }, 0, 1);
            table.Dock = DockStyle.Fill; table.ReadOnly = true; table.AllowUserToAddRows = table.AllowUserToDeleteRows = false; table.RowHeadersVisible = false; table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; table.BackgroundColor = Color.White;
            string[] columns = reference == null ? new[] { "File", "Result", "Detail index", "Warnings" } : new[] { "File", "Result", "Detail / reference, %", "Index", "NCC", "Warnings" };
            foreach (string column in columns) table.Columns.Add(column, column);
            table.Columns[0].FillWeight = 160; table.Columns[1].FillWeight = 180; table.Columns[columns.Length - 1].FillWeight = 190;
            root.Controls.Add(table, 0, 2); root.Controls.Add(progress, 0, 3); root.Controls.Add(status, 0, 4); status.Text = "Files are read individually. An error in one file does not stop the remaining files.";
            select.Click += delegate
            {
                using (var d = new OpenFileDialog { Multiselect = true, Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) Start(d.FileNames);
            };
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            save.Click += delegate
            {
                using (var d = new SaveFileDialog { FileName = "photo-batch", DefaultExt = "csv", Filter = "Summary table CSV|*.csv|Full results JSON|*.json" }) if (d.ShowDialog(this) == DialogResult.OK)
                    try { BatchAnalysis.Save(d.FileName, results); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            };
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                string[] files = (string[])e.Argument;
                BatchAnalysis.Run(files, reference, roi, align, radius, normalize, tolerance, cancellation.Token, delegate(BatchItem item, int index, int count) { worker.ReportProgress(index * 100 / count, item); });
            };
            worker.ProgressChanged += delegate(object sender, ProgressChangedEventArgs e)
            {
                BatchItem item = (BatchItem)e.UserState; results.Add(item); AnalysisReport r = item.Report;
                if (reference == null) table.Rows.Add(Path.GetFileName(item.File), r == null ? "Error" : r.Status, r == null || !r.Working.HasStructure ? "—" : r.Working.DetailIndex.ToString("0.00"), r == null ? item.Error : string.Join(" | ", r.Warnings));
                else table.Rows.Add(Path.GetFileName(item.File), r == null ? "Error" : r.Status, r == null || !r.RelativeDetailPercent.HasValue ? "—" : r.RelativeDetailPercent.Value.ToString("0.0"), r == null || !r.Working.HasStructure ? "—" : r.Working.DetailIndex.ToString("0.00"), r == null || r.Alignment == null ? "—" : r.Alignment.Correlation.ToString("0.00"), r == null ? item.Error : string.Join(" | ", r.Warnings));
                progress.Value = e.ProgressPercentage; status.Text = "Processed: " + results.Count; save.Enabled = false;
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                cancellation.Dispose(); cancellation = null;
                if (closing) { Close(); return; }
                select.Enabled = true; cancel.Enabled = false; save.Enabled = results.Count > 0;
                status.Text = (e.Error is OperationCanceledException ? "Canceled. Completed results retained: " : e.Error != null ? "Error: " + e.Error.Message + ". Processed: " : "Done. Processed: ") + results.Count;
                if (smoke != null)
                {
                    string name = reference == null ? "single-batch-ui" : "compare-batch-ui";
                    BatchAnalysis.Save(Path.Combine(smoke, name + ".json"), results);
                    using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(smoke, name + ".png")); }
                    if (results.Count != 3) Environment.ExitCode = 2; Close();
                }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (worker.IsBusy) { e.Cancel = true; closing = true; cancellation.Cancel(); Hide(); } };
            FormClosed += delegate { worker.Dispose(); };
            if (smoke != null)
            {
                ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-30000, -30000);
                Shown += delegate { Start(new[] { Path.Combine(smoke, "reference.png"), Path.Combine(smoke, "missing.png"), Path.Combine(smoke, "working.png") }); };
            }
        }
        public void Start(string[] files)
        {
            if (worker.IsBusy || files.Length == 0) return; results.Clear(); table.Rows.Clear(); progress.Value = 0; cancellation = new CancellationTokenSource(); select.Enabled = save.Enabled = false; cancel.Enabled = true;
            worker.RunWorkerAsync(files);
        }
    }
}
