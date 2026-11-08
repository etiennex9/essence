namespace Essence.UI.Services;

/// <summary>A station's marker, as <see cref="PillPlacement"/> places it.</summary>
/// <param name="PriceCents">Null when it has no price: it shows "—", and ranks last.</param>
/// <param name="Dimmed">Best near you or the trip doesn't list it: it fades, and ranks after the stations listed.</param>
/// <param name="Selected">Ringed on the map: always a pill, and larger.</param>
/// <param name="Pick">Best near you's or the trip's pick: always a pill, larger, under its flag.</param>
/// <param name="Second">The closest, or the trip's reference: a pill under its flag when there's room.</param>
/// <param name="Flag">The text of the flag above it, for the pick and the second.</param>
public sealed record PillCandidate(
    long Id,
    double Latitude,
    double Longitude,
    decimal? PriceCents,
    bool Favorite = false,
    bool Dimmed = false,
    bool Selected = false,
    bool Pick = false,
    bool Second = false,
    string? Flag = null);

/// <summary>
/// Something the map draws over the stations, which a price shouldn't hide under: it stands on its point, <paramref name="Top"/>
/// pixels tall and <paramref name="HalfWidth"/> on each side.
/// </summary>
public sealed record PillObstacle(double Latitude, double Longitude, double HalfWidth, double Top)
{
    /// <summary>The trip's destination, or where its range runs out: a label over a stem (<c>.ess-dest</c>, <c>.ess-range-end</c>).</summary>
    public static PillObstacle Label(GeoPoint point, string text) => new(
        point.Latitude, point.Longitude, (PillPlacement.LabelPaddingPx + PillPlacement.LabelCharPx * text.Length) / 2, PillPlacement.LabelHeightPx);

    /// <summary>The pin a press and hold dropped (<c>.ess-pin</c>).</summary>
    public static PillObstacle Pin(GeoPoint point) => new(point.Latitude, point.Longitude, PillPlacement.PinPx / 2, PillPlacement.PinPx);
}

/// <param name="Z">Higher draws on top: the order stations get room in, so the price shown on top is the one that got it.</param>
/// <param name="PillFrom">The zoom from which the station shows its pill; below, it's a brand dot. 0: always a pill.</param>
public readonly record struct PillPlace(int Z, double PillFrom);

/// <summary>
/// From which zoom each station has room for its price. The map neither rotates nor tilts, so two points are always
/// Δworld × 512 × 2^zoom pixels apart, and every overlap clears at a zoom worked out once, rather than on each frame of a
/// pinch. Stations get room in order: the selected one, the pick, the second, then the ones listed cheapest first, the
/// faded ones, and those without a price. Each shows its pill from the lowest zoom at which it overlaps no pill or dot of
/// the stations before it, nor what the map draws over them, by more than <see cref="PillOverlapPx"/>. Zooming in only
/// ever turns dots into prices, and from <see cref="AllPillsFromZoom"/> up every station has its pill. A price may cover
/// the dot of a pricier station beside it, which shows zoomed in: more prices, the owner's choice over every dot in sight.
/// </summary>
public static class PillPlacement
{
    /// <summary>From this zoom up every station shows its price, crowded or not, as it always has.</summary>
    public const double AllPillsFromZoom = 12;

    /// <summary>MapLibre's world is 512 × 2^zoom CSS pixels wide.</summary>
    private const double TileSizePx = 512;

    // A pill's box, from the station's point: the tip of its tail. Sizes are essence.css's, its price "165,9" at 12 px wide
    // enough for any platform's system font.
    private const double PillWidthPx = 68;
    private const double UnpricedWidthPx = 50; // "—"
    private const double PillTopPx = 33; // 26 for the pill, 7 for the tail
    private const double StarPx = 6; // .ess-fav, over the pill's top right corner
    private const double PickWidthPx = 80; // .is-best's larger pill
    private const double PickTopPx = 39;
    private const double FlagHeightPx = 20; // data-flag, with its margin
    private const double FlagPaddingPx = 12;
    private const double FlagCharPx = 7;
    private const double SelectedScale = 1.18; // .is-selected, around the tail's tip; the flag doesn't grow
    private const double DotHalfPx = 7.5; // 12 px and its border, centered on the point
    private const double RingedDotHalfPx = 9.5; // a favorite's gold ring, the closest's dark one

    internal const double LabelHeightPx = 35;
    internal const double LabelPaddingPx = 19;
    internal const double LabelCharPx = 6.6;
    internal const double PinPx = 30;

    /// <summary>
    /// How far a pill may reach into what stands before it. A price's digits sit 8 px or more inside its pill, so an edge
    /// covered this much hides neither price, and the one on top is the one that got the room.
    /// </summary>
    private const double PillOverlapPx = 6;

    /// <summary>The order, then the zoom from which each station shows its price, capped at <paramref name="allPillsFromZoom"/>.</summary>
    public static IReadOnlyDictionary<long, PillPlace> Place(
        IReadOnlyList<PillCandidate> candidates, IReadOnlyList<PillObstacle>? obstacles = null, double allPillsFromZoom = AllPillsFromZoom)
    {
        var ordered = candidates
            .OrderBy(Rank)
            .ThenBy(c => c.PriceCents ?? 0)
            .ThenBy(c => c.Id)
            .ToList();

        // In scale (pixels per world unit, 512 × 2^zoom): the one from which each station shows its pill.
        const double alwaysScale = TileSizePx;
        var capScale = TileSizePx * Math.Pow(2, allPillsFromZoom);
        var count = ordered.Count;
        var points = ordered.Select(c => World(c.Latitude, c.Longitude)).ToArray();
        var pills = ordered.Select(PillBox).ToArray();
        var dots = ordered.Select(c => c.Favorite || c.Second ? RingedDotHalfPx : DotHalfPx).ToArray();
        var pillFrom = new double[count];
        var blockers = (obstacles ?? []).Select(o => (Point: World(o.Latitude, o.Longitude), Box: new Box(o.HalfWidth, o.HalfWidth, o.Top, 0))).ToList();
        var places = new Dictionary<long, PillPlace>(count);

        // How far any pill reaches across and up, and any dot down: what no station farther than that can block.
        var widest = count == 0 ? 0 : pills.Max(p => Math.Max(p.Left, p.Right));
        var tallest = count == 0 ? 0 : pills.Max(p => p.Top);
        const double deepest = RingedDotHalfPx;
        for (var i = 0; i < count; i++)
        {
            var scale = alwaysScale;
            if (!ordered[i].Selected && !ordered[i].Pick)
            {
                var (x, y) = points[i];
                ref readonly var a = ref pills[i];
                foreach (var (point, box) in blockers)
                {
                    scale = Math.Max(scale, Clearing(point.X - x, point.Y - y, a, box));
                }

                // Up to a few hundred markers, on every move, in WebAssembly's interpreter on a phone: kept lean, and a
                // station already clear at the scale reached so far is passed over.
                var reachX = Math.Max(a.Left, a.Right) + widest - PillOverlapPx;
                var reachY = Math.Max(a.Top + deepest, tallest) - PillOverlapPx;
                for (var j = 0; j < i; j++)
                {
                    var dx = points[j].X - x;
                    var dy = points[j].Y - y;
                    if (Math.Abs(dx) * scale >= reachX || Math.Abs(dy) * scale >= reachY)
                    {
                        continue;
                    }

                    var perX = 1 / Math.Abs(dx);
                    var perY = 1 / Math.Abs(dy);
                    ref readonly var b = ref pills[j];

                    // Its pill blocks from the zoom it opens to the one this clears it; its dot, below the zoom it opens.
                    var pill = Math.Min(
                        Needed(dx >= 0 ? a.Right + b.Left : a.Left + b.Right, perX), Needed(dy >= 0 ? a.Bottom + b.Top : a.Top + b.Bottom, perY));
                    if (pillFrom[j] < pill)
                    {
                        scale = Math.Max(scale, pill);
                    }

                    var dot = Math.Min(Needed((dx >= 0 ? a.Right : a.Left) + dots[j], perX), Needed((dy >= 0 ? a.Bottom : a.Top) + dots[j], perY));
                    scale = Math.Max(scale, Math.Min(pillFrom[j], dot));
                    if (scale >= capScale)
                    {
                        break;
                    }
                }
            }

            pillFrom[i] = Math.Clamp(scale, alwaysScale, capScale);
            places[ordered[i].Id] = new PillPlace(count - i, Math.Log2(pillFrom[i] / TileSizePx));
        }

        return places;
    }

    private static int Rank(PillCandidate c) =>
        c.Selected ? 0 : c.Pick ? 1 : c.Second ? 2 : c.PriceCents is null ? 5 : c.Dimmed ? 4 : 3;

    /// <summary>Web Mercator, the whole world from 0 to 1, y down.</summary>
    private static (double X, double Y) World(double latitude, double longitude) =>
        ((longitude + 180) / 360, (1 - Math.Asinh(Math.Tan(latitude * Math.PI / 180)) / Math.PI) / 2);

    /// <summary>The scale from which box <paramref name="a"/> clears box <paramref name="b"/>, <paramref name="dx"/> and <paramref name="dy"/> away in the world.</summary>
    private static double Clearing(double dx, double dy, in Box a, in Box b) => Math.Min(
        Needed(dx >= 0 ? a.Right + b.Left : a.Left + b.Right, 1 / Math.Abs(dx)),
        Needed(dy >= 0 ? a.Bottom + b.Top : a.Top + b.Bottom, 1 / Math.Abs(dy)));

    /// <summary>
    /// The scale that sets two points <paramref name="pixels"/> apart, less the overlap allowed, at <paramref name="perWorld"/>
    /// (1 over how far apart they are in the world; infinite at one spot, which no scale clears).
    /// </summary>
    private static double Needed(double pixels, double perWorld) => pixels <= PillOverlapPx ? 0 : (pixels - PillOverlapPx) * perWorld;

    private static Box PillBox(PillCandidate c)
    {
        var scale = c.Selected ? SelectedScale : 1;
        var half = (c.Pick ? PickWidthPx : c.PriceCents is null ? UnpricedWidthPx : PillWidthPx) / 2 * scale;
        var top = c.Pick ? PickTopPx : PillTopPx;
        var box = c.Favorite
            ? new Box(half, half + StarPx * scale, (top + StarPx) * scale, 0)
            : new Box(half, half, top * scale, 0);
        if ((c.Pick || c.Second) && c.Flag is { } flag)
        {
            var flagHalf = (FlagPaddingPx + FlagCharPx * flag.Length) / 2;
            box = new Box(Math.Max(box.Left, flagHalf), Math.Max(box.Right, flagHalf), Math.Max(box.Top, top + FlagHeightPx), 0);
        }

        return box;
    }

    /// <summary>How far a marker reaches from its point on each side, in pixels; <see cref="Top"/> is up.</summary>
    private readonly record struct Box(double Left, double Right, double Top, double Bottom);
}
