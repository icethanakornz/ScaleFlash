using System.Text;
using System.Text.RegularExpressions;

namespace ScaleFlash.Core.Imaging;

/// <summary>Finds a bootloader version string ("U-Boot 2017.03", "GRUB 2.06") in the area before the first partition.</summary>
public static partial class BootloaderBanner
{
    [GeneratedRegex(@"U-Boot (SPL )?20\d\d\.\d\d[-\w.]*|GRUB (version )?\d+\.\d+")]
    private static partial Regex Banner();

    public static string? Find(ReadOnlySpan<byte> area)
    {
        // Latin-1 keeps a 1:1 byte/char mapping, so binary data cannot break the scan.
        string text = Encoding.Latin1.GetString(area);
        var m = Banner().Match(text);
        return m.Success ? m.Value : null;
    }
}
