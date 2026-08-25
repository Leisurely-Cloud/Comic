using System.Collections.Generic;

namespace Comic.WinUI.Models;

public sealed class WeeklyPicksIndexResponse
{
    public List<WeeklyPickIssue> Issues { get; set; } = [];
    public List<WeeklyPickType> Types { get; set; } = [];
}

public sealed class WeeklyPickIssue
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public sealed class WeeklyPickType
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public sealed class WeeklyPicksResponse
{
    public string IssueId { get; set; } = string.Empty;
    public int Total { get; set; }
    public List<WeeklyPickItem> Items { get; set; } = [];
}

public sealed class WeeklyPickItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UpdateTime { get; set; } = string.Empty;
    public List<string> CategoryKeys { get; set; } = [];
    public List<ContentCategory> Categories { get; set; } = [];
}
