using Luban.Datas;
using Luban.Defs;
using Luban.Types;
using Luban.Utils;
using Luban.Validator;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>
/// An anchor validator which executes every Lua file in luaValidator.scriptDir once,
/// after Luban has loaded all table data and before any data target is written.
/// </summary>
[Validator("lua", Priority = 100)]
public sealed class LuaScriptValidator : DataValidatorBase
{
    private const string OptionFamily = "luaValidator";
    private const string ScriptDirOption = "scriptDir";
    private const string ExecutionKey = "luaValidator.executed";

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private readonly string[] _scriptDirectories;

    public LuaScriptValidator()
    {
        EnvManager.Current.TryGetOption(OptionFamily, ScriptDirOption, false, out string? rawDirectories);
        _scriptDirectories = ParseDirectories(rawDirectories);
    }

    public override void Compile(DefField field, TType type)
    {
        if (!string.IsNullOrWhiteSpace(Args))
        {
            throw new ArgumentException($"field:{field} lua validator does not accept arguments. Configure -x {OptionFamily}.{ScriptDirOption}=<directory> instead.");
        }
    }

    public override void Validate(DataValidatorContext ctx, TType type, DType data)
    {
        if (_scriptDirectories.Length == 0)
        {
            WarnMissingDirectoryOnce();
            return;
        }

        if (GenerationContext.Current.GetOrAddUniqueObject(ExecutionKey, () => this) != this)
        {
            return;
        }

        List<string> scriptFiles = GetScriptFiles();
        if (scriptFiles.Count == 0)
        {
            Logger.Warn("No Lua rule files (*.lua) were found in: {0}", string.Join(", ", _scriptDirectories));
            return;
        }

        LuaConfigData configData = LuaConfigData.Create(GenerationContext.Current);
        List<string> failures = new();
        foreach (string scriptFile in scriptFiles)
        {
            ExecuteScript(scriptFile, configData, failures);
        }

        foreach (string failure in failures)
        {
            Logger.Error("[lua validator] {0}", failure);
            GenerationContext.Current.LogValidatorFail(this);
        }
    }

    private void ExecuteScript(string scriptFile, LuaConfigData configData, List<string> failures)
    {
        Script script = new(CoreModules.Preset_SoftSandbox);
        RemoveTableMutationApis(script);
        script.Globals["cfg"] = configData.CreateLuaTable(script);
        script.Globals["fail"] = DynValue.NewCallback((_, args) =>
        {
            string message = args.Count > 0 ? args[0].CastToString() ?? args[0].ToPrintString() : "Lua validation failed.";
            failures.Add($"{scriptFile}: {message}");
            return DynValue.Nil;
        });
        script.Globals["expect"] = DynValue.NewCallback((_, args) =>
        {
            bool passed = args.Count > 0 && args[0].CastToBool();
            if (!passed)
            {
                string message = args.Count > 1 ? args[1].CastToString() ?? args[1].ToPrintString() : "Expectation failed.";
                failures.Add($"{scriptFile}: {message}");
            }
            return DynValue.Nil;
        });

        try
        {
            script.DoString(File.ReadAllText(scriptFile), codeFriendlyName: scriptFile);
            DynValue validate = script.Globals.Get("validate");
            if (validate.Type != DataType.Function && validate.Type != DataType.ClrFunction)
            {
                failures.Add($"{scriptFile}: a global function named validate() is required.");
                return;
            }

            script.Call(validate);
        }
        catch (InterpreterException exception)
        {
            failures.Add($"{scriptFile}: {exception.DecoratedMessage}");
        }
        catch (Exception exception)
        {
            failures.Add($"{scriptFile}: failed to execute script: {exception.Message}");
        }
    }

    private static void RemoveTableMutationApis(Script script)
    {
        // A read-only proxy intercepts ordinary assignment. Remove the standard
        // helpers which could otherwise bypass a proxy's __newindex metamethod.
        script.Globals.Set("rawget", DynValue.Nil);
        script.Globals.Set("rawset", DynValue.Nil);
        script.Globals.Set("getmetatable", DynValue.Nil);
        script.Globals.Set("setmetatable", DynValue.Nil);

        DynValue tableLibraryValue = script.Globals.Get("table");
        if (tableLibraryValue.Type == DataType.Table)
        {
            tableLibraryValue.Table.Set("insert", DynValue.Nil);
            tableLibraryValue.Table.Set("remove", DynValue.Nil);
            tableLibraryValue.Table.Set("sort", DynValue.Nil);
        }
    }

    private List<string> GetScriptFiles()
    {
        List<string> files = new();
        foreach (string directory in _scriptDirectories)
        {
            if (!Directory.Exists(directory))
            {
                Logger.Error("Lua validation rule directory does not exist: {0}", directory);
                GenerationContext.Current.LogValidatorFail(this);
                continue;
            }

            files.AddRange(Directory.EnumerateFiles(directory, "*.lua", SearchOption.AllDirectories));
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private void WarnMissingDirectoryOnce()
    {
        if (GenerationContext.Current.GetOrAddUniqueObject("luaValidator.missingScriptDirWarning", () => this) == this)
        {
            Logger.Warn("Lua validation is disabled because -x {0}.{1}=<directory> was not supplied.", OptionFamily, ScriptDirOption);
        }
    }

    private static string[] ParseDirectories(string? rawDirectories)
    {
        if (string.IsNullOrWhiteSpace(rawDirectories))
        {
            return Array.Empty<string>();
        }

        return rawDirectories
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }
}
