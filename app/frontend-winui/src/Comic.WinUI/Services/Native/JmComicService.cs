using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Comic.WinUI.Services.Native;

public sealed class JmComicService : IDisposable
{
    private const string TempChapterPrefix = ".下载中_";

    private static readonly IReadOnlyDictionary<string, string> RankingSections =
        new Dictionary<string, string>
        {
            ["最新更新"] = "mr",
            ["最多浏览"] = "mv",
            ["最多图片"] = "mp",
            ["最多点赞"] = "tf",
        };

    private static readonly Regex ScrambleRegex = new(
        @"var\s+scramble_id\s*=\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Windows 保留设备名:即使带扩展名也不能作为文件或目录名。
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly JmSiteOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<JmComicService> _logger;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, (DateTimeOffset SavedAt, JsonElement Payload)> _albumCache = [];
    private string _preferredApiDomain;
    private string _sessionCookie = string.Empty;
    private JmAccountInfo? _currentAccount;

    public JmComicService(HttpClient httpClient, JmSiteOptions? options = null, ILogger<JmComicService>? logger = null)
    {
        _options = options ?? JmSiteOptions.Default;
        _preferredApiDomain = _options.ApiDomains[0];
        _httpClient = httpClient;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<JmComicService>.Instance;
        _httpClient.Timeout = TimeSpan.FromSeconds(40);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        }
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    }

    public IReadOnlyDictionary<string, string> GetRankingSections() => RankingSections;

    public JmAccountState GetAccountState()
    {
        lock (_stateLock)
        {
            return new JmAccountState
            {
                IsLoggedIn = _currentAccount is not null && !string.IsNullOrWhiteSpace(_sessionCookie),
                Account = _currentAccount is null ? null : CloneAccount(_currentAccount),
            };
        }
    }

    public async Task<JmAccountInfo> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        username = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("请输入 JM 用户名或邮箱");
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("请输入 JM 密码");

        var result = await RequestApiWithMetadataAsync(
            "/login",
            new Dictionary<string, string>(),
            cancellationToken,
            HttpMethod.Post,
            new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password,
            },
            includeSession: false);

        var payload = result.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JM 登录响应结构无效");

        var sessionToken = GetString(payload, "s");
        if (string.IsNullOrWhiteSpace(sessionToken))
            throw new InvalidDataException("JM 登录成功但未返回会话令牌");

        var account = new JmAccountInfo
        {
            UserId = GetString(payload, "uid"),
            Username = GetString(payload, "username") is { Length: > 0 } returnedName ? returnedName : username,
            Email = GetString(payload, "email"),
            AvatarUrl = GetString(payload, "photo"),
            LevelName = GetString(payload, "level_name"),
            Coin = ReadIntProperty(payload, "coin"),
            FavoriteCount = ReadIntProperty(payload, "album_favorites"),
        };

        var cookies = new Dictionary<string, string>(result.Cookies, StringComparer.OrdinalIgnoreCase)
        {
            ["AVS"] = sessionToken,
        };
        lock (_stateLock)
        {
            _sessionCookie = string.Join("; ", cookies.Select(pair => $"{pair.Key}={pair.Value}"));
            _currentAccount = CloneAccount(account);
            _albumCache.Clear();
        }
        return account;
    }

    public void Logout()
    {
        lock (_stateLock)
        {
            _sessionCookie = string.Empty;
            _currentAccount = null;
            _albumCache.Clear();
        }
    }

    public async Task<JmFavoriteResponse> GetFavoritesAsync(
        int page = 1,
        string folderId = "0",
        string sort = "mr",
        CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        var safePage = Math.Max(page, 1);
        var payload = await RequestApiAsync(
            "/favorite",
            new Dictionary<string, string>
            {
                ["page"] = safePage.ToString(),
                ["folder_id"] = string.IsNullOrWhiteSpace(folderId) ? "0" : folderId,
                ["o"] = string.IsNullOrWhiteSpace(sort) ? "mr" : sort,
            },
            cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JM 收藏夹响应结构无效");

        var items = payload.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(BuildSearchResult).ToList()
            : [];
        var folders = new List<JmFavoriteFolder>();
        if (payload.TryGetProperty("folder_list", out var folderList) && folderList.ValueKind == JsonValueKind.Array)
        {
            folders.AddRange(folderList.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new JmFavoriteFolder
                {
                    Id = GetString(item, "FID"),
                    Name = ChineseTextConverter.ToSimplified(GetString(item, "name")),
                })
                .Where(folder => !string.IsNullOrWhiteSpace(folder.Id)));
        }
        return new JmFavoriteResponse
        {
            Items = items,
            Folders = folders,
            Total = ReadIntProperty(payload, "total"),
            Page = safePage,
            PageSize = Math.Max(ReadIntProperty(payload, "count"), items.Count),
        };
    }

    public async Task<JmFavoriteMutationResult> SetJmFavoriteAsync(
        string albumId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        albumId = (albumId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(albumId)) throw new ArgumentException("漫画编号不能为空");
        var payload = await RequestApiAsync(
            "/favorite",
            // JM 的收藏接口是 POST 切换操作，服务端根据当前状态自动收藏或取消收藏。
            new Dictionary<string, string>(),
            cancellationToken,
            HttpMethod.Post,
            new Dictionary<string, string> { ["aid"] = albumId });
        var status = GetString(payload, "status");
        var success = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                      status == "1";
        if (!success)
        {
            var message = ChineseTextConverter.ToSimplified(GetString(payload, "msg"));
            if (string.IsNullOrWhiteSpace(message)) message = ChineseTextConverter.ToSimplified(GetString(payload, "message"));
            var responsePreview = payload.GetRawText();
            if (responsePreview.Length > 500) responsePreview = responsePreview[..500] + "...";
            _logger.LogWarning("JM 收藏切换返回未识别结果: {Payload}", responsePreview);
            throw new InvalidDataException(string.IsNullOrWhiteSpace(message)
                ? $"JM {(isFavorite ? "收藏" : "取消收藏")}操作未成功"
                : message);
        }
        lock (_stateLock) _albumCache.Remove(albumId);
        return new JmFavoriteMutationResult
        {
            Success = true,
            Message = ChineseTextConverter.ToSimplified(GetString(payload, "msg")),
        };
    }

    public async Task<JmFavoriteMutationResult> ManageFavoriteFolderAsync(
        JmFavoriteFolderOperation operation,
        string folderId = "",
        string folderName = "",
        string albumId = "",
        CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        folderId = (folderId ?? string.Empty).Trim();
        folderName = (folderName ?? string.Empty).Trim();
        albumId = (albumId ?? string.Empty).Trim();
        if (operation == JmFavoriteFolderOperation.Add && string.IsNullOrWhiteSpace(folderId)) folderId = "0";

        var type = operation switch
        {
            JmFavoriteFolderOperation.Add => "add",
            JmFavoriteFolderOperation.Edit => "edit",
            JmFavoriteFolderOperation.Move => "move",
            JmFavoriteFolderOperation.Delete => "del",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        if (operation == JmFavoriteFolderOperation.Add && string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("收藏夹名称不能为空", nameof(folderName));
        if (operation is JmFavoriteFolderOperation.Edit or JmFavoriteFolderOperation.Delete &&
            (string.IsNullOrWhiteSpace(folderId) || folderId == "0"))
            throw new ArgumentException("不能修改或删除默认收藏夹", nameof(folderId));
        if (operation == JmFavoriteFolderOperation.Edit && string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("收藏夹名称不能为空", nameof(folderName));
        if (operation == JmFavoriteFolderOperation.Move && string.IsNullOrWhiteSpace(albumId))
            throw new ArgumentException("漫画编号不能为空", nameof(albumId));

        var form = new Dictionary<string, string> { ["type"] = type };
        if (!string.IsNullOrWhiteSpace(folderId)) form["folder_id"] = folderId;
        if (!string.IsNullOrWhiteSpace(folderName)) form["folder_name"] = folderName;
        if (!string.IsNullOrWhiteSpace(albumId)) form["aid"] = albumId;
        var payload = await RequestApiAsync(
            "/favorite_folder",
            new Dictionary<string, string>(),
            cancellationToken,
            HttpMethod.Post,
            form);

        var status = GetString(payload, "status");
        var message = ChineseTextConverter.ToSimplified(GetString(payload, "msg"));
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(string.IsNullOrWhiteSpace(message) ? "JM 收藏夹操作未成功" : message);

        if (!string.IsNullOrWhiteSpace(albumId))
        {
            lock (_stateLock) _albumCache.Remove(albumId);
        }
        return new JmFavoriteMutationResult { Success = true, Message = message };
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<SearchResponse> SearchAsync(
        string keyword,
        int page = 1,
        string sort = "mr",
        CancellationToken cancellationToken = default)
    {
        var payload = await RequestApiAsync(
            "/search",
            new Dictionary<string, string>
            {
                ["main_tag"] = "0",
                ["search_query"] = (keyword ?? string.Empty).Trim(),
                ["page"] = Math.Max(page, 1).ToString(),
                ["o"] = sort,
            },
            cancellationToken);

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("禁漫天堂搜索结果结构无效");
        }

        var items = new List<JsonElement>();
        if (payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(content.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object));
        }
        else
        {
            var redirectId = GetString(payload, "redirect_aid");
            if (!string.IsNullOrWhiteSpace(redirectId))
            {
                items.Add(await GetAlbumAsync(redirectId, cancellationToken));
            }
        }

        var results = items
            .Where(item => !string.IsNullOrWhiteSpace(GetString(item, "id")))
            .Select(item => BuildSearchResult(item))
            .ToList();

        return new SearchResponse { Items = results, Total = results.Count };
    }

    public async Task<RankingResponse> GetRankingAsync(
        string section,
        int page,
        CancellationToken cancellationToken = default)
    {
        var selectedSection = RankingSections.ContainsKey(section)
            ? section
            : RankingSections.Keys.First();
        var safePage = Math.Max(page, 1);
        // JM 已不再把空关键词搜索当作分类浏览，/search 会返回 total=0。
        // 排行榜本身属于分类接口，按全部分类(c=0)和对应排序读取。
        var payload = await RequestApiAsync(
            "/categories/filter",
            new Dictionary<string, string>
            {
                ["page"] = safePage.ToString(),
                ["order"] = string.Empty,
                ["c"] = "0",
                ["o"] = RankingSections[selectedSection],
            },
            cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("禁漫天堂排行榜结果结构无效");
        }

        var items = payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object &&
                               !string.IsNullOrWhiteSpace(GetString(item, "id")))
                .Select(BuildSearchResult)
                .ToList()
            : [];
        return new RankingResponse
        {
            Items = items.Select(item => new RankingItem
            {
                Title = item.Title,
                Url = item.Url,
                CoverUrl = item.CoverUrl,
                LatestChapter = item.LatestChapter,
                Author = item.Author,
                UpdateTime = item.UpdateTime,
                Section = selectedSection,
                DetailSectionLabel = "站点: 禁漫天堂",
                Categories = item.Categories,
            }).ToList(),
            Total = Math.Max(ReadIntProperty(payload, "total"), items.Count),
            Section = selectedSection,
            AvailableSections = new Dictionary<string, string>(RankingSections),
            IsSinglePage = false,
        };
    }

    public async Task<WeeklyPicksIndexResponse> GetWeeklyPicksIndexAsync(
        CancellationToken cancellationToken = default)
    {
        var payload = await RequestApiAsync(
            "/week",
            new Dictionary<string, string>(),
            cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("JM 每周必看索引结构无效");
        }

        var issues = new List<WeeklyPickIssue>();
        if (payload.TryGetProperty("categories", out var categories) &&
            categories.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in categories.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var title = GetString(item, "time");
                if (string.IsNullOrWhiteSpace(title)) title = GetString(item, "name");
                issues.Add(new WeeklyPickIssue
                {
                    Id = id,
                    Title = string.IsNullOrWhiteSpace(title) ? $"第 {id} 期" : title,
                });
            }
        }

        var types = new List<WeeklyPickType>();
        if (payload.TryGetProperty("type", out var typeItems) &&
            typeItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeItems.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var title = GetString(item, "title");
                if (string.IsNullOrWhiteSpace(title)) title = GetString(item, "name");
                types.Add(new WeeklyPickType
                {
                    Id = id,
                    Title = NormalizeWeeklyPickTypeTitle(id, title),
                });
            }
        }

        return new WeeklyPicksIndexResponse
        {
            Issues = issues
                .OrderByDescending(issue => long.TryParse(issue.Id, out var id) ? id : long.MinValue)
                .ToList(),
            Types = types,
        };
    }

    public Task<WeeklyPicksResponse> GetWeeklyPicksAsync(
        string issueId,
        CancellationToken cancellationToken = default) =>
        GetWeeklyPicksAsync(issueId, string.Empty, cancellationToken);

    public async Task<WeeklyPicksResponse> GetWeeklyPicksAsync(
        string issueId,
        string typeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueId))
            throw new ArgumentException("每周必看期数不能为空", nameof(issueId));

        var parameters = new Dictionary<string, string> { ["id"] = issueId.Trim() };
        if (!string.IsNullOrWhiteSpace(typeId)) parameters["type"] = typeId.Trim();
        var payload = await RequestApiAsync("/week/filter", parameters, cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("JM 每周必看作品结构无效");
        }

        var items = new List<WeeklyPickItem>();
        if (payload.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                var searchItem = BuildSearchResult(item);
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var categoryKeys = ReadFlatStrings(item, "category")
                    .Concat(ReadFlatStrings(item, "category_sub"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                items.Add(new WeeklyPickItem
                {
                    Id = id,
                    Title = searchItem.Title,
                    Url = searchItem.Url,
                    CoverUrl = searchItem.CoverUrl,
                    Author = searchItem.Author,
                    Description = SanitizePlainText(GetString(item, "description")),
                    UpdateTime = searchItem.UpdateTime,
                    CategoryKeys = categoryKeys,
                    Categories = searchItem.Categories,
                });
            }
        }

        var total = payload.TryGetProperty("total", out var totalElement)
            ? ReadInt(totalElement)
            : items.Count;
        return new WeeklyPicksResponse
        {
            IssueId = issueId.Trim(),
            Total = Math.Max(total, items.Count),
            Items = items,
        };
    }

    public async Task<MangaCommentsResponse> GetAlbumCommentsAsync(
        string mangaInput,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var (mangaId, _) = ParseMangaId(mangaInput);
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            throw new ArgumentException("无法识别漫画编号，不能加载评论");
        }

        var safePage = Math.Max(page, 1);
        var payload = await RequestApiAsync(
            "/forum",
            new Dictionary<string, string>
            {
                ["mode"] = "all",
                ["page"] = safePage.ToString(),
                ["aid"] = mangaId,
            },
            cancellationToken);

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("禁漫天堂评论结果结构无效");
        }

        var comments = new List<MangaCommentDto>();
        if (payload.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            comments.AddRange(list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => ParseComment(item, 0)));
        }

        var total = payload.TryGetProperty("total", out var totalElement)
            ? ReadInt(totalElement)
            : comments.Count;
        return new MangaCommentsResponse
        {
            Items = comments,
            Total = Math.Max(total, comments.Count),
            Page = safePage,
        };
    }

    public async Task<MangaResolveResponse> ResolveAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var (mangaId, _) = ParseMangaId(url);
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            throw new ArgumentException("禁漫天堂无法识别该漫画链接");
        }

        var info = await GetMangaInfoAsync(mangaId, cancellationToken);
        return new MangaResolveResponse
        {
            MangaId = info.Id,
            Title = info.Title,
            SiteName = SiteCatalog.DisplayName,
            MangaUrl = AlbumUrl(mangaId),
            LatestChapter = info.Chapters.LastOrDefault()?.Title ?? "-",
            CoverUrl = CoverUrl(mangaId),
            DetailHint = BuildDetailHint(info),
            Author = string.Join("、", info.Authors.Select(ChineseTextConverter.ToSimplified)),
            Description = ChineseTextConverter.ToSimplified(SanitizePlainText(info.Description)),
            Tags = info.Tags.Select(ChineseTextConverter.ToSimplified).ToList(),
            AddedAt = FormatTimestamp(info.AddedAt),
            TotalViews = info.TotalViews,
            Likes = info.Likes,
            CommentCount = info.CommentCount,
            IsFavorite = info.IsFavorite,
            Chapters = info.Chapters.Select(chapter => new MangaChapterDto
            {
                Order = chapter.Order,
                Title = chapter.Title,
                Url = $"https://{_options.PublicSiteDomain}/photo/{chapter.Id}",
            }).ToList(),
        };
    }

    public async Task<JmMangaInfo> GetMangaInfoFromUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var (mangaId, startChapterId) = ParseMangaId(url);
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            throw new ArgumentException("无法解析禁漫天堂漫画链接");
        }
        var info = await GetMangaInfoAsync(mangaId, cancellationToken);
        return info with { StartChapterId = startChapterId };
    }

    public async Task<JmChapterDownloadResult> DownloadChapterAsync(
        JmChapter chapter,
        string rootDirectory,
        int maxConcurrentImages,
        CancellationToken cancellationToken = default,
        Action<JmImageProgress>? progress = null,
        Func<CancellationToken, Task>? waitBeforeImage = null,
        Func<int, CancellationToken, Task>? throttleBytes = null,
        string? preferredDirectoryName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!int.TryParse(chapter.Id, out var chapterId))
        {
            throw new ArgumentException("禁漫天堂章节编号无效");
        }

        var payload = await RequestApiAsync(
            "/chapter",
            new Dictionary<string, string> { ["id"] = chapter.Id },
            cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("禁漫天堂章节结构无效");
        }

        var imageNames = GetStringArray(payload, "images")
            .Where(IsSupportedImageName)
            .ToList();
        if (imageNames.Count == 0)
        {
            throw new InvalidDataException($"禁漫天堂章节 {chapter.Id} 没有可下载图片");
        }

        var scrambleId = await GetScrambleIdAsync(chapterId, cancellationToken);
        var chapterTitle = ResolveChapterTitle(payload, chapter);
        var chapterDirectoryName = string.IsNullOrWhiteSpace(preferredDirectoryName)
            ? $"{chapter.Order:000}_{SanitizeFileName(chapterTitle)}"
            : SanitizeFileName(preferredDirectoryName);
        var finalDirectory = Path.Combine(rootDirectory, chapterDirectoryName);
        var temporaryDirectory = Path.Combine(rootDirectory, TempChapterPrefix + chapterDirectoryName);

        var finalTasks = BuildImageTasks(imageNames, chapterId, scrambleId, finalDirectory);
        if (finalTasks.All(task => File.Exists(task.Destination) && new FileInfo(task.Destination).Length > 0))
        {
            progress?.Invoke(new JmImageProgress(finalTasks.Count, finalTasks.Count, 0));
            return new JmChapterDownloadResult(finalTasks.Count, chapterDirectoryName, chapterTitle);
        }

        var activeDirectory = Directory.Exists(finalDirectory) ? finalDirectory : temporaryDirectory;
        Directory.CreateDirectory(activeDirectory);
        var tasks = BuildImageTasks(imageNames, chapterId, scrambleId, activeDirectory);
        var pending = tasks
            .Where(task => !File.Exists(task.Destination) || new FileInfo(task.Destination).Length == 0)
            .ToList();
        var successCount = tasks.Count - pending.Count;
        long downloadedBytes = 0;
        progress?.Invoke(new JmImageProgress(successCount, tasks.Count, downloadedBytes));
        using var gate = new SemaphoreSlim(Math.Max(maxConcurrentImages, 1));

        var downloads = pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var succeeded = await DownloadImageAsync(
                    item,
                    cancellationToken,
                    bytes =>
                    {
                        var totalBytes = Interlocked.Add(ref downloadedBytes, bytes);
                        progress?.Invoke(new JmImageProgress(
                            Volatile.Read(ref successCount),
                            tasks.Count,
                            totalBytes));
                    },
                    waitBeforeImage,
                    throttleBytes);
                if (succeeded)
                {
                    var completed = Interlocked.Increment(ref successCount);
                    progress?.Invoke(new JmImageProgress(completed, tasks.Count, Volatile.Read(ref downloadedBytes)));
                }
                return succeeded;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(downloads);

        cancellationToken.ThrowIfCancellationRequested();
        if (successCount < tasks.Count)
        {
            throw new IOException($"禁漫天堂图片下载不完整: {successCount}/{tasks.Count}");
        }

        if (string.Equals(activeDirectory, temporaryDirectory, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(finalDirectory))
        {
            Directory.Move(temporaryDirectory, finalDirectory);
        }

        return new JmChapterDownloadResult(successCount, chapterDirectoryName, chapterTitle);
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        _ = await SearchAsync(string.Empty, 1, "mr", cancellationToken);
    }

    /// <summary>
    /// 获取章节的全部图片源(URL + 乱序还原规则),不写盘,供在线阅读使用。
    /// </summary>
    public async Task<IReadOnlyList<JmImageSource>> GetChapterImageSourcesAsync(
        int chapterId,
        CancellationToken cancellationToken = default)
    {
        var payload = await RequestApiAsync(
            "/chapter",
            new Dictionary<string, string> { ["id"] = chapterId.ToString() },
            cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("禁漫天堂章节结构无效");
        }

        var imageNames = GetStringArray(payload, "images")
            .Where(IsSupportedImageName)
            .ToList();
        if (imageNames.Count == 0)
        {
            throw new InvalidDataException($"禁漫天堂章节 {chapterId} 没有可读取图片");
        }

        var scrambleId = await GetScrambleIdAsync(chapterId, cancellationToken);
        var sources = new List<JmImageSource>(imageNames.Count);
        foreach (var imageName in imageNames)
        {
            var extension = Path.GetExtension(imageName).TrimStart('.').ToLowerInvariant();
            var blockCount = extension == "webp"
                ? CalculateBlockCount(scrambleId, chapterId, Path.GetFileNameWithoutExtension(imageName))
                : 0;
            sources.Add(new JmImageSource(
                $"https://{_options.ImageDomain}/media/photos/{chapterId}/{imageName}",
                blockCount));
        }
        return sources;
    }

    /// <summary>
    /// 下载并还原单张图片,返回内存字节,不写盘。带 3 次重试,供在线阅读使用。
    /// </summary>
    public async Task<byte[]> FetchChapterImageAsync(
        JmImageSource source,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var uri = attempt == 0
                    ? source.Url
                    : source.Url + "?ts=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
                    if (read == 0) break;
                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                }
                var imageData = buffer.ToArray();
                if (imageData.Length == 0) throw new InvalidDataException("图片内容为空");

                return source.BlockCount > 0
                    ? await RestoreScrambledImageAsync(imageData, source.BlockCount)
                    : imageData;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < 2)
                {
                    _logger.LogWarning("在线图片下载失败，准备重试（{Attempt}/2）：{Url}", attempt + 1, source.Url);
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
                }
            }
        }
        throw new HttpRequestException($"图片下载失败: {lastError?.Message}");
    }

    public static (string? MangaId, string? StartChapterId) ParseMangaId(string input)
    {
        var normalized = (input ?? string.Empty).Trim();
        if (normalized.All(char.IsDigit) && normalized.Length > 0)
        {
            return (normalized, null);
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return (null, null);
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            (parts[0].Equals("album", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("comic", StringComparison.OrdinalIgnoreCase)) &&
            parts[1].All(char.IsDigit))
        {
            return (parts[1], null);
        }

        if (parts.Length > 0 && parts[^1].All(char.IsDigit))
        {
            var id = parts[^1];
            var isChapter = parts.Take(parts.Length - 1).Any(part =>
                part.Equals("photo", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("chapter", StringComparison.OrdinalIgnoreCase));
            return (id, isChapter ? id : null);
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = pair.Split('=', 2);
            if (pieces.Length == 2 && pieces[0].Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                var id = Uri.UnescapeDataString(pieces[1]);
                return id.All(char.IsDigit) && id.Length > 0 ? (id, null) : (null, null);
            }
        }
        return (null, null);
    }

    public static int CalculateBlockCount(int scrambleId, int chapterId, string fileNameStem)
    {
        if (chapterId < scrambleId) return 0;
        if (chapterId < 268_850) return 10;
        var divisor = chapterId < 421_926 ? 10 : 8;
        var digest = Md5Hex($"{chapterId}{fileNameStem}");
        return (digest[^1] % divisor) * 2 + 2;
    }

    // dataSecret 为 null 时回退到 JmSiteOptions.Default(保持既有调用方与测试可用);
    // 实例调用一律传入自身 _options.AppDataSecret,否则注入的自定义站点配置会被静默忽略。
    public static byte[] DecryptPayload(long timestamp, string encryptedData, string? dataSecret = null)
    {
        var encrypted = Convert.FromBase64String(encryptedData);
        if (encrypted.Length == 0 || encrypted.Length % 16 != 0)
        {
            throw new InvalidDataException("JM API 返回的密文长度无效");
        }

        var secret = string.IsNullOrEmpty(dataSecret) ? JmSiteOptions.Default.AppDataSecret : dataSecret;
        var key = Encoding.ASCII.GetBytes(Md5Hex($"{timestamp}{secret}"));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        var paddingLength = decrypted[^1];
        if (paddingLength is < 1 or > 16 ||
            decrypted.AsSpan(decrypted.Length - paddingLength).ToArray().Any(value => value != paddingLength))
        {
            throw new InvalidDataException("JM API 返回的 PKCS#7 填充无效");
        }
        return decrypted[..^paddingLength];
    }

    public static byte[] ReorderVerticalBlocks(byte[] source, int width, int height, int blockCount)
    {
        if (blockCount <= 0) return source.ToArray();
        var stride = checked(width * 4);
        if (source.Length != checked(stride * height))
        {
            throw new ArgumentException("像素缓冲区大小无效", nameof(source));
        }

        var destination = new byte[source.Length];
        var baseHeight = height / blockCount;
        var remainderHeight = height % blockCount;
        for (var index = 0; index < blockCount; index++)
        {
            var blockHeight = baseHeight;
            var sourceY = height - baseHeight * (index + 1) - remainderHeight;
            var destinationY = baseHeight * index;
            if (index == 0)
            {
                blockHeight += remainderHeight;
            }
            else
            {
                destinationY += remainderHeight;
            }
            System.Buffer.BlockCopy(source, sourceY * stride, destination, destinationY * stride, blockHeight * stride);
        }
        return destination;
    }

    private async Task<JmMangaInfo> GetMangaInfoAsync(string mangaId, CancellationToken cancellationToken)
    {
        var album = await GetAlbumAsync(mangaId, cancellationToken);
        var chapters = BuildChapters(album);
        return new JmMangaInfo(
            mangaId,
            GetString(album, "name") is { Length: > 0 } title ? title : mangaId,
            CoverUrl(mangaId),
            chapters,
            null,
            GetString(album, "addtime"),
            GetAuthors(album),
            GetString(album, "description"),
            GetStringArray(album, "tags"),
            GetString(album, "total_views"),
            GetString(album, "likes"),
            GetString(album, "comment_total"),
            ReadBooleanProperty(album, "is_favorite"));
    }

    private async Task<JsonElement> GetAlbumAsync(string mangaId, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_albumCache.TryGetValue(mangaId, out var cached) &&
                DateTimeOffset.UtcNow - cached.SavedAt < TimeSpan.FromMinutes(2))
            {
                return cached.Payload.Clone();
            }
        }

        var payload = await RequestApiAsync(
            "/album",
            new Dictionary<string, string> { ["id"] = mangaId },
            cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("禁漫天堂漫画详情结构无效");
        }

        lock (_stateLock)
        {
            _albumCache[mangaId] = (DateTimeOffset.UtcNow, payload.Clone());
            if (_albumCache.Count > 256)
            {
                var oldest = _albumCache.MinBy(entry => entry.Value.SavedAt).Key;
                _albumCache.Remove(oldest);
            }
        }
        return payload;
    }

    private async Task<JsonElement> RequestApiAsync(
        string path,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken,
        HttpMethod? method = null,
        IReadOnlyDictionary<string, string>? form = null,
        bool includeSession = true)
    {
        var result = await RequestApiWithMetadataAsync(
            path, parameters, cancellationToken, method, form, includeSession);
        return result.Payload;
    }

    private async Task<ApiRequestResult> RequestApiWithMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken,
        HttpMethod? method = null,
        IReadOnlyDictionary<string, string>? form = null,
        bool includeSession = true)
    {
        var errors = new List<string>();
        foreach (var domain in GetOrderedDomains())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var domainTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            domainTimeout.CancelAfter(_options.ApiRequestTimeout);
            var domainToken = domainTimeout.Token;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var uri = BuildUri($"https://{domain}{path}", parameters);
            try
            {
                using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
                if (form is not null) request.Content = new FormUrlEncodedContent(form);
                AddTokenHeaders(request, timestamp, false);
                if (includeSession)
                {
                    string cookie;
                    lock (_stateLock) cookie = _sessionCookie;
                    if (!string.IsNullOrWhiteSpace(cookie))
                        request.Headers.TryAddWithoutValidation("Cookie", cookie);
                }
                using var response = await _httpClient.SendAsync(request, domainToken);
                response.EnsureSuccessStatusCode();
                using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(domainToken));
                var root = envelope.RootElement;
                if (!root.TryGetProperty("code", out var code) || ReadInt(code) != 200)
                {
                    var message = GetString(root, "errorMsg");
                    if (string.IsNullOrWhiteSpace(message)) message = GetString(root, "error_msg");
                    throw new InvalidDataException($"code={ReadInt(code)}: {message}");
                }
                var encrypted = GetString(root, "data");
                if (string.IsNullOrWhiteSpace(encrypted))
                {
                    throw new InvalidDataException("data 字段不是加密字符串");
                }
                var plaintext = DecryptPayload(timestamp, encrypted, _options.AppDataSecret);
                using var payload = JsonDocument.Parse(plaintext);
                PromoteDomain(domain);
                return new ApiRequestResult(payload.RootElement.Clone(), ReadResponseCookies(response));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var message = $"等待超过 {_options.ApiRequestTimeout.TotalSeconds:0.#} 秒";
                _logger.LogWarning(ex, "禁漫天堂 API 域名 {Domain} 响应超时，尝试备用域名", domain);
                errors.Add($"{domain}: {message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "禁漫天堂 API 域名 {Domain} 请求失败", domain);
                errors.Add($"{domain}: {ex.Message}");
            }
        }
        throw new HttpRequestException($"禁漫天堂 API 请求失败: {string.Join(" | ", errors.TakeLast(3))}");
    }

    private void EnsureLoggedIn()
    {
        lock (_stateLock)
        {
            if (_currentAccount is null || string.IsNullOrWhiteSpace(_sessionCookie))
                throw new InvalidOperationException("请先在设置中登录 JM 账号");
        }
    }

    private static Dictionary<string, string> ReadResponseCookies(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return result;
        foreach (var value in values)
        {
            var pair = value.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            var name = pair[..separator].Trim();
            var cookieValue = pair[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name)) result[name] = cookieValue;
        }
        return result;
    }

    private static JmAccountInfo CloneAccount(JmAccountInfo account) => new()
    {
        UserId = account.UserId,
        Username = account.Username,
        Email = account.Email,
        AvatarUrl = account.AvatarUrl,
        LevelName = account.LevelName,
        Coin = account.Coin,
        FavoriteCount = account.FavoriteCount,
    };

    private static int ReadIntProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ReadInt(value) : 0;

    private static bool ReadBooleanProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => ReadInt(value) != 0,
            JsonValueKind.String => value.GetString() is { } text &&
                (text == "1" || bool.TryParse(text, out var parsed) && parsed),
            _ => false,
        };
    }

    private async Task<int> GetScrambleIdAsync(int chapterId, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["id"] = chapterId.ToString(),
            ["v"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["mode"] = "vertical",
            ["page"] = "0",
            ["app_img_shunt"] = "1",
            ["express"] = "off",
        };
        var errors = new List<string>();
        foreach (var domain in GetOrderedDomains())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    BuildUri($"https://{domain}/chapter_view_template", parameters));
                AddTokenHeaders(request, timestamp, true);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                var match = ScrambleRegex.Match(html);
                PromoteDomain(domain);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var value))
                {
                    return value;
                }

                // 解析不到 scramble_id 时只能回退默认值,但错误的分块数会让图片还原成乱块,
                // 且下载流程仍会“成功”。这里必须留下告警,否则站点改版后无从排查。
                _logger.LogWarning(
                    "未能从 {Domain} 的章节页解析 scramble_id(章节 {ChapterId}),回退默认值 {DefaultScrambleId}。" +
                    "站点模板可能已改版,图片可能无法正确还原。",
                    domain,
                    chapterId,
                    _options.DefaultScrambleId);
                return _options.DefaultScrambleId;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "禁漫天堂 API 域名 {Domain} 请求失败", domain);
                errors.Add($"{domain}: {ex.Message}");
            }
        }
        throw new HttpRequestException($"禁漫天堂图片规则请求失败: {string.Join(" | ", errors.TakeLast(3))}");
    }

    private async Task<bool> DownloadImageAsync(
        ImageDownload item,
        CancellationToken cancellationToken,
        Action<int>? bytesDownloaded = null,
        Func<CancellationToken, Task>? waitBeforeAttempt = null,
        Func<int, CancellationToken, Task>? throttleBytes = null)
    {
        if (File.Exists(item.Destination) && new FileInfo(item.Destination).Length > 0) return true;
        var partialPath = item.Destination + ".part";
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (waitBeforeAttempt is not null)
                {
                    await waitBeforeAttempt(cancellationToken);
                }
                try
                {
                    var uri = attempt == 0
                        ? item.Url
                        : item.Url + "?ts=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");
                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                    using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var imageBuffer = new MemoryStream();
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                        if (read == 0) break;
                        if (throttleBytes is not null) await throttleBytes(read, cancellationToken);
                        await imageBuffer.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        bytesDownloaded?.Invoke(read);
                    }
                    var imageData = imageBuffer.ToArray();
                    if (imageData.Length == 0) throw new InvalidDataException("图片内容为空");

                    var output = item.BlockCount > 0
                        ? await RestoreScrambledImageAsync(imageData, item.BlockCount)
                        : imageData;
                    await File.WriteAllBytesAsync(partialPath, output, cancellationToken);
                    File.Move(partialPath, item.Destination, true);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch when (attempt < 2)
                {
                    _logger.LogWarning("图片下载失败，准备重试（{Attempt}/2）：{Url}", attempt + 1, item.Url);
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
                }
            }
            return false;
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                try { File.Delete(partialPath); } catch { }
            }
        }
    }

    private static async Task<byte[]> RestoreScrambledImageAsync(byte[] imageData, int blockCount)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(imageData);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input);
        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var reordered = ReorderVerticalBlocks(
            pixelProvider.DetachPixelData(),
            checked((int)decoder.PixelWidth),
            checked((int)decoder.PixelHeight),
            blockCount);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            reordered);
        await encoder.FlushAsync();

        var result = new byte[checked((int)output.Size)];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)result.Length);
        reader.ReadBytes(result);
        return result;
    }

    private SearchResultItem BuildSearchResult(JsonElement item)
    {
        var id = GetString(item, "id");
        return new SearchResultItem
        {
            Title = GetString(item, "name") is { Length: > 0 } title ? title : id,
            Url = AlbumUrl(id),
            CoverUrl = CoverUrl(id),
            Author = string.Join("、", GetAuthors(item)),
            UpdateTime = FormatTimestamp(GetString(item, "update_at") is { Length: > 0 } updated
                ? updated
                : GetString(item, "addtime")),
            Categories = ReadContentCategories(item),
        };
    }

    private static List<JmChapter> BuildChapters(JsonElement album)
    {
        var chapters = new List<JmChapter>();
        if (album.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in series.EnumerateArray())
            {
                index++;
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = GetString(item, "name");
                var title = $"第{index}话" + (string.IsNullOrWhiteSpace(name) ? string.Empty : $" {name}");
                chapters.Add(new JmChapter(index, id, title));
            }
        }
        if (chapters.Count == 0)
        {
            var id = GetString(album, "id");
            if (!string.IsNullOrWhiteSpace(id)) chapters.Add(new JmChapter(1, id, "第1话"));
        }
        return chapters;
    }

    private static string ResolveChapterTitle(JsonElement payload, JmChapter fallback)
    {
        if (payload.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in series.EnumerateArray())
            {
                index++;
                if (GetString(item, "id") != fallback.Id) continue;
                var name = GetString(item, "name");
                return $"第{index}话" + (string.IsNullOrWhiteSpace(name) ? string.Empty : $" {name}");
            }
        }
        return fallback.Title;
    }

    private List<ImageDownload> BuildImageTasks(
        IReadOnlyList<string> imageNames,
        int chapterId,
        int scrambleId,
        string directory)
    {
        var tasks = new List<ImageDownload>(imageNames.Count);
        for (var index = 0; index < imageNames.Count; index++)
        {
            var imageName = imageNames[index];
            var extension = Path.GetExtension(imageName).TrimStart('.').ToLowerInvariant();
            var blockCount = extension == "webp"
                ? CalculateBlockCount(scrambleId, chapterId, Path.GetFileNameWithoutExtension(imageName))
                : 0;
            var outputExtension = blockCount > 0 ? "jpg" : extension;
            tasks.Add(new ImageDownload(
                $"https://{_options.ImageDomain}/media/photos/{chapterId}/{imageName}",
                Path.Combine(directory, $"{index + 1:000}.{outputExtension}"),
                blockCount));
        }
        return tasks;
    }

    private string[] GetOrderedDomains()
    {
        lock (_stateLock)
        {
            return [_preferredApiDomain, .. _options.ApiDomains.Where(domain => domain != _preferredApiDomain)];
        }
    }

    private void PromoteDomain(string domain)
    {
        lock (_stateLock) _preferredApiDomain = domain;
    }

    private void AddTokenHeaders(HttpRequestMessage request, long timestamp, bool contentToken)
    {
        var secret = contentToken ? _options.AppTokenSecretContent : _options.AppTokenSecret;
        request.Headers.TryAddWithoutValidation("token", Md5Hex($"{timestamp}{secret}"));
        request.Headers.TryAddWithoutValidation("tokenparam", $"{timestamp},{_options.AppVersion}");
    }

    private static string BuildUri(string baseUrl, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count == 0) return baseUrl;
        return baseUrl + "?" + string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static int ReadInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
        return int.TryParse(element.ValueKind == JsonValueKind.String ? element.GetString() : null, out number)
            ? number
            : 0;
    }

    private static MangaCommentDto ParseComment(JsonElement element, int depth)
    {
        var author = GetString(element, "nickname");
        if (string.IsNullOrWhiteSpace(author)) author = GetString(element, "username");

        var replies = new List<MangaCommentDto>();
        if (depth < 3)
        {
            JsonElement replyElements = default;
            var hasReplies = element.TryGetProperty("replys", out replyElements) ||
                             element.TryGetProperty("replies", out replyElements);
            if (hasReplies && replyElements.ValueKind == JsonValueKind.Array)
            {
                replies.AddRange(replyElements.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => ParseComment(item, depth + 1)));
            }
        }

        var spoilerValue = GetString(element, "spoiler");
        if (string.IsNullOrWhiteSpace(spoilerValue)) spoilerValue = GetString(element, "is_spoiler");
        var createdAt = GetString(element, "addtime");
        if (string.IsNullOrWhiteSpace(createdAt)) createdAt = GetString(element, "created_at");

        return new MangaCommentDto
        {
            Id = GetString(element, "CID") is { Length: > 0 } id ? id : GetString(element, "id"),
            UserId = GetString(element, "UID"),
            Author = ChineseTextConverter.ToSimplified(author),
            Content = SanitizeCommentContent(GetString(element, "content")),
            CreatedAt = FormatTimestamp(createdAt),
            Likes = element.TryGetProperty("likes", out var likes) ? Math.Max(ReadInt(likes), 0) : 0,
            IsSpoiler = spoilerValue is "1" or "2" or "true",
            Replies = replies,
        };
    }

    private static string SanitizeCommentContent(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "（无文字内容）";
        var text = Regex.Replace(value, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*p\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return string.IsNullOrWhiteSpace(text) ? "（无文字内容）" : ChineseTextConverter.ToSimplified(text);
    }

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? string.Empty
            : item.GetRawText()).ToList();
    }

    private static List<string> ReadFlatStrings(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return [];
        var result = new List<string>();
        AddFlatStrings(value, result);
        return result;
    }

    private static List<ContentCategory> ReadContentCategories(JsonElement element)
    {
        var categories = new List<ContentCategory>();
        AddContentCategory(element, "category", categories);
        AddContentCategory(element, "category_sub", categories);
        return categories
            .Where(category => !string.IsNullOrWhiteSpace(category.Title))
            .GroupBy(category => category.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddContentCategory(
        JsonElement element,
        string propertyName,
        List<ContentCategory> categories)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) AddContentCategoryValue(item, categories);
            return;
        }
        AddContentCategoryValue(value, categories);
    }

    private static void AddContentCategoryValue(JsonElement value, List<ContentCategory> categories)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var title = GetString(value, "title");
            if (string.IsNullOrWhiteSpace(title)) title = GetString(value, "name");
            if (string.IsNullOrWhiteSpace(title)) return;
            categories.Add(new ContentCategory
            {
                Id = GetString(value, "id"),
                Title = ChineseTextConverter.ToSimplified(title),
            });
            return;
        }
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return;
        var text = ChineseTextConverter.ToSimplified(value.ToString());
        if (!string.IsNullOrWhiteSpace(text))
        {
            categories.Add(new ContentCategory { Id = text, Title = text });
        }
    }

    private static void AddFlatStrings(JsonElement value, List<string> result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.Number:
                result.Add(value.ToString());
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) AddFlatStrings(item, result);
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    result.Add(property.Name);
                    AddFlatStrings(property.Value, result);
                }
                break;
        }
    }

    private static string SanitizePlainText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decoded = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " "))
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string NormalizeWeeklyPickTypeTitle(string id, string title) =>
        id.ToLowerInvariant() switch
        {
            "hanman" => "韩漫",
            "another" => "其他",
            "manga" => "日漫",
            _ => string.IsNullOrWhiteSpace(title) ? id : title,
        };

    private static List<string> GetAuthors(JsonElement element)
    {
        if (!element.TryGetProperty("author", out var authors)) return [];
        if (authors.ValueKind == JsonValueKind.Array)
            return authors.EnumerateArray().Select(item => item.ToString()).Where(value => value.Length > 0).ToList();
        var author = authors.ToString();
        return string.IsNullOrWhiteSpace(author) ? [] : [author];
    }

    private static bool IsSupportedImageName(string name)
    {
        return new[] { ".webp", ".gif", ".jpg", ".jpeg", ".png" }
            .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildDetailHint(JmMangaInfo info)
    {
        var parts = new List<string> { $"共 {info.Chapters.Count} 章" };
        if (info.Authors.Count > 0) parts.Add("作者: " + string.Join(", ", info.Authors));
        return string.Join("，", parts);
    }

    private static string FormatTimestamp(string value)
    {
        return long.TryParse(value, out var timestamp) && timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd")
            : value;
    }

    public static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim('.', ' ');
        cleaned = TruncatePreservingSurrogates(cleaned, 200);
        if (string.IsNullOrWhiteSpace(cleaned)) return "unnamed";

        // 站点标题是不可信输入。命中 Windows 保留设备名(含带扩展名的 NUL.jpg 之类)
        // 会让 CreateDirectory / File.Create 直接失败,整章下载报一个难以定位的 IO 错误。
        var stem = cleaned;
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    /// <summary>按码元截断但不切开代理对(标题里的 emoji 占两个码元,切一半会得到坏字符)。</summary>
    private static string TruncatePreservingSurrogates(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        var end = maxLength;
        if (char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end].TrimEnd('.', ' ');
    }

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string AlbumUrl(string id) => $"https://{_options.PublicSiteDomain}/album/{id}";
    private string CoverUrl(string id) => $"https://{_options.CoverDomain}/media/albums/{id}.jpg";

    private sealed record ImageDownload(string Url, string Destination, int BlockCount);
    private sealed record ApiRequestResult(JsonElement Payload, IReadOnlyDictionary<string, string> Cookies);
}

public sealed record JmChapter(int Order, string Id, string Title);

public sealed record JmMangaInfo(
    string Id,
    string Title,
    string CoverUrl,
    List<JmChapter> Chapters,
    string? StartChapterId,
    string AddedAt,
    List<string> Authors,
    string Description = "",
    List<string>? RawTags = null,
    string TotalViews = "",
    string Likes = "",
    string CommentCount = "",
    bool IsFavorite = false)
{
    public List<string> Tags => RawTags ?? [];
}

public sealed record JmChapterDownloadResult(int ImageCount, string DirectoryName, string ChapterTitle);
public sealed record JmImageProgress(int CompletedImages, int TotalImages, long DownloadedBytes);

/// <summary>在线阅读的单张图片来源:URL 与乱序还原所需的分块数。</summary>
public sealed record JmImageSource(string Url, int BlockCount);
