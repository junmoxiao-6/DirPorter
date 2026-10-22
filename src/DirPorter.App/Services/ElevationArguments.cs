using System.Text;

namespace DirPorter.App.Services;

/// <summary>
/// 命令行参数处理：<c>--elevate</c>（请求提权）与 <c>--elevate-relay</c>（提权子进程把输出写回文件）。
///
/// 为什么要"输出回传文件"：
/// 提权进程是通过 ShellExecute("runas") 启动的，**会得到一个新的控制台**，
/// 父进程既不能继承它的标准输出，也拿不到它的输出流。
/// 因此子进程把输出 tee 到临时文件，父进程等它退出后读出并打印 —— 这样脚本里
/// `dirporter.exe link --elevate ...` 依然能看到完整输出与正确退出码。
/// </summary>
public static class ElevationArguments
{
    public const string ElevateSwitch = "--elevate";
    public const string RelaySwitch = "--elevate-relay";

    /// <summary>是否请求提权；<paramref name="rest"/> 为去掉该开关后的参数。</summary>
    public static bool TryTakeElevate(string[] args, out string[] rest)
    {
        if (!args.Contains(ElevateSwitch, StringComparer.OrdinalIgnoreCase))
        {
            rest = args;
            return false;
        }

        rest = args.Where(a => !string.Equals(a, ElevateSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();
        return true;
    }

    /// <summary>是否存在输出回传开关；<paramref name="relayPath"/> 为回传文件路径。</summary>
    public static bool TryTakeRelay(string[] args, out string? relayPath, out string[] rest)
    {
        relayPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], RelaySwitch, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length) break;

            relayPath = args[i + 1];

            var list = new List<string>(args);
            list.RemoveRange(i, 2);
            rest = list.ToArray();
            return true;
        }

        rest = args;
        return false;
    }

    /// <summary>把参数数组拼成命令行字符串（按 Windows 规则加引号）。</summary>
    public static string ToCommandLine(IEnumerable<string> args)
        => string.Join(' ', args.Select(Quote));

    /// <summary>
    /// 按 Windows 命令行解析规则加引号。
    /// 规则要点：仅当含空格/制表/引号时才加引号；反斜杠只在引号前需要成对加倍。
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;

        var sb = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    backslashes++;
                    continue;

                case '"':
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
            }

            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(ch);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
