using Luban;
using Luban.Datas;
using Luban.Defs;
using Luban.Types;
using Luban.Utils;
using Luban.Validator;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>
/// Runs a registered column rule on one field of every record. The rule is named
/// by the <c>#lua=&lt;name&gt;</c> type tag, for example
/// <c>type="string#path=normal;assets/*.txt#lua=iconRule"</c>, and it is
/// registered by a script in the column rule directories.
///
/// This is a Luban validator, not a postprocessor, so it runs in the validation
/// phase of every export — no <c>-x dataPostprocess=luaValidator</c> needed — and
/// a failure is reported and recorded exactly like a built-in one, leaving the
/// exit code to the strict flag.
/// </summary>
[Validator(LuaRuleFiles.TagName, Priority = 100)]
public sealed class LuaColumnValidator : DataValidatorBase
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly string[] _scriptDirectories;
    private readonly string[] _columnDirectories;

    private LuaColumnRules.Rule? _rule;

    public LuaColumnValidator()
    {
        EnvManager.Current.TryGetOption(
            LuaRuleFiles.OptionFamily,
            LuaRuleFiles.ScriptDirOption,
            false,
            out string? rawScriptDirectories);
        _scriptDirectories = LuaRuleFiles.ParseDirectories(rawScriptDirectories);
        _columnDirectories = LuaRuleFiles.ResolveColumnDirectories(_scriptDirectories);
    }

    public override void Compile(DefField field, TType type)
    {
        string fieldName = field?.ToString() ?? "(table value)";
        string requested = DefUtil.TrimBracePairs(Args ?? string.Empty).Trim();

        if (requested.Length == 0 || string.Equals(requested, LuaRuleFiles.TagName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"field:{fieldName} 的 #{LuaRuleFiles.TagName} 标签需要写成 #{LuaRuleFiles.TagName}=<列规则名>");
        }

        if (_columnDirectories.Length == 0)
        {
            throw new ArgumentException(
                $"field:{fieldName} 使用了 #{LuaRuleFiles.TagName}={requested}，但没有列规则目录。" +
                $"请用 -x {LuaRuleFiles.OptionFamily}.{LuaRuleFiles.ColumnDirOption}=<目录>，" +
                $"或把脚本放进 <{LuaRuleFiles.ScriptDirOption}>/{LuaRuleFiles.ColumnsSubdirectory}");
        }

        LuaColumnRules rules;
        try
        {
            rules = LuaColumnRules.Current(_columnDirectories);
        }
        catch (Exception exception)
        {
            throw new ArgumentException(
                $"field:{fieldName} 的 #{LuaRuleFiles.TagName} 标签无法解析: {exception.Message}",
                exception);
        }

        if (!rules.TryGet(requested, out LuaColumnRules.Rule? rule) || rule is null)
        {
            throw new ArgumentException(
                $"field:{fieldName} 声明了 #{LuaRuleFiles.TagName}={requested}，但没有列规则注册这个名字" +
                $"（已注册: {rules.Describe()}）");
        }

        if (!LuaColumnRules.MatchesType(rule.TypeName, type, field?.Type))
        {
            string schemaType = LuaColumnRules.BaseTypeName(field?.Type);
            string actual = schemaType.Length == 0 ? type.TypeName : $"{schemaType} ({type.TypeName})";
            throw new ArgumentException(
                $"field:{fieldName} 的 #{LuaRuleFiles.TagName}={requested} 声明输入类型 '{rule.TypeName}'（{rule.File}），" +
                $"但字段的实际类型是 '{actual}'");
        }

        _rule = rule;
    }

    public override void Validate(DataValidatorContext ctx, TType type, DType data)
    {
        LuaColumnRules.Rule? rule = _rule;
        if (rule is null)
        {
            return;
        }

        Record? record = DataValidatorContext.CurrentVisitor?.CurrentValidateRecord;
        List<string> failures = new();
        LuaColumnRuntime.Current(_scriptDirectories, _columnDirectories)
            .Invoke(rule, data, record, ResolveOwner(record), failures);
        if (failures.Count == 0)
        {
            return;
        }

        string location = DescribeLocation(record);
        foreach (string failure in failures)
        {
            Logger.Error("[lua] {0}: {1}", location, failure);
        }

        GenerationContext.Current.LogValidatorFail(LuaValidationFailure.Instance);
    }

    /// <summary>
    /// The bean that contains the field being validated, which lets a rule declared
    /// inside a bean read its sibling fields without knowing where that bean is
    /// used. For a field of the record itself this is the record, so the rule sees
    /// the same table as its <c>row</c> argument.
    /// </summary>
    private static DType? ResolveOwner(Record? record)
    {
        if (record is null)
        {
            return null;
        }

        DataValidatorVisitor? visitor = DataValidatorContext.CurrentVisitor;
        if (visitor is null)
        {
            return record.Data;
        }

        // The path is a stack, and its last component is the field being validated:
        // drop that one and walk the rest from the record. The first component is
        // the table name, which is not part of the data.
        List<object> components = visitor.Path.Reverse().ToList();
        if (components.Count == 0)
        {
            return record.Data;
        }

        components.RemoveAt(components.Count - 1);
        return LuaConfigData.Descend(record.Data, components.Skip(1)) ?? record.Data;
    }

    private static string DescribeLocation(Record? record)
    {
        string path = DataValidatorContext.CurrentVisitor is null
            ? string.Empty
            : DataValidatorContext.CurrentRecordPath;
        if (path.Length == 0)
        {
            path = record?.ToString() ?? "(unknown record)";
        }

        string source = record?.Source ?? string.Empty;
        return source.Length == 0 ? path : $"{path} ({source})";
    }
}

/// <summary>
/// The interpreter the column rules run in, built once per generation: it owns the
/// registered functions, the read-only <c>cfg</c> view of the loaded tables, and
/// the <c>fail</c> / <c>expect</c> globals.
///
/// MoonSharp is not thread-safe, and Luban validates one table per task, so calls
/// are serialised here instead of giving every thread its own copy of the whole
/// read-only view. Cross-table lookups therefore stay free; only the rule calls
/// themselves are queued.
/// </summary>
internal sealed class LuaColumnRuntime
{
    private const string RuntimeKey = "luaValidator.columnRuntime";

    private static readonly object BuildLock = new();

    private readonly Script _script;
    private readonly LuaConfigData _configData;
    private readonly Dictionary<string, DynValue> _functions;
    private readonly object _callLock = new();

    private CallState? _call;

    private LuaColumnRuntime(string[] columnDirectories)
    {
        _configData = LuaConfigData.Create(GenerationContext.Current);

        _script = new Script(CoreModules.Preset_SoftSandbox);
        LuaRuleFiles.HardenSandbox(_script);
        _script.Globals["cfg"] = _configData.CreateLuaTable(_script);
        LuaRuleFiles.RegisterFailureGlobals(_script, ReportFailure);

        // The call interpreter loads the scripts itself: a Lua function can only be
        // called from the interpreter that owns it.
        _functions = LuaColumnRules.LoadFunctions(_script, columnDirectories);
    }

    internal static LuaColumnRuntime Current(string[] scriptDirectories, string[] columnDirectories)
    {
        lock (BuildLock)
        {
            if (GenerationContext.Current.TryGetUniqueObject(RuntimeKey) is LuaColumnRuntime cached)
            {
                return cached;
            }

            LuaColumnRuntime built = new(columnDirectories);
            GenerationContext.Current.GetOrAddUniqueObject(RuntimeKey, () => built);
            return built;
        }
    }

    internal void Invoke(LuaColumnRules.Rule rule, DType data, Record? record, DType? owner, List<string> failures)
    {
        if (!_functions.TryGetValue(rule.Name, out DynValue? function) || function is null)
        {
            failures.Add($"{Path.GetFileName(rule.File)}: rule '{rule.Name}' is missing from the call interpreter.");
            return;
        }

        lock (_callLock)
        {
            _call = new CallState(rule, failures);
            try
            {
                Table? rowTable = record is null ? null : _configData.GetRowTable(record);
                DynValue value = LuaConfigData.ToLuaValue(_script, data);
                DynValue row = rowTable is null ? DynValue.Nil : DynValue.NewTable(rowTable);

                // A field of the record itself has the record as its owner, so hand
                // the rule the very table it already has as `row`: that keeps
                // owner == row, which a rule can rely on.
                DynValue ownerValue = owner switch
                {
                    null => DynValue.Nil,
                    _ when rowTable is not null && ReferenceEquals(owner, record!.Data) => DynValue.NewTable(rowTable),
                    _ => LuaConfigData.ToLuaValue(_script, owner),
                };

                _script.Call(function, value, row, ownerValue);
            }
            catch (InterpreterException exception)
            {
                failures.Add($"{Path.GetFileName(rule.File)}: {exception.DecoratedMessage}");
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(rule.File)}: {exception.Message}");
            }
            finally
            {
                _call = null;
            }
        }
    }

    private void ReportFailure(string message)
    {
        CallState? call = _call;
        if (call is null)
        {
            return;
        }

        call.Failures.Add($"{Path.GetFileName(call.Rule.File)}: {message}");
    }

    private sealed class CallState
    {
        internal CallState(LuaColumnRules.Rule rule, List<string> failures)
        {
            Rule = rule;
            Failures = failures;
        }

        internal LuaColumnRules.Rule Rule { get; }

        internal List<string> Failures { get; }
    }
}
