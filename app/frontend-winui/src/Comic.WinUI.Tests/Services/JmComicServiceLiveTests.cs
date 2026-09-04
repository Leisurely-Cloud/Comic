using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class JmComicServiceLiveTests
{
    [TestMethod]
    [TestCategory("Live")]
    public async Task RankingEndpoint_ReturnsAtLeastOneItem()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("COMIC_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("设置 COMIC_RUN_LIVE_TESTS=1 后才运行联网测试。");
        }

        using var service = new JmComicService(new HttpClient());
        var result = await service
            .GetRankingAsync("最新更新", 1)
            .WaitAsync(TimeSpan.FromSeconds(45));

        Assert.IsTrue(result.Items.Count > 0, "JM 排行榜接口返回了空列表。");
        Assert.IsTrue(result.Items.All(item => !string.IsNullOrWhiteSpace(item.Url)));
    }
}
