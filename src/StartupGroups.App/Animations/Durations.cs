namespace StartupGroups.App.Animations;

public static class Durations
{
    public static readonly TimeSpan OpacityFadeFast = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan SidebarIndicatorSlide = TimeSpan.FromMilliseconds(280);
    public static readonly TimeSpan AdminCardPulse = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan RowReorderPreview = TimeSpan.FromMilliseconds(160);
}

public static class UiMetrics
{
    public const double AdminCardHighlightBlurRadius = 24;
    public const int AdminCardPulseRepeats = 2;
    public const double RowReorderSnapToleranceY = 0.5;
}
