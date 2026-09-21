using Luban.Datas;
using Luban.Defs;
using Luban.Types;
using Luban.Utils;
using Luban.Validator;

namespace Luban.MultiRootPathValidator;

/// <summary>
/// Drop-in replacement for Luban's built-in "path" validator.
/// Supports multiple validation roots and succeeds when the file exists under any root.
/// </summary>
[Validator("path", Priority = 100)]
public sealed class MultiRootPathValidator : DataValidatorBase
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const string OptionFamily = "pathValidator";
    private const string RootDirsOption = "rootDirs";
    private const string RootDirOption = "rootDir";

    private readonly string[] _rootDirs;

    private string _rawPattern;
    private IPathPattern _pathPattern;

    public MultiRootPathValidator()
    {
        string roots = null;

        // Prefer the explicit plural option. Fall back to Luban's original option
        // so existing launch scripts continue to work.
        if (!EnvManager.Current.TryGetOption(OptionFamily, RootDirsOption, false, out roots))
        {
            EnvManager.Current.TryGetOption(OptionFamily, RootDirOption, false, out roots);
        }

        _rootDirs = ParseRootDirs(roots);

        if (_rootDirs.Length == 0)
        {
            string key = $"{OptionFamily}.{RootDirsOption}";
            if (GenerationContext.Current.GetOrAddUniqueObject(key, () => this) == this)
            {
                Logger.Warn(
                    "option '-x {0}=<dir1;dir2;...>' (or '-x {1}.{2}=<dir>') not found, path validation is disabled",
                    key,
                    OptionFamily,
                    RootDirOption);
            }
        }
    }

    public override void Compile(DefField field, TType type)
    {
        _rawPattern = DefUtil.TrimBracePairs(Args);

        if (type is not TString)
        {
            ThrowCompileError(field, "只支持string类型");
        }

        string[] parts = _rawPattern.Split(';');
        if (parts.Length < 1)
        {
            ThrowCompileError(field, "");
        }

        string patternType = parts[0];
        bool emptyAble = false;
        if (patternType.EndsWith('?'))
        {
            patternType = patternType[..^1];
            emptyAble = true;
        }

        switch (patternType)
        {
            case "normal":
            {
                if (parts.Length != 2)
                {
                    ThrowCompileError(field, "normal模式格式应为 normal;<pattern>");
                }

                string pattern = parts[1];
                int starIndex = pattern.IndexOf('*');
                if (starIndex < 0)
                {
                    ThrowCompileError(field, "必须包含 *");
                }

                _pathPattern = new SimpleReplacePattern(
                    pattern[..starIndex],
                    pattern[(starIndex + 1)..]);
                break;
            }
            case "unity":
            {
                if (parts.Length != 1)
                {
                    ThrowCompileError(field, "unity模式不接受额外参数");
                }

                _pathPattern = new UnityAddressablePattern();
                break;
            }
            case "ue":
            {
                if (parts.Length != 1)
                {
                    ThrowCompileError(field, "ue模式不接受额外参数");
                }

                _pathPattern = new Ue4ResourcePattern();
                break;
            }
            case "godot":
            {
                if (parts.Length != 1)
                {
                    ThrowCompileError(field, "godot模式不接受额外参数");
                }

                _pathPattern = new GodotResourcePattern();
                break;
            }
            default:
                ThrowCompileError(field, $"不支持的path模式类型:{patternType}");
                break;
        }

        _pathPattern.EmptyAble = emptyAble;
    }

    public override void Validate(DataValidatorContext ctx, TType type, DType data)
    {
        if (_rootDirs.Length == 0)
        {
            return;
        }

        string value = ((DString)data).Value;
        if (value == "" && _pathPattern.EmptyAble)
        {
            return;
        }

        foreach (string rootDir in _rootDirs)
        {
            if (_pathPattern.ExistPath(rootDir, value))
            {
                return;
            }
        }

        Logger.Error(
            "{}:{} (来自文件:{}) 在任一资源根目录中都找不到对应文件。roots=[{}]",
            RecordPath,
            value,
            Source,
            string.Join(", ", _rootDirs));
        GenerationContext.Current.LogValidatorFail(this);
    }

    private static string[] ParseRootDirs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(GetPathComparer())
            .ToArray();
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private void ThrowCompileError(DefField field, string error)
    {
        throw new ArgumentException($"field:{field} {_rawPattern} 定义不合法. {error}");
    }
}
