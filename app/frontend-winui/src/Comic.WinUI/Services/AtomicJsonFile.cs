using System.Text.Json;

namespace Comic.WinUI.Services;

internal static class AtomicJsonFile
{
    private static readonly object Gate = new();

    public static T? Read<T>(string path, JsonSerializerOptions? options = null, Func<T, bool>? validate = null) =>
        Read<T>(path, out _, options, validate);

    /// <summary>同时返回通过反序列化及校验的原始内容，保证额外字段来自同一份文件快照。</summary>
    public static T? Read<T>(string path, out string? acceptedJson, JsonSerializerOptions? options = null, Func<T, bool>? validate = null)
    {
        acceptedJson = null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var json = File.ReadAllText(candidate);
                    var value = JsonSerializer.Deserialize<T>(json, options);
                    if (value is not null && (validate is null || validate(value)))
                    {
                        acceptedJson = json;
                        return value;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return default;
    }

    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null) =>
        WriteText(path, JsonSerializer.Serialize(value, options));

    public static void WriteText(string path, string json)
    {
        using var validation = JsonDocument.Parse(json);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    // 损坏的原文件单独保留，不让它覆盖最后一份可读备份。
                    try
                    {
                        using var existing = JsonDocument.Parse(File.ReadAllText(path));
                        File.Replace(temporary, path, path + ".bak");
                    }
                    catch (JsonException)
                    {
                        File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                        File.Move(temporary, path, true);
                    }
                }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
