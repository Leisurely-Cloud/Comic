using System;
using Comic.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Helpers;

[TestClass]
public class ConvertersTests
{
    [TestMethod]
    public void InverseBoolToVisibilityConverter_True_ShouldReturnCollapsed()
    {
        var converter = new InverseBoolToVisibilityConverter();
        var result = converter.Convert(true, typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Collapsed, result);
    }

    [TestMethod]
    public void InverseBoolToVisibilityConverter_False_ShouldReturnVisible()
    {
        var converter = new InverseBoolToVisibilityConverter();
        var result = converter.Convert(false, typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Visible, result);
    }

    [TestMethod]
    public void InverseBoolToVisibilityConverter_NonBool_ShouldReturnVisible()
    {
        var converter = new InverseBoolToVisibilityConverter();
        var result = converter.Convert("test", typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Visible, result);
    }

    [TestMethod]
    public void InverseBoolToVisibilityConverter_ConvertBack_Visible_ShouldReturnFalse()
    {
        var converter = new InverseBoolToVisibilityConverter();
        var result = converter.ConvertBack(Visibility.Visible, typeof(bool), null!, "en-US");
        Assert.AreEqual(false, result);
    }

    [TestMethod]
    public void InverseBoolToVisibilityConverter_ConvertBack_Collapsed_ShouldReturnTrue()
    {
        var converter = new InverseBoolToVisibilityConverter();
        var result = converter.ConvertBack(Visibility.Collapsed, typeof(bool), null!, "en-US");
        Assert.AreEqual(true, result);
    }

    [TestMethod]
    public void InverseBoolConverter_True_ShouldReturnFalse()
    {
        var converter = new InverseBoolConverter();
        var result = converter.Convert(true, typeof(bool), null!, "en-US");
        Assert.AreEqual(false, result);
    }

    [TestMethod]
    public void InverseBoolConverter_False_ShouldReturnTrue()
    {
        var converter = new InverseBoolConverter();
        var result = converter.Convert(false, typeof(bool), null!, "en-US");
        Assert.AreEqual(true, result);
    }

    [TestMethod]
    public void InverseBoolConverter_NonBool_ShouldReturnTrue()
    {
        var converter = new InverseBoolConverter();
        var result = converter.Convert("test", typeof(bool), null!, "en-US");
        Assert.AreEqual(true, result);
    }

    [TestMethod]
    public void BoolToVisibilityConverter_True_ShouldReturnVisible()
    {
        var converter = new BoolToVisibilityConverter();
        var result = converter.Convert(true, typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Visible, result);
    }

    [TestMethod]
    public void BoolToVisibilityConverter_False_ShouldReturnCollapsed()
    {
        var converter = new BoolToVisibilityConverter();
        var result = converter.Convert(false, typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Collapsed, result);
    }

    [TestMethod]
    public void StringToVisibilityConverter_Empty_ShouldReturnCollapsed()
    {
        var converter = new StringToVisibilityConverter();
        var result = converter.Convert("", typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Collapsed, result);
    }

    [TestMethod]
    public void StringToVisibilityConverter_Whitespace_ShouldReturnCollapsed()
    {
        var converter = new StringToVisibilityConverter();
        var result = converter.Convert("   ", typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Collapsed, result);
    }

    [TestMethod]
    public void StringToVisibilityConverter_NonEmpty_ShouldReturnVisible()
    {
        var converter = new StringToVisibilityConverter();
        var result = converter.Convert("test", typeof(Visibility), null!, "en-US");
        Assert.AreEqual(Visibility.Visible, result);
    }

    [TestMethod]
    public void SelectedCountConverter_Zero_ShouldReturn未选择()
    {
        var converter = new SelectedCountConverter();
        var result = converter.Convert(0, typeof(string), null!, "zh-CN");
        Assert.AreEqual("未选择", result);
    }

    [TestMethod]
    public void SelectedCountConverter_Positive_ShouldReturn已选择()
    {
        var converter = new SelectedCountConverter();
        var result = converter.Convert(5, typeof(string), null!, "zh-CN");
        Assert.AreEqual("已选择 5 项", result);
    }

    [TestMethod]
    public void HistoryPrevEnabledConverter_Page1_ShouldReturnFalse()
    {
        var converter = new HistoryPrevEnabledConverter();
        var result = converter.Convert(1, typeof(bool), null!, "en-US");
        Assert.AreEqual(false, result);
    }

    [TestMethod]
    public void HistoryPrevEnabledConverter_Page2_ShouldReturnTrue()
    {
        var converter = new HistoryPrevEnabledConverter();
        var result = converter.Convert(2, typeof(bool), null!, "en-US");
        Assert.AreEqual(true, result);
    }
}
