namespace Comic.WinUI.Services;

public static class ReaderLayoutCalculator
{
    public static int PrimaryColumnSpan(bool isDoublePage) => isDoublePage ? 1 : 2;

    public static (double Width, double Height) CalculateFitSize(
        double pixelWidth,
        double pixelHeight,
        double viewportWidth,
        double viewportHeight,
        bool isDoublePage,
        double columnSpacing = 8)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return (0, 0);

        var availableWidth = Math.Max(1, viewportWidth);
        if (isDoublePage)
        {
            availableWidth = Math.Max(1, (availableWidth - columnSpacing) / 2);
        }

        var availableHeight = Math.Max(1, viewportHeight);
        var fitScale = Math.Min(availableWidth / pixelWidth, availableHeight / pixelHeight);
        return (pixelWidth * fitScale, pixelHeight * fitScale);
    }
}
