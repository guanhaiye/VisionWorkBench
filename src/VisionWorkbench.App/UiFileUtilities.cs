using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace VisionWorkbench.App;

internal static class UiFileUtilities
{
    public static List<string> DeleteFiles(IEnumerable<string> paths)
    {
        var failedFiles = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                failedFiles.Add(path);
            }
            catch (UnauthorizedAccessException)
            {
                failedFiles.Add(path);
            }
        }
        return failedFiles;
    }

    public static void ShowImage(Image image, string? path)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            image.Source = path is not null && File.Exists(path)
                ? new BitmapImage(new Uri(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or UriFormatException)
        {
            image.Source = null;
        }
    }
}

internal static class ByteSizeFormatter
{
    public static string Format(ulong bytes)
    {
        const double unit = 1024;
        return bytes switch
        {
            >= (ulong)(unit * unit * unit) => $"{bytes / (unit * unit * unit):0.0} GB",
            >= (ulong)(unit * unit) => $"{bytes / (unit * unit):0.0} MB",
            _ => $"{bytes / unit:0.0} KB",
        };
    }
}
