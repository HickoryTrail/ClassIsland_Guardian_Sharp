using System.Text;

namespace ClassIslandGuardian.Common;

public sealed class FileLog
{
    private readonly string _path;
    private readonly string _source;
    private readonly object _gate = new();

    public FileLog(string path)
    {
        _path = path;
        _source = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Path => _path;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception exception) => Write("ERROR", $"{message}: {exception}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path) ?? ".");
            File.AppendAllText(
                _path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level.ToLowerInvariant()}] [{_source}] {message}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
