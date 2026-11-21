using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace PhotoFocus
{
    public sealed class TileOverlay
    {
        public Rectangle Area;
        public double Value;
        public bool Valid, Relative;
    }
    public sealed class PhotoCanvas : Control
    {
        public Bitmap Image;
        public Rectangle Area, MeasuredArea;
        public bool AllowRegionSelection;
        public List<TileOverlay> Tiles = new List<TileOverlay>();
        public bool ShowTiles;
        public event Action AreaChanged;
        public event Action<string> FileDropped;
        float zoom = 1; PointF pan; Point start, last; bool selecting, panning;
        Rectangle drag;
        public PhotoCanvas()
        {
            DoubleBuffered = true; BackColor = Color.FromArgb(31, 34, 38); Dock = DockStyle.Fill; AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { if (Enabled && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { string[] files = (string[])e.Data.GetData(DataFormats.FileDrop); if (files.Length > 0 && FileDropped != null) FileDropped(files[0]); };
        }
        public void SetImage(Bitmap bitmap)
        { Image = bitmap; Area = bitmap == null ? Rectangle.Empty : new Rectangle(0, 0, bitmap.Width, bitmap.Height); MeasuredArea = Rectangle.Empty; Tiles.Clear(); Fit(); }
        public void Fit() { zoom = 1; pan = PointF.Empty; Invalidate(); }
        RectangleF Display()
        {
            if (Image == null) return RectangleF.Empty;
            float s = Math.Min((ClientSize.Width - 16f) / Image.Width, (ClientSize.Height - 16f) / Image.Height) * zoom;
            s = Math.Max(.001f, s); float w = Image.Width * s, h = Image.Height * s;
            return new RectangleF((ClientSize.Width - w) / 2 + pan.X, (ClientSize.Height - h) / 2 + pan.Y, w, h);
        }
        Point ImagePoint(Point screen)
        {
            RectangleF d = Display();
            return new Point(Math.Max(0, Math.Min(Image.Width, (int)Math.Floor((screen.X - d.X) * Image.Width / d.Width))),
                Math.Max(0, Math.Min(Image.Height, (int)Math.Floor((screen.Y - d.Y) * Image.Height / d.Height))));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics;
            if (Image == null)
            {
                TextRenderer.DrawText(g, AllowRegionSelection ? "Open or drop an image\nThen drag to select a region" : "Open or drop the test image", Font, ClientRectangle, Color.Silver, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            RectangleF d = Display(); g.InterpolationMode = d.Width > Image.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(Image, d, new RectangleF(0, 0, Image.Width, Image.Height), GraphicsUnit.Pixel);
            if (ShowTiles)
            {
                double max = Math.Max(.01, Tiles.Where(t => t.Valid).Select(t => t.Value).DefaultIfEmpty(1).Max());
                foreach (TileOverlay tile in Tiles)
                {
                    RectangleF box = new RectangleF(d.X + tile.Area.X * d.Width / Image.Width, d.Y + tile.Area.Y * d.Height / Image.Height, tile.Area.Width * d.Width / Image.Width, tile.Area.Height * d.Height / Image.Height);
                    double value = tile.Relative ? Math.Max(0, Math.Min(1, tile.Value / 110)) : tile.Value / max;
                    Color color = !tile.Valid ? Color.Gray : Color.FromArgb((int)(225 * (1 - value)), (int)(80 + 70 * value), (int)(45 + 180 * value));
                    using (Brush brush = new SolidBrush(Color.FromArgb(65, color))) g.FillRectangle(brush, box);
                    using (Pen pen = new Pen(Color.FromArgb(180, Color.White))) g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                    if (box.Width >= 45 && box.Height >= 27)
                    {
                        string label = tile.Valid ? tile.Value.ToString(tile.Relative ? "0'%'" : "0.0") : "—";
                        Size size = TextRenderer.MeasureText(label, Font); Rectangle textBox = new Rectangle((int)(box.X + (box.Width - size.Width) / 2), (int)(box.Y + (box.Height - size.Height) / 2), size.Width, size.Height);
                        using (Brush bg = new SolidBrush(Color.FromArgb(195, Color.Black))) g.FillRectangle(bg, textBox); TextRenderer.DrawText(g, label, Font, textBox, Color.White);
                    }
                }
            }
            DrawArea(g, d, selecting ? drag : Area, Color.DeepSkyBlue, DashStyle.Solid);
            if (!MeasuredArea.IsEmpty && MeasuredArea != Area) DrawArea(g, d, MeasuredArea, Color.Orange, DashStyle.Dash);
            string info = Image.Width + "×" + Image.Height + " px  |  ROI " + Area.X + ", " + Area.Y + " · " + Area.Width + "×" + Area.Height;
            Size text = TextRenderer.MeasureText(info, Font); g.FillRectangle(Brushes.Black, 0, Height - text.Height - 6, Math.Min(Width, text.Width + 10), text.Height + 6);
            TextRenderer.DrawText(g, info, Font, new Point(5, Height - text.Height - 3), Color.White);
        }
        void DrawArea(Graphics g, RectangleF d, Rectangle r, Color color, DashStyle dash)
        {
            if (r.IsEmpty) return;
            using (Pen pen = new Pen(color, 2))
            { pen.DashStyle = dash; g.DrawRectangle(pen, d.X + r.X * d.Width / Image.Width, d.Y + r.Y * d.Height / Image.Height, r.Width * d.Width / Image.Width, r.Height * d.Height / Image.Height); }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (Image == null) return; Focus();
            if (e.Button == MouseButtons.Right) { panning = true; last = e.Location; Capture = true; }
            else if (e.Button == MouseButtons.Left && AllowRegionSelection && Display().Contains(e.Location))
            { start = ImagePoint(e.Location); selecting = true; drag = Rectangle.Empty; Capture = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (panning) { pan.X += e.X - last.X; pan.Y += e.Y - last.Y; last = e.Location; Invalidate(); }
            if (selecting) { Point end = ImagePoint(e.Location); drag = Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)); Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); Capture = false; panning = false;
            if (selecting)
            {
                selecting = false;
                if (drag.Width >= 16 && drag.Height >= 16) { Area = drag; MeasuredArea = Rectangle.Empty; if (AreaChanged != null) AreaChanged(); }
                Invalidate();
            }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e); if (Image == null) return;
            RectangleF old = Display(); float fx = (e.X - old.X) / old.Width, fy = (e.Y - old.Y) / old.Height;
            zoom = Math.Max(1, Math.Min(16, zoom * (e.Delta > 0 ? 1.2f : 1 / 1.2f)));
            RectangleF current = Display(); pan.X += e.X - current.X - fx * current.Width; pan.Y += e.Y - current.Y - fy * current.Height; Invalidate();
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { if (e.Button == MouseButtons.Middle) Fit(); base.OnMouseDoubleClick(e); }
    }

    public sealed class ResultPlot : Control
    {
        public AnalysisReport Report;
        public ResultPlot() { DoubleBuffered = true; BackColor = Color.White; Dock = DockStyle.Fill; MinimumSize = new Size(240, 130); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Report == null) { TextRenderer.DrawText(g, "The chart will appear after analysis", Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            bool compare = Report.Reference != null; double[] values = compare ? Report.Metrics.Select(m => m.AdjustedAmplitudePercent ?? double.NaN).ToArray() : Report.Working.BlurEnergy.Select(v => Report.Working.BlurEnergy[0] > 0 ? v / Report.Working.BlurEnergy[0] * 100 : 0).ToArray();
            if (values.All(double.IsNaN) || (!compare && Report.Working.Raw.EOG <= 1e-10)) { TextRenderer.DrawText(g, "Insufficient data for the chart", Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            string[] labels = compare ? new[] { "EOG", "TEN", "SML", "LAPV", "BREN", "EOL" } : new[] { "0", "0.7", "1.4", "2.8" };
            double max = Math.Max(110, values.Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).Max() * 1.13);
            Rectangle plot = new Rectangle(43, 31, Math.Max(80, Width - 55), Math.Max(45, Height - 86));
            TextRenderer.DrawText(g, compare ? (Report.ReliableComparison ? "Detail relative to the reference, %" : "Provisional metric ratios, %") : "EOG after additional blur, %", Font, new Rectangle(0, 4, Width, 22), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            using (Pen grid = new Pen(Color.Gainsboro)) for (int k = 0; k <= 4; k++)
            {
                int y = plot.Bottom - k * plot.Height / 4; g.DrawLine(grid, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, (max * k / 4).ToString("0"), Font, new Rectangle(0, y - 8, 39, 18), Color.DimGray, TextFormatFlags.Right);
            }
            using (Pen refPen = new Pen(Color.Black)) { refPen.DashStyle = DashStyle.Dash; float y = (float)(plot.Bottom - 100 / max * plot.Height); g.DrawLine(refPen, plot.Left, y, plot.Right, y); }
            float cell = (float)plot.Width / values.Length;
            for (int k = 0; k < values.Length; k++)
            {
                float x = plot.Left + cell * k + cell * .2f;
                if (!double.IsNaN(values[k]))
                {
                    float h = (float)(values[k] / max * plot.Height);
                    using (Brush brush = new SolidBrush(compare && (!Report.ReliableComparison || !Report.Metrics[k].UsedForSummary) ? Color.Gray : Color.SteelBlue)) g.FillRectangle(brush, x, plot.Bottom - h, cell * .6f, h);
                    if (Height >= 170) TextRenderer.DrawText(g, values[k].ToString("0"), Font, new Rectangle((int)(x - cell * .2f), (int)(plot.Bottom - h - 18), (int)cell, 18), Color.Black, TextFormatFlags.HorizontalCenter);
                }
                TextRenderer.DrawText(g, labels[k], Font, new Rectangle((int)(plot.Left + cell * k), plot.Bottom + 4, (int)cell, 20), Color.Black, TextFormatFlags.HorizontalCenter);
            }
            string note = compare ? "Dashed: reference 100%; gray: diagnostics" : "Gaussian σ, px · Not a physical defocus measurement";
            TextRenderer.DrawText(g, note, Font, new Rectangle(0, Height - 24, Width, 23), Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    public sealed class PhotoForm : Form
    {
        readonly bool compare;
        Photo reference, working;
        readonly PhotoCanvas left = new PhotoCanvas(), right = new PhotoCanvas();
        readonly GroupBox leftGroup = new GroupBox(), rightGroup = new GroupBox();
        readonly FlowLayoutPanel toolbar = new FlowLayoutPanel(), options = new FlowLayoutPanel();
        readonly Button analyze = new Button(), export = new Button(), cancel = new Button();
        readonly CheckBox align = new CheckBox(), normalize = new CheckBox(), map = new CheckBox();
        readonly NumericUpDown radius = new NumericUpDown(), tolerance = new NumericUpDown();
        readonly Label result = new Label(), diagnostics = new Label(), localStatus = new Label(), status = new Label();
        readonly DataGridView grid = new DataGridView();
        readonly DataGridView localGrid = new DataGridView();
        readonly TextBox explanation = new TextBox();
        readonly ResultPlot plot = new ResultPlot();
        readonly Timer auto = new Timer();
        readonly BackgroundWorker worker = new BackgroundWorker();
        readonly AnalysisSession session = new AnalysisSession();
        CancellationTokenSource cancellation;
        readonly string smokeFolder;
        AnalysisReport report;
        bool busy, closing;
        public PhotoForm(bool comparison, string smoke)
        {
            compare = comparison; smokeFolder = smoke;
            Text = ProductInfo.DisplayName + (compare ? " — Reference comparison" : " — Image sharpness");
            Font = new Font("Segoe UI", 9.5f); AutoScaleMode = AutoScaleMode.Dpi; Size = new Size(1320, 950); MinimumSize = new Size(1000, 760); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(246, 247, 249); KeyPreview = true;
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 52)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 48)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            toolbar.Dock = DockStyle.Fill; toolbar.AutoSize = true; toolbar.WrapContents = true; toolbar.Padding = new Padding(0, 0, 0, 4);
            if (compare) AddButton(toolbar, "1. Open reference…", delegate { OpenDialog(true); });
            AddButton(toolbar, compare ? "2. Open test image…" : "Open image…", delegate { OpenDialog(false); });
            analyze.Text = compare ? "Compare (F5)" : "Analyze (F5)"; StyleButton(analyze); analyze.Click += delegate { RunAnalysis(); }; toolbar.Controls.Add(analyze);
            export.Text = "Save result…"; StyleButton(export); export.Enabled = false; export.Click += delegate { Save(); }; toolbar.Controls.Add(export);
            AddButton(toolbar, "Batch images…", delegate { OpenBatch(); });
            cancel.Text = "Cancel"; StyleButton(cancel); cancel.Enabled = false; cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); }; toolbar.Controls.Add(cancel);
            AddButton(toolbar, "Help", delegate { HelpText(); }); root.Controls.Add(toolbar, 0, 0);
            Label hint = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 7), Text = compare ?
                "Select the same detail in the reference. Alignment compensates translation; object scale, rotation and processing settings must match." :
                "Drag to select a target or textured object. The index measures fine detail; one image cannot prove optimal focus." };
            root.Controls.Add(hint, 0, 1);
            options.AutoSize = true; options.Dock = DockStyle.Fill; options.WrapContents = true;
            AddButton(options, "Full image", delegate { if (left.Image != null) { left.Area = new Rectangle(0, 0, left.Image.Width, left.Image.Height); left.Invalidate(); Queue(); } });
            AddButton(options, "Fit preview", delegate { left.Fit(); right.Fit(); });
            map.Text = "Spatial map"; map.AutoSize = true; map.Margin = new Padding(6, 8, 10, 0); map.CheckedChanged += delegate { left.ShowTiles = right.ShowTiles = map.Checked; left.Invalidate(); right.Invalidate(); }; options.Controls.Add(map);
            if (compare)
            {
                align.Text = "Align translation"; align.Checked = true; align.AutoSize = true; align.Margin = new Padding(12, 8, 6, 0); options.Controls.Add(align);
                AddOptionLabel("± px:"); radius.Minimum = 1; radius.Maximum = 256; radius.Value = 32; radius.Width = 57; radius.Margin = new Padding(0, 5, 10, 0); options.Controls.Add(radius);
                normalize.Text = "Contrast correction"; normalize.Checked = false; normalize.AutoSize = true; normalize.Margin = new Padding(4, 8, 10, 0); options.Controls.Add(normalize);
                AddOptionLabel("Close to reference ±%:"); tolerance.Minimum = 0; tolerance.Maximum = 50; tolerance.Value = 5; tolerance.Width = 51; tolerance.Margin = new Padding(0, 5, 0, 0); options.Controls.Add(tolerance);
                align.CheckedChanged += delegate { Queue(); }; normalize.CheckedChanged += delegate { Queue(); }; radius.ValueChanged += delegate { Queue(); }; tolerance.ValueChanged += delegate { Queue(); };
            }
            root.Controls.Add(options, 0, 2);
            TableLayoutPanel previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = compare ? 2 : 1, RowCount = 1, Margin = new Padding(0, 5, 0, 0) };
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, compare ? 50 : 100)); if (compare) previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); previews.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.AllowRegionSelection = true; leftGroup.Text = compare ? "Reference · select the region here" : "Image · measurement region"; leftGroup.Dock = DockStyle.Fill; leftGroup.Controls.Add(left); previews.Controls.Add(leftGroup, 0, 0);
            if (compare) { rightGroup.Text = "Test image · aligned region"; rightGroup.Dock = DockStyle.Fill; rightGroup.Controls.Add(right); previews.Controls.Add(rightGroup, 1, 0); }
            left.AreaChanged += Queue; left.FileDropped += delegate(string file) { LoadPhoto(file, compare); };
            right.FileDropped += delegate(string file) { LoadPhoto(file, false); }; root.Controls.Add(previews, 0, 3);
            TableLayoutPanel verdict = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(0, 4, 0, 1) };
            verdict.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); verdict.RowStyles.Add(new RowStyle(SizeType.Percent, 28)); verdict.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
            result.Font = new Font(Font.FontFamily, 15, FontStyle.Bold); result.ForeColor = Color.FromArgb(25, 72, 112); result.Dock = DockStyle.Fill; result.TextAlign = ContentAlignment.MiddleLeft; result.Text = "Open " + (compare ? "both images" : "an image");
            diagnostics.Dock = DockStyle.Fill; diagnostics.TextAlign = ContentAlignment.MiddleLeft; diagnostics.AutoEllipsis = true; verdict.Controls.Add(result, 0, 0); verdict.Controls.Add(diagnostics, 0, 1); root.Controls.Add(verdict, 0, 4);
            localStatus.Dock = DockStyle.Fill; localStatus.TextAlign = ContentAlignment.MiddleLeft; localStatus.AutoEllipsis = true; verdict.Controls.Add(localStatus, 0, 2);
            TabControl tabs = new TabControl { Dock = DockStyle.Fill }; TabPage metricsTab = new TabPage("Metrics and chart"), notesTab = new TabPage("Interpretation and warnings"), localTab = new TabPage("Spatial tiles");
            TableLayoutPanel lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68)); lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false; grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            string[] columns = compare ? new[] { "Metric", "Reference raw", "Test raw", "Raw, %", "σ0.7 / C, %", "In summary" } : new[] { "Metric", "Original", "After σ0.7", "Definition" };
            foreach (string title in columns) { int k = grid.Columns.Add(title, title); grid.Columns[k].SortMode = DataGridViewColumnSortMode.NotSortable; }
            if (!compare) grid.Columns[3].FillWeight = 210;
            lower.Controls.Add(grid, 0, 0); lower.Controls.Add(plot, 1, 0); metricsTab.Controls.Add(lower);
            explanation.Multiline = true; explanation.ReadOnly = true; explanation.Dock = DockStyle.Fill; explanation.ScrollBars = ScrollBars.Vertical; explanation.BackColor = Color.White; explanation.BorderStyle = BorderStyle.None; explanation.Font = new Font("Segoe UI", 10);
            explanation.Text = "PNG / JPEG / BMP / TIFF (8 bits per channel). Color photographs are converted to luminance. A grayscale palette is preferable for thermal images.\r\n\r\nLeft drag: select a region. Wheel: preview zoom. Right drag: pan. Minimum region: 16×16 px. Measurements always use original pixels.\r\n\r\nNo single-image metric can distinguish every case of defocus from noise, motion blur, weak texture or digital processing.";
            localGrid.Dock = DockStyle.Fill; localGrid.ReadOnly = true; localGrid.AllowUserToAddRows = localGrid.AllowUserToDeleteRows = false; localGrid.RowHeadersVisible = false;
            localGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; localGrid.BackgroundColor = Color.White; localGrid.BorderStyle = BorderStyle.None;
            string[] localColumns = compare ? new[] { "Tile", "Region within ROI, px", "Reference index", "Test index", "Reference level, %", "Notes" } : new[] { "Tile", "Region within ROI, px", "Detail index", "Contrast", "Notes" };
            foreach (string title in localColumns) localGrid.Columns.Add(title, title);
            localTab.Controls.Add(localGrid); notesTab.Controls.Add(explanation); tabs.TabPages.Add(metricsTab); tabs.TabPages.Add(localTab); tabs.TabPages.Add(notesTab); root.Controls.Add(tabs, 0, 5);
            Label guide = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 3), Text = "Left drag: region · Wheel: preview zoom · Right drag: pan · Source files are not modified" }; root.Controls.Add(guide, 0, 6);
            status.AutoSize = true; status.Dock = DockStyle.Fill; status.ForeColor = Color.DimGray; status.Text = compare ? "100% means the level of the selected reference. Values above 100% are allowed." : "There is no universal in-focus threshold without calibration."; root.Controls.Add(status, 0, 7);
            auto.Interval = 250; auto.Tick += delegate { auto.Stop(); RunAnalysis(); };
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                object[] args = (object[])e.Argument;
                e.Result = compare ? session.Compare((Photo)args[0], (Photo)args[1], (Rectangle)args[2], (bool)args[3], (int)args[4], (bool)args[5], (double)args[6], (CancellationToken)args[7]) : session.Single((Photo)args[1], (Rectangle)args[2], (CancellationToken)args[7]);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
                if (closing) { busy = false; Close(); return; }
                SetBusy(false);
                if (e.Error is OperationCanceledException) { report = null; result.Text = "Analysis canceled"; diagnostics.Text = "You can change the region or open another image."; status.Text = "Canceled"; }
                else if (e.Error != null) { report = null; result.Text = "Analysis failed"; explanation.Text = e.Error.Message; status.Text = e.Error.Message; }
                else { report = (AnalysisReport)e.Result; ShowReport(); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                auto.Stop(); if (busy) { e.Cancel = true; closing = true; if (cancellation != null) cancellation.Cancel(); Hide(); } else ReleasePhotos();
            };
            FormClosed += delegate { auto.Dispose(); worker.Dispose(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && busy) { if (cancellation != null) cancellation.Cancel(); e.SuppressKeyPress = true; return; }
                if (busy) return;
                if (e.Control && e.KeyCode == Keys.O) { OpenDialog(compare && e.Shift); e.SuppressKeyPress = true; }
                if (e.Control && e.KeyCode == Keys.S) { Save(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.F5) { RunAnalysis(); e.SuppressKeyPress = true; }
            };
            if (smokeFolder != null)
            {
                ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-30000, -30000);
                if (Environment.GetEnvironmentVariable("PHOTOFOCUS_TEST_MIN_SIZE") == "1") Size = new Size(1000, 760);
                Shown += delegate { BeginSmoke(); };
            }
        }
        void AddOptionLabel(string text) { options.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(3, 9, 3, 0) }); }
        static void StyleButton(Button b) { b.AutoSize = true; b.Height = 33; b.Padding = new Padding(8, 3, 8, 3); b.Margin = new Padding(0, 0, 7, 4); }
        static void AddButton(FlowLayoutPanel panel, string title, Action action) { Button b = new Button { Text = title }; StyleButton(b); b.Click += delegate { action(); }; panel.Controls.Add(b); }
        void OpenDialog(bool isReference)
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = isReference ? "Reference image" : compare ? "Test image" : "Image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) LoadPhoto(d.FileName, isReference);
        }
        public void LoadPhoto(string file, bool isReference)
        {
            if (busy) return;
            try
            {
                Photo photo = Photo.Open(file);
                if (isReference) { Photo old = reference; reference = photo; left.SetImage(photo.Bitmap); leftGroup.Text = "Reference: " + Path.GetFileName(file); if (old != null) old.Dispose(); }
                else { Photo old = working; working = photo; (compare ? right : left).SetImage(photo.Bitmap); (compare ? rightGroup : leftGroup).Text = (compare ? "Test: " : "Image: ") + Path.GetFileName(file); if (old != null) old.Dispose(); }
                Queue();
            }
            catch (Exception ex) { status.Text = ex.Message; if (smokeFolder == null) MessageBox.Show(this, ex.Message, "Could not open the image", MessageBoxButtons.OK, MessageBoxIcon.Information); else throw; }
        }
        void InvalidateReport()
        {
            report = null; export.Enabled = false; grid.Rows.Clear(); localGrid.Rows.Clear(); left.Tiles.Clear(); right.Tiles.Clear(); plot.Report = null; plot.Invalidate();
            right.MeasuredArea = left.MeasuredArea = Rectangle.Empty; if (compare) right.Area = Rectangle.Empty; left.Invalidate(); right.Invalidate();
            result.Text = "New analysis required"; diagnostics.Text = localStatus.Text = "";
        }
        void Queue()
        { if (busy) return; InvalidateReport(); auto.Stop(); if (working != null && (!compare || reference != null)) auto.Start(); }
        void SetBusy(bool value)
        {
            busy = value; options.Enabled = left.Enabled = right.Enabled = !value; UseWaitCursor = value;
            foreach (Control control in toolbar.Controls) control.Enabled = !value; cancel.Enabled = value;
            export.Enabled = !value && report != null;
            if (value) { result.Text = "Analyzing…"; diagnostics.Text = "Processing original pixels. You can cancel this operation."; }
        }
        void RunAnalysis()
        {
            auto.Stop(); if (busy || working == null || (compare && reference == null)) return;
            Rectangle area = left.Area;
            InvalidateReport(); SetBusy(true);
            cancellation = new CancellationTokenSource();
            worker.RunWorkerAsync(new object[] { reference, working, area, align.Checked, (int)radius.Value, normalize.Checked, (double)tolerance.Value, cancellation.Token });
        }
        void ShowReport()
        {
            ImageMeasures m = report.Working;
            result.Text = compare ? (report.RelativeDetailPercent.HasValue ? report.RelativeDetailPercent.Value.ToString("0.0") + "% of reference · " + report.Status : report.Status) : (m.HasStructure ? "Detail index: " + m.DetailIndex.ToString("0.00") : report.Status);
            diagnostics.Text = "Region " + m.Width + "×" + m.Height + " px · Contrast " + ReportFiles.F(m.ContrastP95P05) + " · Noise ≈ " + ReportFiles.F(m.NoiseSigmaEstimate) + " · Clipping " + ReportFiles.F(m.ClippedPercent) + "%";
            if (compare) diagnostics.Text += " · dx=" + report.Alignment.Dx + ", dy=" + report.Alignment.Dy + " px · NCC=" + report.Alignment.Correlation.ToString("0.00");
            localStatus.Text = compare && report.LocalMedianPercent.HasValue ? "Tiles: median " + report.LocalMedianPercent.Value.ToString("0.0") + "% · P25–P75: " + report.LocalP25Percent.Value.ToString("0.0") + "–" + report.LocalP75Percent.Value.ToString("0.0") + "% · Below tolerance: " + report.LowerDetailTiles + "/" + report.InformativeTiles + (report.ReliableComparison ? "" : " · Provisional: comparison conditions are unreliable") : "The spatial map shows local detail, not a percentage of optical focus.";
            grid.Rows.Clear();
            if (compare)
            {
                grid.Columns[4].HeaderText = normalize.Checked ? "σ0.7 / C, %" : "σ0.7, %";
                foreach (MetricComparison v in report.Metrics)
                {
                    int k = grid.Rows.Add(v.Metric, ReportFiles.F(v.ReferenceRaw), ReportFiles.F(v.WorkingRaw), ReportFiles.Nullable(v.RawAmplitudePercent), ReportFiles.Nullable(v.AdjustedAmplitudePercent), v.UsedForSummary ? "yes" : "diag.");
                    grid.Rows[k].Cells[0].ToolTipText = FocusMath.Descriptions[k] + " " + v.Unit;
                    grid.Rows[k].Cells[4].ToolTipText = "After the same σ=0.7 filter: reference=" + ReportFiles.F(v.ReferenceFiltered) + "; test=" + ReportFiles.F(v.WorkingFiltered);
                }
                left.MeasuredArea = report.Alignment.ReferenceArea; right.Area = report.Alignment.WorkingArea; left.Invalidate(); right.Invalidate();
            }
            else
            {
                double[] raw = m.Raw.Array(), filtered = m.Filtered.Array();
                for (int k = 0; k < raw.Length; k++) grid.Rows.Add(FocusMath.Names[k], ReportFiles.F(raw[k]), ReportFiles.F(filtered[k]), FocusMath.Descriptions[k]);
            }
            localGrid.Rows.Clear(); left.Tiles.Clear(); right.Tiles.Clear();
            for (int k = 0; k < m.Tiles.Count; k++)
            {
                TileMeasure wt = m.Tiles[k], rt = compare ? report.Reference.Tiles[k] : null;
                TileComparison tc = compare ? report.Tiles[k] : null;
                Rectangle leftArea = wt.Area; leftArea.Offset(compare ? report.Alignment.ReferenceArea.Location : report.RequestedArea.Location);
                Rectangle rightArea = wt.Area; if (compare) rightArea.Offset(report.Alignment.WorkingArea.Location);
                left.Tiles.Add(new TileOverlay { Area = leftArea, Value = compare ? 100 : wt.DetailIndex, Valid = compare ? rt.Informative : wt.Informative, Relative = compare });
                if (compare) right.Tiles.Add(new TileOverlay { Area = rightArea, Value = tc.RelativePercent ?? 0, Valid = tc.Informative && report.ReliableComparison, Relative = true });
                string tileArea = wt.Area.X + "," + wt.Area.Y + " · " + wt.Area.Width + "×" + wt.Area.Height;
                if (compare) localGrid.Rows.Add(k + 1, tileArea, rt.Informative ? rt.DetailIndex.ToString("0.00") : "—", wt.Informative ? wt.DetailIndex.ToString("0.00") : "—", tc.RelativePercent.HasValue ? tc.RelativePercent.Value.ToString("0.0") : "—", tc.Informative ? (report.ReliableComparison ? "" : "unreliable comparison conditions") : "insufficient reference detail");
                else localGrid.Rows.Add(k + 1, tileArea, wt.Informative ? wt.DetailIndex.ToString("0.00") : "—", wt.Contrast.ToString("0.0"), wt.Informative ? "local index" : "insufficient detail / noise");
            }
            localGrid.ClearSelection(); left.Invalidate(); right.Invalidate();
            if (grid.Rows.Count > 0) grid.ClearSelection();
            explanation.Text = ReportFiles.Text(report).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            plot.Report = report; plot.Invalidate(); export.Enabled = true;
            status.Text = "Analysis: " + report.AnalysisMilliseconds.ToString("0.0") + " ms" + (report.CachedReference || report.CachedWorking ? " · cached measurements" : "") + " · " + (report.Warnings.Count == 0 ? "See the interpretation tab for details." : "Warnings: " + report.Warnings.Count + ". Open the interpretation tab.");
        }
        void Save()
        {
            if (report == null || busy) return;
            using (SaveFileDialog d = new SaveFileDialog { FileName = compare ? "photo-comparison" : "photo-sharpness", DefaultExt = "html", Filter = "HTML report with chart and map|*.html|Full JSON result|*.json|Metric table CSV|*.csv|Text report|*.txt" })
                if (d.ShowDialog(this) == DialogResult.OK) try { ReportFiles.Save(d.FileName, report); status.Text = "Saved: " + d.FileName; } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        }
        void OpenBatch()
        {
            if (busy) return;
            if (compare && reference == null) { MessageBox.Show(this, "Open a reference image and select a region first."); return; }
            auto.Stop();
            using (var form = new BatchForm(compare ? reference : null, left.Image == null ? (Rectangle?)null : left.Area, align.Checked, (int)radius.Value, normalize.Checked, (double)tolerance.Value)) form.ShowDialog(this);
        }
        void HelpText()
        {
            string instructions = compare ?
                "1. Open a reference image, then a test image.\n2. Drag to select a target or object in the reference.\n3. Inspect the metric ratios, spatial map and warnings.\n4. Save an HTML, JSON, CSV or text report.\n\n100% is the level of your selected reference, even if that image is blurred. Values above 100% are allowed. The default ±5% tolerance is a UI setting, not a production acceptance limit.\n\nUse the same scene, object scale, orientation and processing settings. Alignment compensates translation only. Contrast correction is optional and off by default.\n\nBatch images uses the current reference and region. The README.md in this folder contains complete instructions.\n\nCtrl+O: test image · Ctrl+Shift+O: reference · F5: compare · Ctrl+S: save · Esc: cancel." :
                "1. Open or drop an image.\n2. Drag to select a target or textured object.\n3. Inspect the detail index, metrics, spatial map and warnings.\n4. Save an HTML, JSON, CSV or text report.\n\nThe detail index is not a focus percentage and has no fixed upper limit. A single image cannot prove that the lens is at optimal focus.\n\nUse Batch images to process multiple files with the current region. Without a selected image, the batch analyzes each whole image.\n\nPNG/JPEG/BMP/TIFF, 8 bits per channel. Thermal images should use a grayscale palette. Minimum region: 16×16 pixels. Measurements always use original pixels.\n\nCtrl+O: open · F5: analyze · Ctrl+S: save · Esc: cancel. See README.md in this folder for complete instructions.";
            MessageBox.Show(this, instructions, ProductInfo.Name + " — Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        void ReleasePhotos()
        { session.Clear(); left.Image = right.Image = null; if (reference != null) { reference.Dispose(); reference = null; } if (working != null) { working.Dispose(); working = null; } }
        void BeginSmoke()
        {
            Directory.CreateDirectory(smokeFolder);
            map.Checked = true;
            if (compare) LoadPhoto(Path.Combine(smokeFolder, "reference.png"), true);
            LoadPhoto(Path.Combine(smokeFolder, "working.png"), false);
            Timer poll = new Timer { Interval = 200 }; int attempts = 0;
            poll.Tick += delegate
            {
                if (report != null)
                {
                    poll.Stop(); ReportFiles.Save(Path.Combine(smokeFolder, compare ? "compare-ui.json" : "single-ui.json"), report);
                    using (Bitmap bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(smokeFolder, compare ? "compare-ui.png" : "single-ui.png")); }
                    poll.Dispose(); Close();
                }
                else if (++attempts > 300 || (!busy && !auto.Enabled)) { poll.Stop(); File.WriteAllText(Path.Combine(smokeFolder, "ui-error.txt"), explanation.Text); Environment.ExitCode = 2; poll.Dispose(); Close(); }
            };
            poll.Start();
        }
    }
}
