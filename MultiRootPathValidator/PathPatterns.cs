using System.Text.RegularExpressions;

namespace Luban.MultiRootPathValidator;

internal sealed class SimpleReplacePattern : IPathPattern
{
    private readonly string _prefix;
    private readonly string _suffix;

    public bool EmptyAble { get; set; }

    public SimpleReplacePattern(string prefix, string suffix)
    {
        _prefix = prefix;
        _suffix = suffix;
    }

    public bool ExistPath(string rootDir, string subFile)
    {
        string finalPath = Path.Combine(rootDir, _prefix + subFile + _suffix);
        return File.Exists(finalPath);
    }
}

internal sealed class UnityAddressablePattern : IPathPattern
{
    public bool EmptyAble { get; set; }

    public bool ExistPath(string rootDir, string subFile)
    {
        return File.Exists(Path.Combine(rootDir, subFile));
    }
}

internal sealed class GodotResourcePattern : IPathPattern
{
    public bool EmptyAble { get; set; }

    public bool ExistPath(string rootDir, string subFile)
    {
        return subFile.StartsWith("res://", StringComparison.Ordinal)
            && File.Exists(Path.Combine(rootDir, subFile[6..]));
    }
}

internal sealed class Ue4ResourcePattern : IPathPattern
{
    private readonly Regex _pat1 = new(@"^/Game/(.+?)(\..+)?$");
    private readonly Regex _pat2 = new(@"^\w+'/Game/(.+?)(\..+)?'$");

    public bool EmptyAble { get; set; }

    private static bool CheckMatch(Match match)
    {
        var groups = match.Groups;
        if (!groups[1].Success)
        {
            return false;
        }

        if (!groups[2].Success)
        {
            return true;
        }

        string path = groups[1].Value;
        string suffix = groups[2].Value[1..];
        if (suffix.EndsWith("_C", StringComparison.Ordinal))
        {
            suffix = suffix[..^2];
        }

        return path.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static bool AlternativePaths(string rawPath)
    {
        return File.Exists($"{rawPath}.uasset") || File.Exists($"{rawPath}.umap");
    }

    public bool ExistPath(string rootDir, string subFile)
    {
        var match1 = _pat1.Match(subFile);
        if (match1.Success)
        {
            return CheckMatch(match1)
                && AlternativePaths(Path.Combine(rootDir, match1.Groups[1].Value));
        }

        var match2 = _pat2.Match(subFile);
        if (match2.Success)
        {
            return CheckMatch(match2)
                && AlternativePaths(Path.Combine(rootDir, match2.Groups[1].Value));
        }

        return false;
    }
}
