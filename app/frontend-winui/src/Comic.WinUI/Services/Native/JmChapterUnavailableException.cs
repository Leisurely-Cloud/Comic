namespace Comic.WinUI.Services.Native;

public sealed class JmChapterUnavailableException(string chapterId)
    : IOException($"源站章节 {chapterId} 暂无图片，当前无法读取或下载。请稍后重试或到源站确认。")
{
    public string ChapterId { get; } = chapterId;
}
