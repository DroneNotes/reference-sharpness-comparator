namespace PhotoFocus
{
    internal static class ProductInfo
    {
        public const string Name = "PhotoCompare";
        public const string Version = "2.1";
        public const string FileVersion = "2.1.0.0";
        public const string Description = "Reference-based image sharpness comparison";
        public const bool IsComparison = true;
        public static string DisplayName { get { return Name + " " + Version; } }
    }
}
