namespace ITest.Core;

/// <summary>Ghi file an toàn: ghi ra file tạm, đẩy xuống đĩa rồi mới thay file chính — mất điện giữa chừng không để lại file ghi dở.</summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    public static void WriteAllBytes(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}

/// <summary>
/// Xếp hàng các lần ghi file ở luồng nền theo đúng thứ tự gọi. Mỗi lần gọi nhận một số thứ tự; bản cũ đến muộn
/// không bao giờ ghi đè bản mới hơn của cùng một file, và mỗi lần ghi đều qua file tạm (<see cref="SafeFile"/>).
/// </summary>
public sealed class OrderedWriter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _written = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;
    private Task _tail = Task.CompletedTask;

    /// <summary>Ghi ở luồng nền. Task trả về true khi bản này (hoặc một bản mới hơn) đã nằm trên đĩa.</summary>
    public Task<bool> Write(string path, string text)
    {
        lock (_lock)
        {
            long seq = ++_seq;
            var t = _tail.ContinueWith(_ => WriteCore(path, text, seq), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _tail = t;
            return t;
        }
    }

    /// <summary>Ghi ngay trên luồng gọi (dùng lúc thoát ứng dụng); vẫn giữ thứ tự với các lần ghi nền.</summary>
    public bool WriteNow(string path, string text)
    {
        long seq;
        lock (_lock) seq = ++_seq;
        return WriteCore(path, text, seq);
    }

    /// <summary>Xoá file, xếp hàng sau các lần ghi đã gọi trước đó (bản ghi đang chờ không "hồi sinh" file vừa xoá).</summary>
    public Task<bool> Delete(string path)
    {
        lock (_lock)
        {
            long seq = ++_seq;
            var t = _tail.ContinueWith(_ =>
            {
                lock (_written)
                {
                    try { File.Delete(path); } catch { return false; }
                    _written[path] = seq;
                    return true;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _tail = t;
            return t;
        }
    }

    private bool WriteCore(string path, string text, long seq)
    {
        lock (_written)
        {
            if (_written.TryGetValue(path, out var last) && last > seq) return true;   // đã có bản mới hơn trên đĩa
            try
            {
                SafeFile.WriteAllText(path, text);
                _written[path] = seq;
                return true;
            }
            catch { return false; }
        }
    }
}
