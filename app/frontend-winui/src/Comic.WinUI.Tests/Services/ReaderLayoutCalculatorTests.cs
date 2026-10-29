using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ReaderLayoutCalculatorTests
{
    [TestMethod]
    public void StripItemMinHeight_RemovesLoadingPlaceholderAfterImageLoads()
    {
        Assert.AreEqual(480, ReaderLayoutCalculator.StripItemMinHeight(false));
        Assert.AreEqual(0, ReaderLayoutCalculator.StripItemMinHeight(true));
    }

    [TestMethod]
    public void PrimaryColumnSpan_SinglePageFillsBothColumnsAndSpreadUsesOne()
    {
        Assert.AreEqual(2, ReaderLayoutCalculator.PrimaryColumnSpan(false));
        Assert.AreEqual(1, ReaderLayoutCalculator.PrimaryColumnSpan(true));
    }

    [TestMethod]
    public void CalculateFitSize_DoublePageUsesHalfViewportForEachPage()
    {
        var size = ReaderLayoutCalculator.CalculateFitSize(1000, 2000, 2008, 2000, true);

        Assert.AreEqual(1000, size.Width, 0.001);
        Assert.AreEqual(2000, size.Height, 0.001);
    }

    [TestMethod]
    public void CalculateFitSize_PreservesEachPagesOwnAspectRatio()
    {
        var portrait = ReaderLayoutCalculator.CalculateFitSize(1000, 2000, 2008, 1200, true);
        var landscape = ReaderLayoutCalculator.CalculateFitSize(2000, 1000, 2008, 1200, true);

        Assert.AreEqual(600, portrait.Width, 0.001);
        Assert.AreEqual(1200, portrait.Height, 0.001);
        Assert.AreEqual(1000, landscape.Width, 0.001);
        Assert.AreEqual(500, landscape.Height, 0.001);
    }

    [TestMethod]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    public void LogicalViewportAtDisplayScale_FitsBothPagesAndRestoresSinglePageWidth(double displayScale)
    {
        // Unit-level DIP geometry, not a claim that Windows display scaling was exercised.
        var width = 2400 / displayScale;
        var height = 1800 / displayScale;
        var spread = ReaderLayoutCalculator.CalculateFitSize(2000, 1000, width, height, true);
        var single = ReaderLayoutCalculator.CalculateFitSize(2000, 1000, width, height, false);
        Assert.AreEqual(width, spread.Width * 2 + 8, 0.001);
        Assert.IsTrue(spread.Height <= height);
        Assert.AreEqual(width, single.Width, 0.001);
        Assert.AreEqual(2, ReaderLayoutCalculator.PrimaryColumnSpan(false));
    }
}
