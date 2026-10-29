using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.System;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ReaderKeyboardPolicyTests
{
    private static readonly ReaderShortcutPreference Defaults = new("Left", "Right", "F11", true);

    [TestMethod]
    [DataRow(VirtualKey.F11, false, false, "Fullscreen")]
    [DataRow(VirtualKey.F11, true, false, "Fullscreen")]
    [DataRow(VirtualKey.Escape, false, true, "Fullscreen")]
    [DataRow(VirtualKey.Escape, false, false, "Back")]
    [DataRow(VirtualKey.Left, false, false, "Previous")]
    [DataRow(VirtualKey.Right, true, false, "Next")]
    [DataRow(VirtualKey.PageUp, false, false, "Previous")]
    [DataRow(VirtualKey.PageDown, false, false, "Next")]
    [DataRow(VirtualKey.Home, false, false, "First")]
    [DataRow(VirtualKey.End, false, false, "Last")]
    [DataRow(VirtualKey.Home, true, false, "None")]
    [DataRow(VirtualKey.End, true, false, "None")]
    [DataRow(VirtualKey.PageUp, true, false, "None")]
    [DataRow(VirtualKey.PageDown, true, false, "None")]
    [DataRow(VirtualKey.A, false, false, "None")]
    public void DefaultMapping_PreservesModeAndEscapeBehavior(VirtualKey key, bool strip, bool fullscreen, string expected)
    {
        Assert.AreEqual(expected, ReaderKeyboardPolicy.Resolve(key, Defaults, strip, fullscreen, false, false).ToString());
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void EditingDialogsAndModifiedKeys_DoNotTriggerReader(bool inputOwnsKey, bool modified)
    {
        foreach (var key in new[] { VirtualKey.F11, VirtualKey.Escape, VirtualKey.Left, VirtualKey.Right,
                     VirtualKey.Home, VirtualKey.End, VirtualKey.PageUp, VirtualKey.PageDown })
            Assert.AreEqual(ReaderKeyAction.None, ReaderKeyboardPolicy.Resolve(key, Defaults, false, true, inputOwnsKey, modified));
    }

    [TestMethod]
    public void CustomKeys_AreCaseInsensitiveAndPreservePriority()
    {
        var custom = new ReaderShortcutPreference("a", "d", "f10", true);
        Assert.AreEqual(ReaderKeyAction.Previous, ReaderKeyboardPolicy.Resolve(VirtualKey.A, custom, false, false, false, false));
        Assert.AreEqual(ReaderKeyAction.Next, ReaderKeyboardPolicy.Resolve(VirtualKey.D, custom, false, false, false, false));
        Assert.AreEqual(ReaderKeyAction.Fullscreen, ReaderKeyboardPolicy.Resolve(VirtualKey.F10, custom, false, false, false, false));
        Assert.AreEqual(ReaderKeyAction.None, ReaderKeyboardPolicy.Resolve(VirtualKey.F11, custom, false, false, false, false));
        Assert.AreEqual(ReaderKeyAction.Previous, ReaderKeyboardPolicy.Resolve(VirtualKey.Left, custom, false, false, false, false));
        Assert.AreEqual(ReaderKeyAction.Next, ReaderKeyboardPolicy.Resolve(VirtualKey.Right, custom, true, false, false, false));
        Assert.AreEqual(ReaderKeyAction.Fullscreen, ReaderKeyboardPolicy.Resolve(VirtualKey.A,
            new("A", "A", "A", true), false, false, false, false));
    }
}
