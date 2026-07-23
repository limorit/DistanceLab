using System.Text.RegularExpressions;

namespace OpenCVDistanceLab.Core;

public static partial class ImageSequence
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<string> Find(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder)
            .Where(IsSupported)
            .OrderBy(path => Path.GetFileName(path), NaturalPathComparer.Instance)
            .ToArray();
    }

    private sealed class NaturalPathComparer : IComparer<string>
    {
        internal static readonly NaturalPathComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            string[] left = NumberParts().Split(x ?? string.Empty);
            string[] right = NumberParts().Split(y ?? string.Empty);
            for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                int comparison = long.TryParse(left[index], out long a) &&
                                 long.TryParse(right[index], out long b)
                    ? a.CompareTo(b)
                    : StringComparer.CurrentCultureIgnoreCase.Compare(left[index], right[index]);
                if (comparison != 0) return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    [GeneratedRegex("(\\d+)")]
    private static partial Regex NumberParts();
}
