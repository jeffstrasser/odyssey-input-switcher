namespace OdysseyInputSwitcher;

internal sealed class WindowLocation
{
    public required int X { get; set; }
    public required int Y { get; set; }
}

internal static class WindowPlacement
{
    internal static Point Resolve(WindowLocation? saved, Size size,
        IReadOnlyList<Rectangle> workingAreas, Rectangle primary)
    {
        if (saved is not null)
        {
            // Long arithmetic also handles corrupt/extreme saved coordinates without overflow.
            var best = workingAreas.Select(area => new
            {
                Area = area,
                Overlap = Math.Max(0L, Math.Min((long)saved.X + size.Width, area.Right) -
                    Math.Max((long)saved.X, area.Left)) *
                    Math.Max(0L, Math.Min((long)saved.Y + size.Height, area.Bottom) -
                    Math.Max((long)saved.Y, area.Top))
            }).OrderByDescending(item => item.Overlap).FirstOrDefault();
            if (best is not null && best.Overlap > 0)
                return new Point(
                    Math.Clamp(saved.X, best.Area.Left, Math.Max(best.Area.Left, best.Area.Right - size.Width)),
                    Math.Clamp(saved.Y, best.Area.Top, Math.Max(best.Area.Top, best.Area.Bottom - size.Height)));
        }
        return new Point(primary.Left + Math.Max(0, (primary.Width - size.Width) / 2),
            primary.Top + Math.Max(0, (primary.Height - size.Height) / 2));
    }
}
