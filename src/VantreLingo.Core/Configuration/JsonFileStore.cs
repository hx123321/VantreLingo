using System.Text.Json;

namespace VantreLingo.Core.Configuration;

public sealed class JsonFileStore<T>(string path, Func<T> createDefault, Action<T> validate) where T : class
{
    public T Load()
    {
        if (!File.Exists(path))
        {
            var defaults = createDefault();
            validate(defaults);
            return defaults;
        }
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonFormat.Options)
                ?? throw new InvalidDataException("配置不能为空。");
            validate(value);
            return value;
        }
        catch (JsonException)
        {
            // 不把正文、密钥或包含原始 JSON 的解析异常放进日志/错误提示。
            throw new InvalidDataException("配置 JSON 无效；原文件已保留，请修复后重启。");
        }
    }

    public void Save(T value)
    {
        validate(value);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryDirectory = Path.Combine(directory, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryFile = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonFormat.Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryFile, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
                File.Delete(temporaryFile);
        }
    }
}
