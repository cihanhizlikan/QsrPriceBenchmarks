using System.Windows.Media;
using System.Windows.Media.Imaging;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// View-model for one chain in the sidebar selector. Holds the chain config
/// and a lazily-generated placeholder logo (colored square with initials)
/// shown when no LOGO_BLOB is stored in the database.
/// </summary>
public sealed class ChainViewModel(ChainConfig chain, byte[]? logoBytes) : ViewModelBase
{
    public ChainConfig Chain { get; } = chain;
    public string Name => Chain.Name;
    public string Slug => Chain.PlatformSlugs.Values.First();

    /// <summary>
    /// Initials to show in the placeholder badge (up to 2 chars from the
    /// chain name words, e.g. "BK" for "Burger King", "PY" for "Popeyes").
    /// </summary>
    public string Initials { get; } = BuildInitials(chain.Name);

    /// <summary>
    /// The badge background colour — derived deterministically from the
    /// chain name so each chain always gets the same colour.
    /// </summary>
    public Color BadgeColor { get; } = BuildBadgeColor(chain.Name);
    public SolidColorBrush BadgeBrush => new(BadgeColor);

    /// <summary>
    /// Decoded logo image, or null if no logo is stored (placeholder shown
    /// instead via the DataTrigger in the XAML template).
    /// </summary>
    public BitmapSource? LogoImage { get; } = DecodeLogo(logoBytes);

    private static string BuildInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 1
            ? name[..Math.Min(2, name.Length)].ToUpperInvariant()
            : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0, 77, 92),   // teal
        Color.FromRgb(49, 27, 146), // indigo
        Color.FromRgb(74, 20, 140), // purple
        Color.FromRgb(1, 87, 155),  // blue
        Color.FromRgb(0, 96, 100),  // dark-cyan
        Color.FromRgb(27, 94, 32),  // green
        Color.FromRgb(100, 21, 21), // red
    };

    /// <summary>
    /// Each chain's badge uses its real-life brand colour from the shared
    /// <see cref="BrandPalette"/> (single source of truth, also used by the Excel
    /// export header). Unknown chains fall back to the deterministic palette.
    /// </summary>
    private static Color BuildBadgeColor(string name)
    {
        if (BrandPalette.HasBrand(name))
        {
            return (Color)ColorConverter.ConvertFromString(BrandPalette.HexFor(name));
        }
        return FallbackColor(name);
    }

    private static Color FallbackColor(string name)
    {
        int hash = name.Aggregate(0, (h, c) => h * 31 + c);
        return Palette[Math.Abs(hash) % Palette.Length];
    }

    /// <summary>
    /// Badge text colour, chosen automatically for legibility: dark text on
    /// light badges (e.g. Burger King's amber), white on dark ones.
    /// </summary>
    public SolidColorBrush BadgeForeground { get; } = PickReadableText(BuildBadgeColor(chain.Name));

    private static readonly SolidColorBrush DarkText  = Frozen(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly SolidColorBrush LightText = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private static SolidColorBrush PickReadableText(Color bg)
    {
        // Perceived brightness (ITU-R BT.601); dark text once the badge is light.
        double y = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        return y > 150 ? DarkText : LightText;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static BitmapSource? DecodeLogo(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        using var ms = new System.IO.MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return (BitmapSource)bmp;   // BitmapImage : BitmapSource in WPF
    }
}
