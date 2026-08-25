using OpenccNetLib;

namespace Comic.WinUI.Services.Native;

/// <summary>使用 OpenCC 词典进行繁体到简体的字符与词汇转换。</summary>
internal static class ChineseTextConverter
{
    private static readonly Opencc TraditionalToSimplified = new("t2s");

    public static string ToSimplified(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            return TraditionalToSimplified.Convert(value);
        }
        catch
        {
            // 转换组件异常不能阻断评论加载或 CBZ 导出。
            return value;
        }
    }
}
