using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public class JmComicServiceTests
{
    [TestMethod]
    public void ParseMangaId_AcceptsAlbumQueryAndNumericInput()
    {
        Assert.AreEqual("12345", JmComicService.ParseMangaId("https://18comic.vip/album/12345").MangaId);
        Assert.AreEqual("678", JmComicService.ParseMangaId("https://18comic.vip/album?id=678").MangaId);
        Assert.AreEqual("42", JmComicService.ParseMangaId("42").MangaId);
        Assert.IsNull(JmComicService.ParseMangaId("https://example.test/no-id").MangaId);
    }

    [TestMethod]
    public void DecryptPayload_DecodesAesEcbEnvelope()
    {
        const long timestamp = 1_700_000_000;
        var plaintext = Encoding.UTF8.GetBytes("""{"id":123,"name":"测试漫画"}""");
        var keyText = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var result = JmComicService.DecryptPayload(timestamp, Convert.ToBase64String(encrypted));

        CollectionAssert.AreEqual(plaintext, result);
    }

    [TestMethod]
    public void CalculateBlockCount_MatchesJmRules()
    {
        Assert.AreEqual(0, JmComicService.CalculateBlockCount(220_980, 220_979, "001"));
        Assert.AreEqual(10, JmComicService.CalculateBlockCount(220_980, 220_980, "001"));
        Assert.IsTrue(JmComicService.CalculateBlockCount(220_980, 500_000, "001") is >= 2 and <= 16);
    }

    [TestMethod]
    public void ReorderVerticalBlocks_ReversesSourceBlockOrder()
    {
        var pixels = Enumerable.Range(0, 5)
            .SelectMany(row => Enumerable.Repeat((byte)row, 4))
            .ToArray();

        var result = JmComicService.ReorderVerticalBlocks(pixels, width: 1, height: 5, blockCount: 2);
        var firstByteOfEachRow = Enumerable.Range(0, 5).Select(row => result[row * 4]).ToArray();

        CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 0, 1 }, firstByteOfEachRow);
    }

    [TestMethod]
    public async Task GetRankingAsync_UsesCategoryFilterInsteadOfEmptySearch()
    {
        using var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.AreEqual("/categories/filter", request.RequestUri?.AbsolutePath);
            var query = request.RequestUri?.Query ?? string.Empty;
            StringAssert.Contains(query, "page=2");
            StringAssert.Contains(query, "order=");
            StringAssert.Contains(query, "c=0");
            StringAssert.Contains(query, "o=tf");
            return BuildEncryptedResponse(request,
                """
                {
                  "total": "81",
                  "content": [
                    {"id":"1001","name":"榜单作品","author":["作者甲"],"category":{"id":"1","title":"同人"}}
                  ]
                }
                """);
        });
        using var service = new JmComicService(new HttpClient(handler));

        var result = await service.GetRankingAsync("最多点赞", 2);

        Assert.AreEqual(81, result.Total);
        Assert.AreEqual("最多点赞", result.Section);
        Assert.AreEqual("榜单作品", result.Items.Single().Title);
        Assert.AreEqual("https://18comic.vip/album/1001", result.Items.Single().Url);
    }

    [TestMethod]
    public void DecryptPayload_UsesSuppliedDataSecretInsteadOfDefault()
    {
        // 站点密钥集中在 JmSiteOptions 里,注入自定义配置必须真正生效。
        const long timestamp = 1_700_000_000;
        const string customSecret = "custom-data-secret";
        var plaintext = Encoding.UTF8.GetBytes("""{"id":7}""");
        var encrypted = EncryptWithSecret(timestamp, customSecret, plaintext);

        var result = JmComicService.DecryptPayload(timestamp, encrypted, customSecret);

        CollectionAssert.AreEqual(plaintext, result);

        // 用默认密钥解同一段密文不可能得到相同明文,否则说明 dataSecret 参数没被采纳。
        // 注意不能直接断言抛异常:错密钥解出的随机字节有约 1/256 的概率恰好通过
        // PKCS#7 校验,那样断言就成了偶发失败。
        byte[]? decodedWithDefaultSecret = null;
        try
        {
            decodedWithDefaultSecret = JmComicService.DecryptPayload(timestamp, encrypted);
        }
        catch (InvalidDataException)
        {
            // 填充校验失败是最常见的结果,同样说明默认密钥解不开。
        }

        if (decodedWithDefaultSecret is not null)
        {
            CollectionAssert.AreNotEqual(plaintext, decodedWithDefaultSecret);
        }
    }

    [TestMethod]
    public void SanitizeFileName_ReplacesIllegalCharactersAndTrimsEdges()
    {
        Assert.AreEqual("a_b_c", JmComicService.SanitizeFileName("a/b\\c"));
        Assert.AreEqual("标题", JmComicService.SanitizeFileName("  .标题. "));
        Assert.AreEqual("unnamed", JmComicService.SanitizeFileName("   "));
        Assert.AreEqual("unnamed", JmComicService.SanitizeFileName("..."));
    }

    [TestMethod]
    public void SanitizeFileName_EscapesWindowsReservedDeviceNames()
    {
        // 标题来自站点,叫 NUL 或 CON.jpg 会让 CreateDirectory/File.Create 直接失败。
        Assert.AreEqual("_NUL", JmComicService.SanitizeFileName("NUL"));
        Assert.AreEqual("_nul.jpg", JmComicService.SanitizeFileName("nul.jpg"));
        Assert.AreEqual("_CON.txt", JmComicService.SanitizeFileName("CON.txt"));
        Assert.AreEqual("_COM1", JmComicService.SanitizeFileName("COM1"));
        Assert.AreEqual("_LPT9", JmComicService.SanitizeFileName("LPT9"));
        // 只是以保留名开头的普通标题不能被改写。
        Assert.AreEqual("CONTENT", JmComicService.SanitizeFileName("CONTENT"));
        Assert.AreEqual("COM10", JmComicService.SanitizeFileName("COM10"));
    }

    [TestMethod]
    public void SanitizeFileName_TruncatesWithoutSplittingSurrogatePairs()
    {
        // 第 200 个码元正好落在 emoji 的高代理项上,直接切会留下孤立代理项。
        var value = new string('a', 199) + "😀" + new string('b', 50);
        Assert.IsTrue(value.Length > 200);

        var result = JmComicService.SanitizeFileName(value);

        Assert.AreEqual(199, result.Length);
        Assert.IsFalse(result.Any(char.IsSurrogate), "截断结果不应包含孤立代理项。");
        Assert.AreEqual(new string('a', 199), result);
    }

    [TestMethod]
    public void SanitizeFileName_KeepsWholeEmojiWhenPairFitsInsideLimit()
    {
        var value = new string('a', 198) + "😀" + new string('b', 50);

        var result = JmComicService.SanitizeFileName(value);

        Assert.AreEqual(200, result.Length);
        Assert.AreEqual(new string('a', 198) + "😀", result);
    }

    [TestMethod]
    public async Task DownloadChapterAsync_WaitsBeforeStartingImageRequests()
    {
        var imageRequestCount = 0;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/chapter", StringComparison.OrdinalIgnoreCase))
            {
                var tokenParam = request.Headers.GetValues("tokenparam").Single();
                var timestamp = long.Parse(tokenParam.Split(',')[0]);
                var payload = Encoding.UTF8.GetBytes(
                    """{"id":"123","name":"测试章节","images":["001.jpg","002.jpg","003.jpg"]}""");
                var encrypted = EncryptWithSecret(timestamp, "185Hcomic3PAPP7R", payload);
                var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(envelope, Encoding.UTF8, "application/json")
                };
            }

            if (path.EndsWith("/chapter_view_template", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<script>var scramble_id = 220980;</script>")
                };
            }

            if (path.Contains("/media/photos/", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref imageRequestCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        var service = new JmComicService(client);
        var pauseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), $"comic-pause-{Guid.NewGuid():N}");

        try
        {
            async Task WaitBeforeImage(CancellationToken cancellationToken)
            {
                pauseReached.TrySetResult();
                await resume.Task.WaitAsync(cancellationToken);
            }

            var download = service.DownloadChapterAsync(
                new JmChapter(1, "123", "测试章节"),
                root,
                maxConcurrentImages: 2,
                waitBeforeImage: WaitBeforeImage,
                preferredDirectoryName: "1");

            await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.AreEqual(0, Volatile.Read(ref imageRequestCount), "暂停时不应发起新的图片请求。");

            resume.TrySetResult();
            var result = await download.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(3, result.ImageCount);
            Assert.AreEqual("1", result.DirectoryName);
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "1")));
            Assert.AreEqual(3, Volatile.Read(ref imageRequestCount));
        }
        finally
        {
            resume.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetAlbumCommentsAsync_ParsesCommentsRepliesAndPlainText()
    {
        using var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.AreEqual("/forum", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "aid=323359");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "page=2");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "mode=all");

            var tokenParam = request.Headers.GetValues("tokenparam").Single();
            var timestamp = long.Parse(tokenParam.Split(',')[0]);
            var payload = Encoding.UTF8.GetBytes(
                """
                {
                  "total": "12",
                  "list": [
                    {
                      "CID": "88",
                      "UID": "9",
                      "nickname": "測試用戶",
                      "content": "很好看<br>&lt;繼續更新&gt;<b>！媽媽</b>",
                      "addtime": "1700000000",
                      "likes": "3",
                      "spoiler": "2",
                      "replys": [
                        {
                          "CID": "89",
                          "username": "回覆者",
                          "content": "同感，這期很精彩",
                          "addtime": "2026-08-22"
                        }
                      ]
                    }
                  ]
                }
                """);
            var encrypted = EncryptWithSecret(timestamp, "185Hcomic3PAPP7R", payload);
            var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        using var service = new JmComicService(client);

        var result = await service.GetAlbumCommentsAsync("https://18comic.vip/album/323359", 2);

        Assert.AreEqual(12, result.Total);
        Assert.AreEqual(2, result.Page);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("88", result.Items[0].Id);
        Assert.AreEqual("测试用户", result.Items[0].AuthorDisplay);
        Assert.AreEqual("很好看\n<继续更新>！妈妈", result.Items[0].Content);
        Assert.AreEqual(3, result.Items[0].Likes);
        Assert.IsTrue(result.Items[0].IsSpoiler);
        Assert.AreEqual(1, result.Items[0].Replies.Count);
        Assert.AreEqual("回复者", result.Items[0].Replies[0].AuthorDisplay);
        Assert.AreEqual("同感，这期很精彩", result.Items[0].Replies[0].Content);
    }

    [TestMethod]
    public async Task ResolveAsync_ParsesDetailedAlbumMetadataForSelectionPanel()
    {
        using var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.AreEqual("/album", request.RequestUri?.AbsolutePath);
            return BuildEncryptedResponse(request,
                """
                {
                  "id": "1459797",
                  "name": "详情测试漫画",
                  "author": ["測試作者"],
                  "description": "這是<b>詳細</b>介紹",
                  "tags": ["全彩", "戀愛"],
                  "total_views": "123456",
                  "likes": "789",
                  "comment_total": "173",
                  "is_favorite": "1",
                  "addtime": "1700000000",
                  "series": [
                    { "id": "1459797", "name": "第一章" }
                  ]
                }
                """);
        });
        using var service = new JmComicService(new HttpClient(handler));

        var result = await service.ResolveAsync("https://18comic.vip/album/1459797");

        Assert.AreEqual("1459797", result.MangaId);
        Assert.AreEqual("测试作者", result.Author);
        Assert.AreEqual("这是 详细 介绍", result.Description);
        CollectionAssert.AreEqual(new[] { "全彩", "恋爱" }, result.Tags);
        Assert.AreEqual("123456", result.TotalViews);
        Assert.AreEqual("789", result.Likes);
        Assert.AreEqual("173", result.CommentCount);
        Assert.IsTrue(result.IsFavorite);
        Assert.AreEqual(1, result.Chapters.Count);
        Assert.AreEqual(1, result.Chapters[0].Order);
    }

    [TestMethod]
    public async Task GetWeeklyPicksIndexAsync_ParsesTypesAndSortsNewestIssueFirst()
    {
        using var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.AreEqual("/week", request.RequestUri?.AbsolutePath);
            return BuildEncryptedResponse(request,
                """
                {
                  "categories": [
                    { "id": "253", "time": "2026第252期" },
                    { "id": "254", "time": "2026第253期" }
                  ],
                  "type": [
                    { "id": "hanman", "title": "韓漫" },
                    { "id": "manga", "title": "日漫" }
                  ]
                }
                """);
        });
        using var service = new JmComicService(new HttpClient(handler));

        var result = await service.GetWeeklyPicksIndexAsync();

        Assert.AreEqual(2, result.Issues.Count);
        Assert.AreEqual("254", result.Issues[0].Id);
        Assert.AreEqual("2026第253期", result.Issues[0].Title);
        Assert.AreEqual("hanman", result.Types[0].Id);
        Assert.AreEqual("韩漫", result.Types[0].Title);
    }

    [TestMethod]
    public async Task GetWeeklyPicksAsync_ParsesOfficialItemsAndCategoryShapes()
    {
        using var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.AreEqual("/week/filter", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "id=254");
            return BuildEncryptedResponse(request,
                """
                {
                  "total": "2",
                  "list": [
                    {
                      "id": "1001",
                      "name": "每周作品一",
                      "author": ["作者甲"],
                      "description": "简介<br><b>内容</b>",
                      "category": "hanman",
                      "update_at": "1700000000"
                    },
                    {
                      "id": "1002",
                      "name": "每周作品二",
                      "author": "作者乙",
                      "category_sub": ["manga", { "another": "其他" }]
                    }
                  ]
                }
                """);
        });
        using var service = new JmComicService(new HttpClient(handler));

        var result = await service.GetWeeklyPicksAsync("254");

        Assert.AreEqual("254", result.IssueId);
        Assert.AreEqual(2, result.Total);
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual("每周作品一", result.Items[0].Title);
        Assert.AreEqual("作者甲", result.Items[0].Author);
        Assert.AreEqual("简介 内容", result.Items[0].Description);
        CollectionAssert.Contains(result.Items[0].CategoryKeys, "hanman");
        CollectionAssert.Contains(result.Items[1].CategoryKeys, "manga");
        CollectionAssert.Contains(result.Items[1].CategoryKeys, "another");
    }

    [TestMethod]
    public async Task LoginAsync_PostsCredentialsAndKeepsSessionOnlyInMemory()
    {
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/login", request.RequestUri?.AbsolutePath);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            StringAssert.Contains(form, "username=test%40example.com");
            StringAssert.Contains(form, "password=secret");
            var response = BuildEncryptedResponse(request,
                """
                {
                  "uid": "42", "username": "tester", "email": "test@example.com",
                  "level_name": "LV.5", "coin": "12", "album_favorites": "87",
                  "s": "session-token"
                }
                """);
            response.Headers.TryAddWithoutValidation("Set-Cookie", "device=desktop; Path=/; HttpOnly");
            return response;
        });
        using var service = new JmComicService(new HttpClient(handler));

        var account = await service.LoginAsync("test@example.com", "secret");
        var state = service.GetAccountState();

        Assert.AreEqual("tester", account.Username);
        Assert.AreEqual(87, account.FavoriteCount);
        Assert.IsTrue(state.IsLoggedIn);
        Assert.AreEqual("42", state.Account?.UserId);
    }

    [TestMethod]
    public async Task LoginAsync_TimesOutSlowDomainAndFallsBackToNextDomain()
    {
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.Host == "slow.test")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return BuildEncryptedResponse(request, """{"uid":"42","username":"fallback","s":"token"}""");
        });
        using var service = new JmComicService(
            new HttpClient(handler),
            new JmSiteOptions
            {
                ApiDomains = ["slow.test", "working.test"],
                ApiRequestTimeout = TimeSpan.FromMilliseconds(50),
            });

        var account = await service.LoginAsync("tester", "secret").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual("fallback", account.Username);
        CollectionAssert.AreEqual(
            new[] { "slow.test", "working.test" },
            handler.RequestedUris.Select(uri => new Uri(uri).Host).ToArray());
    }

    [TestMethod]
    public async Task GetFavoritesAsync_SendsSessionCookieAndParsesFoldersAndPaging()
    {
        var favoriteRequestSeen = false;
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/login")
            {
                await request.Content!.ReadAsStringAsync(cancellationToken);
                var response = BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "device=desktop; Path=/");
                return response;
            }

            Assert.AreEqual("/favorite", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "page=2");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "folder_id=7");
            var cookie = request.Headers.GetValues("Cookie").Single();
            StringAssert.Contains(cookie, "AVS=session-token");
            StringAssert.Contains(cookie, "device=desktop");
            favoriteRequestSeen = true;
            return BuildEncryptedResponse(request,
                """
                {
                  "list": [{"id":"1001","name":"收藏作品","author":["作者甲"]}],
                  "folder_list": [{"FID":"7","name":"我的收藏"}],
                  "total":"21", "count":20
                }
                """);
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");

        var result = await service.GetFavoritesAsync(2, "7");

        Assert.IsTrue(favoriteRequestSeen);
        Assert.AreEqual(21, result.Total);
        Assert.AreEqual(20, result.PageSize);
        Assert.AreEqual("收藏作品", result.Items.Single().Title);
        Assert.AreEqual("7", result.Folders.Single().Id);
    }

    [TestMethod]
    public async Task Logout_ClearsSessionAndBlocksFavoriteRequests()
    {
        var requestCount = 0;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            requestCount++;
            return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");
        service.Logout();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.GetFavoritesAsync());

        Assert.IsFalse(service.GetAccountState().IsLoggedIn);
        Assert.AreEqual(1, requestCount, "退出后应在发请求前拦截收藏夹访问。");
    }

    [TestMethod]
    public async Task SetJmFavoriteAsync_AddsWithPostAndAlbumIdInFormBody()
    {
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/login")
                return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");

            Assert.AreEqual("/favorite", request.RequestUri?.AbsolutePath);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(string.Empty, request.RequestUri?.Query);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.AreEqual("aid=1001", form);
            return BuildEncryptedResponse(request, """{"status":"ok","msg":"操作成功"}""");
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");

        var result = await service.SetJmFavoriteAsync("1001", true);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("操作成功", result.Message);
    }

    [TestMethod]
    public async Task SetJmFavoriteAsync_RemovesWithSameToggleRequest()
    {
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/login")
                return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");

            Assert.AreEqual("/favorite", request.RequestUri?.AbsolutePath);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(string.Empty, request.RequestUri?.Query);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.AreEqual("aid=1001", form);
            return BuildEncryptedResponse(request, """{"status":"ok","msg":"取消成功"}""");
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");

        var result = await service.SetJmFavoriteAsync("1001", false);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("取消成功", result.Message);
    }

    [TestMethod]
    public async Task SetJmFavoriteAsync_UsesServerMessageWhenMutationFails()
    {
        using var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/login")
                return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");
            return BuildEncryptedResponse(request, """{"status":"error","msg":"尚未收藏"}""");
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.SetJmFavoriteAsync("1001", false));

        StringAssert.Contains(error.Message, "尚未收藏");
    }

    [TestMethod]
    public async Task ManageFavoriteFolderAsync_AddsFolderWithOfficialFields()
    {
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/login")
                return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");

            Assert.AreEqual("/favorite_folder", request.RequestUri?.AbsolutePath);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            StringAssert.Contains(form, "type=add");
            StringAssert.Contains(form, "folder_id=0");
            StringAssert.Contains(form, "folder_name=%E8%BF%BD%E6%9B%B4");
            return BuildEncryptedResponse(request, """{"status":"ok","msg":"新增成功"}""");
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");

        var result = await service.ManageFavoriteFolderAsync(
            JmFavoriteFolderOperation.Add,
            folderName: "追更");

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task ManageFavoriteFolderAsync_MovesAlbumWithOfficialFields()
    {
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath == "/login")
                return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");

            Assert.AreEqual("/favorite_folder", request.RequestUri?.AbsolutePath);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            StringAssert.Contains(form, "type=move");
            StringAssert.Contains(form, "folder_id=7");
            StringAssert.Contains(form, "aid=1001");
            return BuildEncryptedResponse(request, """{"status":"ok","msg":"移动成功"}""");
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");

        var result = await service.ManageFavoriteFolderAsync(
            JmFavoriteFolderOperation.Move,
            folderId: "7",
            albumId: "1001");

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task ManageFavoriteFolderAsync_RejectsDefaultFolderDeletionBeforeRequest()
    {
        var requestCount = 0;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            requestCount++;
            return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");
        });
        using var service = new JmComicService(new HttpClient(handler));
        await service.LoginAsync("tester", "secret");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            service.ManageFavoriteFolderAsync(JmFavoriteFolderOperation.Delete, folderId: "0"));

        Assert.AreEqual(1, requestCount);
    }

    private static HttpResponseMessage BuildEncryptedResponse(HttpRequestMessage request, string json)
    {
        var tokenParam = request.Headers.GetValues("tokenparam").Single();
        var timestamp = long.Parse(tokenParam.Split(',')[0]);
        var payload = Encoding.UTF8.GetBytes(json);
        var encrypted = EncryptWithSecret(timestamp, "185Hcomic3PAPP7R", payload);
        var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    private static string EncryptWithSecret(long timestamp, string secret, byte[] plaintext)
    {
        var keyText = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + secret))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
    }
}
