using Luban;
using Luban.PostProcess;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>
/// The export rules: every <c>*.lua</c> below the script directories that defines
/// <c>validate()</c> runs once per export, as an explicitly enabled data
/// postprocessor. Column rules registered with <c>register(...)</c> live in the
/// column directories and are handled by <see cref="LuaColumnValidator"/>.
/// </summary>
[PostProcess("luaValidator", TargetFileType.DataExport, Priority = 100)]
public sealed class LuaScriptPostProcessor : PostProcessBase
{
    private const string ExecutionKey = "luaValidator.postprocessExecuted";

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly string[] _scriptDirectories;
    private readonly string[] _columnDirectories;

    public LuaScriptPostProcessor()
    {
        EnvManager.Current.TryGetOption(
            LuaRuleFiles.OptionFamily,
            LuaRuleFiles.ScriptDirOption,
            false,
            out string? rawDirectories);
        _scriptDirectories = LuaRuleFiles.ParseDirectories(rawDirectories);
        _columnDirectories = LuaRuleFiles.ResolveColumnDirectories(_scriptDirectories);
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
            throw new InvalidOperationException(
                $"Lua validation requires -x {LuaRuleFiles.OptionFamily}.{LuaRuleFiles.ScriptDirOption}=<directory>.");
        }

        // The pipeline calls the manifest overload and, through it, the per-file
        // overload, and Luban hands the same postprocessor instance to every call.
        // A fresh probe object is what separates "I stored this just now" from
        // "someone already stored theirs"; comparing against `this` cannot, and
        // would run the rules once per exported file.
        object probe = new();
        if (GenerationContext.Current.GetOrAddUniqueObject(ExecutionKey, () => probe) != probe)
        {
            return;
        }

        List<string> scriptFiles = GetScriptFiles();
        if (scriptFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"No Lua rule files (*.lua) were found in: {string.Join(", ", _scriptDirectories)}");
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
            Logger.Error("[lua validator] {0} error(s) in {1} rule file(s).", failures.Count, scriptFiles.Count);

            // Luban treats a validator failure as a report, not as an abort: the
            // run continues, the output is written, and --validationFailAsError
            // (4.x) or --strict (5.x) decides the exit code at the end of the
            // pipeline. Recording the failure here is what makes Lua rules behave
            // exactly like the built-in validators.
            GenerationContext.Current.LogValidatorFail(LuaValidationFailure.Instance);
        }
    }

    private void ExecuteScript(string scriptFile, LuaConfigData configData, List<string> failures)
    {
        Script script = new(CoreModules.Preset_SoftSandbox);
        LuaRuleFiles.HardenSandbox(script);
        script.Globals["cfg"] = configData.CreateLuaTable(script);
        LuaRuleFiles.RegisterFailureGlobals(script, message => failures.Add($"{scriptFile}: {message}"));

        try
        {
            script.DoString(File.ReadAllText(scriptFile), codeFriendlyName: scriptFile);
            DynValue validate = script.Globals.Get("validate");
            if (validate.Type != DataType.Function && validate.Type != DataType.ClrFunction)
            {
                failures.Add($"{scriptFile}: a global function named validate() is required. " +
                    $"Column rules use register(...) and belong in -x {LuaRuleFiles.OptionFamily}.{LuaRuleFiles.ColumnDirOption}.");
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

    /// <summary>
    /// Every rule file below the script directories, minus the column directories:
    /// those are loaded by the column validator at schema compile time, and their
    /// scripts have no <c>validate()</c>.
    /// </summary>
    private List<string> GetScriptFiles()
    {
        return LuaRuleFiles.EnumerateRules(_scriptDirectories, _columnDirectories);
    }
}
