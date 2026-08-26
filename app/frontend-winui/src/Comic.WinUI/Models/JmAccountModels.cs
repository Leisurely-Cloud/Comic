using System.Collections.Generic;

namespace Comic.WinUI.Models;

public sealed class JmAccountInfo
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string LevelName { get; set; } = string.Empty;
    public int Coin { get; set; }
    public int FavoriteCount { get; set; }
}

public sealed class JmAccountState
{
    public bool IsLoggedIn { get; set; }
    public JmAccountInfo? Account { get; set; }
}

public sealed class JmFavoriteFolder
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "默认收藏夹" : Name;
}

public sealed class JmFavoriteResponse
{
    public List<SearchResultItem> Items { get; set; } = [];
    public List<JmFavoriteFolder> Folders { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 20;
}

public sealed class JmFavoriteMutationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public enum JmFavoriteFolderOperation
{
    Add,
    Edit,
    Move,
    Delete,
}
