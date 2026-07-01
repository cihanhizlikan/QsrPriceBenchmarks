using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// One province (İl) on the Locations map: its vector outline (parsed, frozen
/// SVG geometry), the marker-dot position (province centroid, offset so the dot
/// is centred), and — when the selected chain has locations there — the total
/// count and per-district breakdown shown in the hover tooltip.
/// </summary>
public sealed class MapCellViewModel
{
    /// <summary>Side length of the (transparent) dot container, in SVG units.</summary>
    public const double DotBox = 30;

    public string ProvinceName { get; init; } = "";
    public Geometry? Shape { get; init; }

    /// <summary>Top-left of the dot container so its centre sits on the centroid.</summary>
    public double DotLeft { get; init; }
    public double DotTop  { get; init; }

    public int Total { get; init; }
    public bool HasLocations => Total > 0;

    /// <summary>Per-district lines for the tooltip, e.g. "Üsküdar (2)".</summary>
    public IReadOnlyList<string> Districts { get; init; } = Array.Empty<string>();

    /// <summary>Number of districts shown per tooltip column before wrapping.</summary>
    public const int DistrictsPerColumn = 10;

    /// <summary>
    /// <see cref="Districts"/> split into columns of <see cref="DistrictsPerColumn"/>
    /// so provinces with many districts (e.g. İstanbul) lay out across several
    /// columns instead of one very tall list. Gains one extra column per 10 rows.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> DistrictColumns
    {
        get
        {
            List<IReadOnlyList<string>> columns = new();
            for (int i = 0; i < Districts.Count; i += DistrictsPerColumn)
            {
                columns.Add(Districts.Skip(i).Take(DistrictsPerColumn).ToList());
            }
            return columns;
        }
    }
}
