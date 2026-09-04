using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class BackendClientTests
{
    private string _container = null!;
    private string _storageRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        Directory.CreateDirectory(_storageRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task Settings_CanSwitchManagedStorageRoot()
    {
        var nextStorageRoot = Path.Combine(_container, "next-library");
        using var httpClient = new HttpClient();
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var client = TestServiceFactory.CreateClient(
            new JmComicService(httpClient),
            scheduler,
            library,
            exporter,
            reader,
            settings);

        var result = await client.UpdateSettingsAsync(
            new SettingsUpdateRequest { StorageRoot = nextStorageRoot });

        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), result.StorageRoot);
        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), library.StorageRoot);
        Assert.IsTrue(Directory.Exists(Path.Combine(nextStorageRoot, ".comic_state")));
        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), settings.StorageRoot);
    }

    [TestMethod]
    public async Task Settings_RejectsEmptyStorageRoot()
    {
        using var httpClient = new HttpClient();
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var client = TestServiceFactory.CreateClient(
            new JmComicService(httpClient),
            scheduler,
            library,
            exporter,
            reader,
            settings);

        await Assert.ThrowsExactlyAsync<BackendApiException>(
            () => client.UpdateSettingsAsync(new SettingsUpdateRequest { StorageRoot = "   " }));
    }

    [TestMethod]
    public async Task JmLogin_RestoresSavedCredentialAndLogoutClearsIt()
    {
        using var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/login", request.RequestUri?.AbsolutePath);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            StringAssert.Contains(form, "username=tester");
            StringAssert.Contains(form, "password=secret");
            return BuildEncryptedResponse(request, """{"uid":"42","username":"tester","s":"session-token"}""");
        });
        using var jmComic = new JmComicService(new HttpClient(handler));
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(jmComic, library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var credentials = new FakeJmCredentialStore("tester", "secret");
        var client = TestServiceFactory.CreateClient(
            jmComic, scheduler, library, exporter, reader, settings, credentials);

        var state = await client.RestoreJmLoginAsync();

        Assert.IsTrue(state.IsLoggedIn);
        Assert.AreEqual("tester", state.Account?.Username);
        client.LogoutJm();
        Assert.IsFalse(client.GetJmAccountState().IsLoggedIn);
        Assert.IsFalse(credentials.HasCredential);
    }

    [TestMethod]
    public async Task LibraryUpdates_DetectsMissingChapterByOrderInsteadOfOnlyComparingCounts()
    {
        var mangaRoot = Path.Combine(_storageRoot, "1001");
        Directory.CreateDirectory(mangaRoot);
        foreach (var order in new[] { 1, 3, 4 })
        {
            var chapterRoot = Path.Combine(mangaRoot, order.ToString("000"));
            Directory.CreateDirectory(chapterRoot);
            await File.WriteAllBytesAsync(Path.Combine(chapterRoot, "001.jpg"), [1, 2, 3]);
        }
        await File.WriteAllTextAsync(Path.Combine(mangaRoot, "元数据.json"),
            """
            {
              "schema_version": 1,
              "site_key": "jmcomic",
              "site_name": "禁漫天堂",
              "manga_title": "追更测试",
              "manga_url": "https://18comic.vip/album/1001"
            }
            """);

        using var handler = new FakeHttpMessageHandler(request =>
        {
            Assert.AreEqual("/album", request.RequestUri?.AbsolutePath);
            return BuildEncryptedResponse(request,
                """
                {
                  "id": "1001",
                  "name": "追更测试",
                  "author": ["作者"],
                  "series": [
                    { "id": "2001", "name": "第1话" },
                    { "id": "2002", "name": "第2话" },
                    { "id": "2003", "name": "第3话" }
                  ]
                }
                """);
        });
        using var jmComic = new JmComicService(new HttpClient(handler));
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(jmComic, library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var client = TestServiceFactory.CreateClient(jmComic, scheduler, library, exporter, reader, settings);

        var response = await client.CheckLibraryUpdatesAsync();

        Assert.AreEqual(1, response.CheckedCount);
        Assert.AreEqual(0, response.FailedCount);
        Assert.AreEqual(1, response.Items.Count);
        Assert.IsTrue(response.Items[0].HasUpdate);
        Assert.AreEqual(3, response.Items[0].LocalChapterCount);
        Assert.AreEqual(3, response.Items[0].RemoteChapterCount);
        Assert.AreEqual(1, response.Items[0].MissingChapters.Count);
        Assert.AreEqual(2, response.Items[0].MissingChapters[0].Order);
        StringAssert.EndsWith(response.Items[0].MissingChapters[0].Url, "/photo/2002");
    }

    [TestMethod]
    public async Task LibraryQuery_SearchesJmIdFiltersAndSortsByReadingTime()
    {
        var first = Path.Combine(_storageRoot, "100");
        var second = Path.Combine(_storageRoot, "200");
        Directory.CreateDirectory(Path.Combine(first, "1"));
        Directory.CreateDirectory(Path.Combine(second, "1"));
        await File.WriteAllBytesAsync(Path.Combine(first, "1", "001.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(second, "1", "001.jpg"), [2]);
        await File.WriteAllTextAsync(Path.Combine(first, "元数据.json"),
            """{"manga_title":"甲","manga_url":"https://18comic.vip/album/100","completed":true,"is_favorite":true}""");
        await File.WriteAllTextAsync(Path.Combine(second, "元数据.json"),
            """{"manga_title":"乙","manga_url":"https://18comic.vip/album/200","completed":false}""");
        using var httpClient = new HttpClient();
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings-query"));
        var progress = new ReadingProgressService(Path.Combine(_container, "progress-query"));
        progress.Save(second, "1", 0);
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var client = TestServiceFactory.CreateClient(new JmComicService(httpClient), scheduler, library,
            exporter, TestServiceFactory.CreateReader(library), settings, readingProgress: progress);

        var byId = await client.GetLibraryAsync(keyword: "100");
        var favorites = await client.GetLibraryAsync(favoritesOnly: true);
        var incomplete = await client.GetLibraryAsync(completion: LibraryCompletionFilter.Incomplete);
        var recent = await client.GetLibraryAsync(sort: LibrarySort.RecentRead);

        Assert.AreEqual("100", byId.Items.Single().MangaId);
        Assert.AreEqual("甲", favorites.Items.Single().Title);
        Assert.AreEqual("乙", incomplete.Items.Single().Title);
        Assert.AreEqual("乙", recent.Items.First().Title);
    }

    private static HttpResponseMessage BuildEncryptedResponse(HttpRequestMessage request, string json)
    {
        var timestamp = long.Parse(request.Headers.GetValues("tokenparam").Single().Split(',')[0]);
        var keyText = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(json);
        var encrypted = Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { code = 200, data = encrypted }),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class FakeJmCredentialStore(string username, string password) : IJmCredentialStore
    {
        private string _username = username;
        private string _password = password;
        public bool HasCredential => !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password);
        public bool TrySave(string savedUsername, string savedPassword)
        {
            _username = savedUsername;
            _password = savedPassword;
            return true;
        }
        public bool TryLoad(out string savedUsername, out string savedPassword)
        {
            savedUsername = _username;
            savedPassword = _password;
            return HasCredential;
        }
        public void Clear()
        {
            _username = string.Empty;
            _password = string.Empty;
        }
    }
}
