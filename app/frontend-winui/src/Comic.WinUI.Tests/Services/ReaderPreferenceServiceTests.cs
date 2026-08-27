using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ReaderPreferenceServiceTests
{
    [TestMethod]
    public void Preferences_AreSavedPerMangaWithoutChangingLegacySettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ReaderPreferenceService(directory);
            service.Save("manga-a", new ReaderPreference("spread", true, 80, 120));
            service.Save("manga-b", new ReaderPreference("strip", false, 60, 100));
            service.SaveShortcuts(new ReaderShortcutPreference("A", "D", "F11", false));

            var reloaded = new ReaderPreferenceService(directory);
            Assert.AreEqual("spread", reloaded.Get("manga-a", "paged", 40).Mode);
            Assert.IsTrue(reloaded.Get("manga-a", "paged", 40).RightToLeft);
            Assert.AreEqual("strip", reloaded.Get("manga-b", "paged", 40).Mode);
            Assert.AreEqual("A", reloaded.Shortcuts.PreviousKey);
            Assert.IsFalse(reloaded.Shortcuts.TapRightToAdvance);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
