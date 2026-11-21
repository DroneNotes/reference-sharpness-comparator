using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace PhotoFocus
{
    public static class ReportHtml
    {
        static string H(string value) { return WebUtility.HtmlEncode(value ?? ""); }
        static string N(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
        static string P(double? value) { return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "—"; }
        public static string Render(AnalysisReport r)
        {
            bool compare = r.Reference != null; var b = new StringBuilder();
            b.Append("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Analysis results</title><style>body{max-width:1050px;margin:35px auto;padding:0 22px;font:16px/1.45 'Segoe UI',sans-serif;color:#18212a;background:white}h1{font-size:25px}h2{font-size:20px;margin-top:30px}.result{font-size:23px;font-weight:600}.muted,small{color:#555}table{border-collapse:collapse;width:100%;font-size:14px}th,td{padding:7px;border-bottom:1px solid #ccc;text-align:right}th:first-child,td:first-child{text-align:left}th{background:#f2f3f4}svg{width:100%;height:auto}.figure{max-width:720px;margin:15px auto}.tiles{display:grid;gap:4px}.tile{padding:13px 5px;text-align:center;min-width:0;font-size:15px;border:1px solid #bbb}.warning{border-left:3px solid #ad6a16;padding-left:12px}.wrap{overflow-x:auto}.hash{overflow-wrap:anywhere;font-family:monospace;font-size:12px}@media print{body{margin:0;font-size:11pt}.figure,table{break-inside:avoid}}@media(max-width:600px){body{padding:0 12px}.tile{padding:8px 2px;font-size:12px}}</style><body>");
            b.Replace("<title>Analysis results</title>", "<title>" + H(r.ToolVersion) + " — analysis report</title>");
            b.Append("<h1>" + (compare ? "Reference sharpness comparison" : "Single-image sharpness") + "</h1><p class=muted>" + H(r.ToolVersion) + " · " + H(r.CreatedLocal.ToString("yyyy-MM-dd HH:mm")) + "</p>");
            b.Append("<p class=result>" + (compare ? (r.RelativeDetailPercent.HasValue ? P(r.RelativeDetailPercent) + "% of reference · " : "") + H(r.Status) : r.Working.HasStructure ? "Detail index: " + P(r.Working.DetailIndex) : H(r.Status)) + "</p>");
            b.Append((compare ? "<p>Test image: <strong>" : "<p>Image: <strong>") + H(Path.GetFileName(r.WorkingFile)) + "</strong></p>");
            if (compare) b.Append("<p>Reference: <strong>" + H(Path.GetFileName(r.ReferenceFile)) + "</strong></p>");
            b.Append("<p>Selected region: x=" + r.RequestedArea.X + ", y=" + r.RequestedArea.Y + ", " + r.RequestedArea.Width + "×" + r.RequestedArea.Height + " px. Measured: " + r.Working.Width + "×" + r.Working.Height + " px." + (compare ? " Contrast correction: " + (r.ContrastAdjusted ? "enabled" : "disabled") + "." : "") + "</p>");
            if (compare)
            {
                AlignmentResult a = r.Alignment;
                b.Append("<p>UI tolerance for close-to-reference results: ±" + N(r.TolerancePercent) + "%. Range across the four summary metrics: " + P(r.MetricMinPercent) + "–" + P(r.MetricMaxPercent) + "%. This is not a confidence interval or a production acceptance limit.</p>");
                b.Append("<p>Test-image shift: dx=" + a.Dx + ", dy=" + a.Dy + " px; context NCC=" + N(a.Correlation) + ", ROI NCC=" + N(a.RoiCorrelation) + ". Fractional estimate (diagnostic): " + N(a.FractionalDx) + ", " + N(a.FractionalDy) + " px. Local-shift spread: " + N(a.ResidualShiftSpread) + " px.</p>");
            }
            b.Append("<p>" + H(r.Interpretation) + "</p>");
            if (r.Warnings.Count > 0) { b.Append("<div class=warning><strong>Warnings</strong><ul>"); foreach (string warning in r.Warnings) b.Append("<li>" + H(warning) + "</li>"); b.Append("</ul></div>"); }
            b.Append("<h2>Metrics</h2><div class=wrap><table><thead><tr><th>Metric</th>" + (compare ? "<th>Reference raw</th><th>Test raw</th><th>Raw, %</th><th>σ0.7" + (r.ContrastAdjusted ? "/C" : "") + ", %</th><th>In summary</th>" : "<th>Original</th><th>After σ0.7</th><th>Definition</th>") + "</tr></thead><tbody>");
            if (compare) foreach (MetricComparison m in r.Metrics) b.Append("<tr><td>" + H(m.Metric) + "</td><td>" + N(m.ReferenceRaw) + "</td><td>" + N(m.WorkingRaw) + "</td><td>" + P(m.RawAmplitudePercent) + "</td><td>" + P(m.AdjustedAmplitudePercent) + "</td><td>" + (m.UsedForSummary ? "yes" : "diagnostic") + "</td></tr>");
            else
            {
                double[] raw = r.Working.Raw.Array(), filtered = r.Working.Filtered.Array();
                for (int k = 0; k < raw.Length; k++) b.Append("<tr><td>" + H(FocusMath.Names[k]) + "</td><td>" + N(raw[k]) + "</td><td>" + N(filtered[k]) + "</td><td>" + H(FocusMath.Descriptions[k]) + "</td></tr>");
            }
            b.Append("</tbody></table></div><div class=figure>" + Plot(r) + "</div>");
            ImageMeasures mwork = r.Working;
            b.Append("<h2>Input diagnostics</h2><p>P95−P05 contrast: " + N(mwork.ContrastP95P05) + "; mean luminance: " + N(mwork.Mean) + "; noise ≈ " + N(mwork.NoiseSigmaEstimate) + "; clipped values: " + N(mwork.ClippedPercent) + "%. Approximate energy fraction explainable by white noise: " + P(100 * mwork.NoiseEnergyFractionEstimate) + "%. This is a diagnostic model, not a sensor-noise measurement.</p>");
            b.Append("<h2>Spatial tiles</h2>");
            if (compare) b.Append("<p>Local median: " + P(r.LocalMedianPercent) + "%; P25–P75: " + P(r.LocalP25Percent) + "–" + P(r.LocalP75Percent) + "%. Below the selected tolerance: " + r.LowerDetailTiles + "/" + r.InformativeTiles + ". This is variation across tiles, not a confidence interval. " + (r.ReliableComparison ? "" : "Reliable-comparison conditions are not met; local ratios are provisional.") + "</p>");
            else b.Append("<p>Local detail index. Colors show differences within the selected region, not an absolute focus verdict.</p>");
            int columns = Math.Min(4, Math.Max(1, mwork.Width / 32)); double max = Math.Max(.01, mwork.Tiles.Where(t => t.Informative).Select(t => t.DetailIndex).DefaultIfEmpty(1).Max());
            b.Append("<div class=tiles style='grid-template-columns:repeat(" + columns + ",minmax(0,1fr))'>");
            for (int k = 0; k < mwork.Tiles.Count; k++)
            {
                bool valid = compare ? r.Tiles[k].Informative && r.ReliableComparison : mwork.Tiles[k].Informative;
                double value = compare ? r.Tiles[k].RelativePercent ?? 0 : mwork.Tiles[k].DetailIndex;
                double v = Math.Max(0, Math.Min(1, compare ? value / 110 : value / max));
                string color = valid ? "rgb(" + (int)(245 - 50 * v) + "," + (int)(210 + 20 * v) + "," + (int)(190 + 60 * v) + ")" : "#eee";
                b.Append("<div class=tile style='background:" + color + "'><small>Tile " + (k + 1) + "</small><br><strong>" + (valid ? P(value) + (compare ? "%" : "") : "—") + "</strong></div>");
            }
            b.Append("</div><h2>Reproducibility</h2><p>Luminance: 0.299R + 0.587G + 0.114B; range 0–255. Metric filter: Gaussian σ=0.7 px. All six operators are averaged over valid positions in the original pixel grid. Percentages above 100 are not capped.</p>");
            b.Append((compare ? "<p class=hash>Test-file SHA-256: " : "<p class=hash>Image SHA-256: ") + H(r.WorkingSha256 ?? "not available") + (compare ? "<br>Reference SHA-256: " + H(r.ReferenceSha256 ?? "not available") : "") + "</p><p class=muted>Analysis time: " + N(r.AnalysisMilliseconds) + " ms; excludes file reading and rendering. " + (compare ? "Cache: reference=" + r.CachedReference + ", test=" + r.CachedWorking + ", alignment=" + r.CachedAlignment : "Cached measurements: " + r.CachedWorking) + ".</p></body></html>");
            return b.ToString();
        }
        static string Plot(AnalysisReport r)
        {
            bool comparison = r.Reference != null;
            double[] values = comparison ? r.Metrics.Select(m => m.AdjustedAmplitudePercent ?? double.NaN).ToArray() : r.Working.BlurEnergy.Select(e => r.Working.BlurEnergy[0] > 0 ? e * 100 / r.Working.BlurEnergy[0] : double.NaN).ToArray();
            string[] labels = comparison ? new[] { "EOG", "TEN", "SML", "LAPV", "BREN", "EOL" } : new[] { "σ=0", "σ=0.7", "σ=1.4", "σ=2.8" };
            double max = Math.Max(110, values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max() * 1.15); var b = new StringBuilder("<svg viewBox='0 0 720 260' role=img aria-label='Metric chart'><rect width='720' height='260' fill='white'/>");
            for (int i = 0; i < 5; i++) { double y = 220 - i * 48; b.Append("<path d='M48 " + N(y) + "H700' stroke='#ddd'/><text x='40' y='" + N(y + 5) + "' text-anchor='end' font-size='13'>" + N(max * i / 4) + "</text>"); }
            b.Append("<path d='M48 " + N(220 - 100 / max * 192) + "H700' stroke='#555' stroke-dasharray='5 4'/>");
            double cell = 652.0 / values.Length;
            for (int k = 0; k < values.Length; k++)
            {
                double h = values[k] / max * 192, x = 48 + cell * k;
                if (!double.IsNaN(values[k])) b.Append("<rect x='" + N(x + cell * .2) + "' y='" + N(220 - h) + "' width='" + N(cell * .6) + "' height='" + N(h) + "' fill='" + (comparison && (!r.ReliableComparison || !r.Metrics[k].UsedForSummary) ? "#888" : "#3478a5") + "'/><text x='" + N(x + cell * .5) + "' y='" + N(214 - h) + "' text-anchor='middle' font-size='13'>" + P(values[k]) + "</text>");
                else b.Append("<text x='" + N(x + cell * .5) + "' y='210' text-anchor='middle'>—</text>");
                b.Append("<text x='" + N(x + cell * .5) + "' y='242' text-anchor='middle' font-size='13'>" + H(labels[k]) + "</text>");
            }
            return b.Append("</svg>").ToString();
        }
    }
}
