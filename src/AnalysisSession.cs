using System.Drawing;
using System.Threading;

namespace PhotoFocus
{
    // Bounded per-window/per-batch cache; callers serialize access, so no static state is shared across threads.
    public sealed class AnalysisSession
    {
        Photo referencePhoto, workingPhoto, alignmentReference, alignmentWorking;
        Rectangle referenceArea, workingArea, alignmentArea;
        ImageMeasures referenceMeasures, workingMeasures;
        AlignmentResult alignment;
        bool alignmentEnabled; int alignmentRadius;
        public int MeasureComputations { get; private set; }
        public int AlignmentComputations { get; private set; }
        internal ImageMeasures Measure(Photo photo, Rectangle area, bool reference, CancellationToken token, out bool cached)
        {
            token.ThrowIfCancellationRequested();
            cached = reference ? ReferenceEquals(photo, referencePhoto) && area == referenceArea && referenceMeasures != null : ReferenceEquals(photo, workingPhoto) && area == workingArea && workingMeasures != null;
            if (cached) return reference ? referenceMeasures : workingMeasures;
            ImageMeasures value = FocusMath.Measure(photo.Gray.Crop(area, token), token); MeasureComputations++;
            if (reference) { referencePhoto = photo; referenceArea = area; referenceMeasures = value; }
            else { workingPhoto = photo; workingArea = area; workingMeasures = value; }
            return value;
        }
        internal AlignmentResult Align(Photo reference, Photo working, Rectangle area, bool enabled, int radius, CancellationToken token, out bool cached)
        {
            token.ThrowIfCancellationRequested();
            cached = ReferenceEquals(reference, alignmentReference) && ReferenceEquals(working, alignmentWorking) && area == alignmentArea && alignmentEnabled == enabled && alignmentRadius == radius && alignment != null;
            if (cached) return alignment;
            AlignmentResult value = Registration.Find(reference.Gray, working.Gray, area, enabled, radius, token); AlignmentComputations++;
            alignmentReference = reference; alignmentWorking = working; alignmentArea = area; alignmentEnabled = enabled; alignmentRadius = radius; alignment = value;
            return value;
        }
        public AnalysisReport Single(Photo photo, Rectangle area, CancellationToken token = default(CancellationToken))
        { return FocusMath.Single(photo, area, token, this); }
        public AnalysisReport Compare(Photo reference, Photo working, Rectangle area, bool align, int radius, bool normalize, double tolerance, CancellationToken token = default(CancellationToken))
        { return FocusMath.Compare(reference, working, area, align, radius, normalize, tolerance, token, this); }
        public void Clear()
        { referencePhoto = workingPhoto = alignmentReference = alignmentWorking = null; referenceMeasures = workingMeasures = null; alignment = null; }
    }
}
