using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ReaderLayoutCalculatorTests
{
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
}
