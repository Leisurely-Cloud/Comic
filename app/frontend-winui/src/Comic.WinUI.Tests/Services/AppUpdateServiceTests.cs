using System.Net;
using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class AppUpdateServiceTests
{
    [TestMethod]
    public async Task Check_ParsesReleaseNotesAndSelectsSetupExecutable()
    {
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v999.0.0","name":"未来版本","body":"更新说明","html_url":"https://example.test/release","assets":[{"name":"ComicDownloader-999.0.0-Setup.exe","browser_download_url":"https://example.test/setup.exe","size":42}]}""")
        });
        using var client = new HttpClient(handler);
        var service = new AppUpdateService(client);

        var result = await service.CheckAsync();

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("v999.0.0", result.LatestVersion);
        Assert.AreEqual("更新说明", result.ReleaseNotes);
        Assert.AreEqual("ComicDownloader-999.0.0-Setup.exe", result.AssetName);
    }
}
