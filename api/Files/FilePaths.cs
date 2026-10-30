namespace Koi.Api.Files;

/// <summary>
/// 文件路径规范化与根目录包含校验。
/// 所有来自请求的路径都必须先经 <see cref="TryNormalize"/>，再经 <see cref="IsWithinRoots"/>；
/// 根目录配置为 <c>/</c> 时包含校验恒成立，但规范化仍必须执行（拒绝相对路径与控制字符）。
/// </summary>
public static class FilePaths
{
    /// <summary>路径长度上限（Linux PATH_MAX 为 4096）。</summary>
    public const int MaxPathLength = 4096;

    /// <summary>文件名长度上限（Linux NAME_MAX 为 255）。</summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// 规范化绝对路径：拒绝空白、相对路径、NUL、控制字符与超长路径。
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxPathLength)
        {
            return false;
        }

        if (input.Contains('\0') || input.Any(char.IsControl) || !Path.IsPathFullyQualified(input))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(input);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return true;
    }

    /// <summary>判断路径是否落在任一允许根目录内（按目录边界比较，避免 /home/a 命中 /home/ab）。</summary>
    public static bool IsWithinRoots(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (!TryNormalize(root, out var normalizedRoot))
            {
                continue;
            }

            if (normalizedRoot == "/")
            {
                return true;
            }

            var trimmed = normalizedRoot.TrimEnd('/');
            if (string.Equals(path, trimmed, StringComparison.Ordinal) ||
                path.StartsWith(trimmed + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 解析符号链接后的真实路径，用于根目录包含校验。
    /// 路径不存在时向上找到最近的已存在祖先再拼接，避免校验阶段被符号链接绕过。
    /// </summary>
    public static string ResolveRealPath(string path)
    {
        var remaining = new Stack<string>();
        var current = path;

        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var target = File.ResolveLinkTarget(current, returnFinalTarget: true);
                var resolved = target?.FullName ?? current;
                if (remaining.Count == 0)
                {
                    return resolved;
                }

                return Path.GetFullPath(Path.Combine(resolved, Path.Combine(remaining.Reverse().ToArray())));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 路径不存在或不可访问：继续检查父目录。
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            remaining.Push(Path.GetFileName(current));
            current = parent;
        }

        return path;
    }

    /// <summary>校验单个文件名（重命名、上传文件名）：不得包含路径分隔符或控制字符。</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= MaxNameLength &&
        name is not "." and not ".." &&
        !name.Contains('/') &&
        !name.Contains('\0') &&
        !name.Any(char.IsControl);
}
