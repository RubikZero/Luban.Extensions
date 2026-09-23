using Luban;
using Luban.PostProcess;
using Luban.Utils;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>Runs every Lua rule once as an explicitly enabled data postprocessor.</summary>
[PostProcess("luaValidator", TargetFileType.DataExport, Priority = 100)]
public sealed class LuaScriptPostProcessor : PostProcessBase
{
    private const string OptionFamily = "luaValidator";
    private const string ScriptDirOption = "scriptDir";
    private const string ExecutionKey = "luaValidator.postprocessExecuted";

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private readonly string[] _scriptDirectories;

    public LuaScriptPostProcessor()
    {
        EnvManager.Current.TryGetOption(OptionFamily, ScriptDirOption, false, out string? rawDirectories);
        _scriptDirectories = ParseDirectories(rawDirectories);
    }

    // Luban collects the exported files into one manifest, hands the
    // postprocessor an empty second manifest and saves whatever comes back:
    //
    //   mission.Handle(ctx, dataTarget, outputManifest);
    //   var newManifest = PostProcess(dataPostprocess, outputManifest);
    //   Save(newManifest);
    //
    // A postprocessor that only validates therefore returns an empty manifest and
    // the export writes nothing at all — already exported files are removed by the
    // output saver. Passing every file through unchanged is what makes validation
    // additive instead of destructive.
    public override void PostProcess(OutputFileManifest oldOutputFileManifest, OutputFileManifest newOutputFileManifest)
    {
        // Also runs for a target that exports no file, so a rule problem cannot
        // hide behind an empty export.
        ExecuteOnce();

        foreach (OutputFile outputFile in oldOutputFileManifest.DataFiles)
        {
            PostProcess(oldOutputFileManifest, newOutputFileManifest, outputFile);
        }
    }

    public override void PostProcess(OutputFileManifest oldOutputFileManifest, OutputFileManifest newOutputFileManifest, OutputFile outputFile)
    {
        // ExecuteOnce is idempotent, so validation still runs exactly once whether
        // the pipeline drives the whole manifest or individual files.
        ExecuteOnce();
        newOutputFileManifest.AddFile(outputFile);
    }

    private void ExecuteOnce()
    {
        if (_scriptDirectories.Length == 0)
        {
            throw new InvalidOperationException($"Lua validation requires -x {OptionFamily}.{ScriptDirOption}=<directory>.");
        }

        if (GenerationContext.Current.GetOrAddUniqueObject(ExecutionKey, () => this) != this)
        {
            return;
        }

        List<string> scriptFiles = GetScriptFiles();
        if (scriptFiles.Count == 0)
        {
            throw new InvalidOperationException($"No Lua rule files (*.lua) were found in: {string.Join(", ", _scriptDirectories)}");
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
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Lua validation failed with {failures.Count} error(s).");
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

        // MoonSharp's soft sandbox also enables the CLR interop module
        // (dynamic.eval / dynamic.prepare), which can evaluate expressions at run
        // time. Rules only need cfg, fail and expect.
        script.Globals.Set("dynamic", DynValue.Nil);
    }

    private List<string> GetScriptFiles()
    {
        List<string> files = new();
        foreach (string directory in _scriptDirectories)
        {
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"Lua validation rule directory does not exist: {directory}");
            }

            files.AddRange(Directory.EnumerateFiles(directory, "*.lua", SearchOption.AllDirectories));
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
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
