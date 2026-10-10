using Luban.Datas;
using Luban.Defs;
using Luban.Types;
using Luban.Utils;
using Luban.Validator;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>
/// Shared plumbing for the two kinds of Lua rules.
///
/// Export rules (<c>-x luaValidator.scriptDir=...</c>) define <c>validate()</c>
/// and run as a data postprocessor, after the tables are loaded.
///
/// Column rules (<c>-x luaValidator.columnDir=...</c>, by default the
/// <c>columns</c> subdirectory of every script directory) only call
/// <c>register(...)</c> and are loaded while the schema is compiled, because the
/// <c>#lua=&lt;name&gt;</c> type tag has to be resolved before any data exists.
/// </summary>
internal static class LuaRuleFiles
{
    internal const string OptionFamily = "luaValidator";
    internal const string ScriptDirOption = "scriptDir";
    internal const string ColumnDirOption = "columnDir";

    /// <summary>Type tag that attaches a registered column rule to a field.</summary>
    internal const string TagName = "lua";

    /// <summary>
    /// Column rules default to this directory below every script directory, so a
    /// project can keep one rule tree and still let each kind of rule live in its
    /// own place.
    /// </summary>
    internal const string ColumnsSubdirectory = "columns";

    internal static string[] ParseDirectories(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
    }

    /// <summary>
    /// Resolves the column-rule directories: the explicit option when it is set,
    /// otherwise every existing <c>&lt;scriptDir&gt;/columns</c>. A default that does
    /// not exist is simply "no column rules"; an explicit directory that does not
    /// exist is reported when it is read.
    /// </summary>
    internal static string[] ResolveColumnDirectories(string[] scriptDirectories)
    {
        EnvManager.Current.TryGetOption(OptionFamily, ColumnDirOption, false, out string? raw);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return ParseDirectories(raw);
        }

        List<string> defaults = new();
        foreach (string scriptDirectory in scriptDirectories)
        {
            string candidate = Path.Combine(scriptDirectory, ColumnsSubdirectory);
            if (Directory.Exists(candidate))
            {
                defaults.Add(Path.GetFullPath(candidate));
            }
        }

        return defaults.ToArray();
    }

    /// <summary>Every <c>*.lua</c> below the given directories, in a stable order.</summary>
    internal static List<string> EnumerateRules(
        IEnumerable<string> directories,
        IReadOnlyCollection<string>? excludedDirectories = null)
    {
        List<string> files = new();
        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"Lua rule directory does not exist: {directory}");
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.lua", SearchOption.AllDirectories))
            {
                if (excludedDirectories != null && excludedDirectories.Any(excluded => IsUnder(file, excluded)))
                {
                    continue;
                }

                files.Add(file);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    internal static bool IsUnder(string file, string directory)
    {
        string full = Path.GetFullPath(file);
        string root = Path.GetFullPath(directory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return full.StartsWith(root, PathComparison);
    }

    /// <summary>
    /// Removes the APIs that are irrelevant for rules and dangerous in general:
    /// file/process/package loading, and the helpers that could bypass the
    /// read-only proxies.
    /// </summary>
    internal static void HardenSandbox(Script script)
    {
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
        // time. Rules only need cfg, register, fail and expect.
        script.Globals.Set("dynamic", DynValue.Nil);
    }

    /// <summary>
    /// Wires the two globals every rule kind shares: <c>fail(message)</c> and
    /// <c>expect(condition, message)</c>. Both hand the message to the caller,
    /// which prefixes it with the file it came from.
    /// </summary>
    internal static void RegisterFailureGlobals(Script script, Action<string> onFailure)
    {
        script.Globals["fail"] = DynValue.NewCallback((_, args) =>
        {
            onFailure(args.Count > 0 ? args[0].CastToString() ?? args[0].ToPrintString() : "Lua validation failed.");
            return DynValue.Nil;
        });

        script.Globals["expect"] = DynValue.NewCallback((_, args) =>
        {
            if (!(args.Count > 0 && args[0].CastToBool()))
            {
                onFailure(args.Count > 1 ? args[1].CastToString() ?? args[1].ToPrintString() : "Expectation failed.");
            }

            return DynValue.Nil;
        });
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>
/// The adapter Luban's own bookkeeping needs: <c>LogValidatorFail</c> takes an
/// <c>IDataValidator</c> and only remembers that a validation failed, which is
/// what turns a reported failure into exit code 1 under the strict flag.
/// </summary>
internal sealed class LuaValidationFailure : DataValidatorBase
{
    internal static readonly IDataValidator Instance = new LuaValidationFailure();

    public override void Compile(DefField field, TType type)
    {
    }

    public override void Validate(DataValidatorContext ctx, TType type, DType data)
    {
    }
}
