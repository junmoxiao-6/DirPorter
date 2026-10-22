using System.IO;
using System.Text;

namespace DirPorter.App.Services;

/// <summary>
/// 把输出同时写到多个目标（控制台 + 回传文件）。
/// 用于提权子进程：用户能在提权控制台看到进度，父进程也能读到完整输出并转发。
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter[] _writers;

    public TeeTextWriter(params TextWriter[] writers) => _writers = writers;

    public override Encoding Encoding => _writers.Length > 0 ? _writers[0].Encoding : Encoding.UTF8;

    public override void Write(char value)
    {
        foreach (var writer in _writers) writer.Write(value);
    }

    public override void Write(string? value)
    {
        foreach (var writer in _writers) writer.Write(value);
    }

    public override void WriteLine(string? value)
    {
        foreach (var writer in _writers) writer.WriteLine(value);
    }

    public override void Flush()
    {
        foreach (var writer in _writers) writer.Flush();
    }
}
