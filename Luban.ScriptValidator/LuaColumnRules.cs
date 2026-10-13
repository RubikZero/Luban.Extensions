using Luban.Types;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>
/// The column rules registered by the scripts in the column directories.
///
/// They are loaded while Luban compiles the schema, because the
/// <c>#lua=&lt;name&gt;</c> tag has to be resolved and its declared input type
/// checked before any data is read. A second load happens in the per-call
/// interpreter, since a Lua function can only be called from the interpreter that
/// owns it.
/// </summary>
internal sealed class LuaColumnRules
{
    private const string RegistryKey = "luaValidator.columnRules";

    private const string NameKey = "name";
    private const string TypeKey = "type";
    private const string ValidateKey = "validate";

    private static readonly object LoadLock = new();

    internal sealed class Rule
    {
        internal Rule(string name, string typeName, string file)
        {
            Name = name;
            TypeName = typeName;
            File = file;
        }

        internal string Name { get; }

        /// <summary>Input type the registration declared, <c>any</c> when omitted.</summary>
        internal string TypeName { get; }

        internal string File { get; }
    }

    private sealed class Registration
    {
        internal Registration(string name, string typeName, string file, DynValue function)
        {
            Name = name;
            TypeName = typeName;
            File = file;
            Function = function;
        }

        internal string Name { get; }

        internal string TypeName { get; }

        internal string File { get; }

        internal DynValue Function { get; }
    }

    private readonly Dictionary<string, Rule> _rules;

    private LuaColumnRules(Dictionary<string, Rule> rules)
    {
        _rules = rules;
    }

    /// <summary>The rules of the current generation, loaded once.</summary>
    internal static LuaColumnRules Current(string[] columnDirectories)
    {
        lock (LoadLock)
        {
            if (GenerationContext.Current.TryGetUniqueObject(RegistryKey) is LuaColumnRules cached)
            {
                return cached;
            }

            LuaColumnRules loaded = new(ReadRegistrations(columnDirectories));
            GenerationContext.Current.GetOrAddUniqueObject(RegistryKey, () => loaded);
            return loaded;
        }
    }

    internal bool TryGet(string name, out Rule? rule)
    {
        bool found = _rules.TryGetValue(name, out Rule? value);
        rule = value;
        return found;
    }

    internal string Describe()
    {
        return _rules.Count == 0
            ? "none"
            : string.Join(", ", _rules.Keys.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Executes the column scripts in the given interpreter and returns the
    /// registered functions, so the call interpreter owns the functions it calls.
    /// </summary>
    internal static Dictionary<string, DynValue> LoadFunctions(Script script, string[] columnDirectories)
    {
        Dictionary<string, Registration> registrations = new(StringComparer.Ordinal);
        Execute(script, columnDirectories, registrations);
        return registrations.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Function,
            StringComparer.Ordinal);
    }

    /// <summary>Reads the rules and fails on anything that cannot be a rule.</summary>
    private static Dictionary<string, Rule> ReadRegistrations(string[] columnDirectories)
    {
        Dictionary<string, Rule> rules = new(StringComparer.Ordinal);
        if (columnDirectories.Length == 0)
        {
            return rules;
        }

        Script script = new(CoreModules.Preset_SoftSandbox);
        LuaRuleFiles.HardenSandbox(script);

        Dictionary<string, Registration> registrations = new(StringComparer.Ordinal);
        Execute(script, columnDirectories, registrations);
        foreach (Registration registration in registrations.Values)
        {
            rules[registration.Name] = new Rule(registration.Name, registration.TypeName, registration.File);
        }

        return rules;
    }

    private static void Execute(
        Script script,
        string[] columnDirectories,
        Dictionary<string, Registration> sink)
    {
        string currentFile = string.Empty;

        script.Globals["register"] = DynValue.NewCallback((_, args) =>
        {
            Register(sink, currentFile, args);
            return DynValue.Nil;
        });

        foreach (string file in LuaRuleFiles.EnumerateRules(columnDirectories))
        {
            currentFile = file;

            // Each file gets a clean slate: a file that only defines validate() is
            // an export rule and must not be mistaken for a column rule because an
            // earlier file happened to define one.
            script.Globals.Set("validate", DynValue.Nil);
            int before = sink.Count;

            try
            {
                script.DoString(File.ReadAllText(file), codeFriendlyName: file);
            }
            catch (InterpreterException exception)
            {
                throw new InvalidOperationException(
                    $"column rule script failed: {file}: {exception.DecoratedMessage}. " +
                    "Column scripts run while the schema is compiled, so they cannot use cfg, row, fail or expect; " +
                    "use them inside the registered function instead.",
                    exception);
            }

            bool definesValidate = script.Globals.Get("validate").Type is DataType.Function or DataType.ClrFunction;
            if (definesValidate && sink.Count == before)
            {
                throw new InvalidOperationException(
                    $"{file} defines validate() but registers nothing. Export rules belong in " +
                    $"-x {LuaRuleFiles.OptionFamily}.{LuaRuleFiles.ScriptDirOption}, not in the column directory.");
            }
        }
    }

    private static void Register(Dictionary<string, Registration> sink, string file, CallbackArguments args)
    {
        string? name = null;
        string? typeName = null;
        DynValue function = DynValue.Nil;

        if (args.Count >= 1 && args[0].Type == DataType.Table)
        {
            foreach (TablePair pair in args[0].Table.Pairs)
            {
                string key = pair.Key.CastToString() ?? string.Empty;
                switch (key)
                {
                    case NameKey:
                        name = pair.Value.CastToString();
                        break;
                    case TypeKey:
                        typeName = pair.Value.CastToString();
                        break;
                    case ValidateKey:
                        function = pair.Value;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"{file}: register{{...}} has an unsupported key '{key}'. Supported keys: {NameKey}, {TypeKey}, {ValidateKey}.");
                }
            }
        }
        else
        {
            name = args.Count > 0 ? args[0].CastToString() : null;
            typeName = args.Count > 1 ? args[1].CastToString() : null;
            function = args.Count > 2 ? args[2] : DynValue.Nil;
        }

        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException(
                $"{file}: a column rule needs a name: register{{ name = \"...\", type = \"...\", validate = function(value, row) ... end }} " +
                "or register(\"name\", \"type\", function(value, row) ... end).");
        }

        if (function.Type is not (DataType.Function or DataType.ClrFunction))
        {
            throw new InvalidOperationException($"{file}: column rule '{name}' has no validate function.");
        }

        if (sink.TryGetValue(name, out Registration? existing))
        {
            throw new InvalidOperationException(
                $"{file}: column rule '{name}' is already registered by {existing.File}. Rule names have to be unique.");
        }

        typeName = string.IsNullOrWhiteSpace(typeName) ? "any" : typeName.Trim();
        sink[name] = new Registration(name, typeName, file, function);
    }

    /// <summary>
    /// Whether a field of the given type may use a rule that declared
    /// <paramref name="declared"/>. Anything Luban can name works, so a rule can
    /// declare a scalar (<c>string</c>, <c>int</c>, …), <c>bean</c>, <c>enum</c>,
    /// <c>collection</c>, the type exactly as the schema spells it
    /// (<c>UnitInfo</c>, <c>UnitType</c>), or <c>any</c>.
    /// </summary>
    internal static bool MatchesType(string declared, TType type, string? rawFieldType)
    {
        string expected = declared.Trim();
        if (expected.Length == 0 || string.Equals(expected, "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(expected, "bean", StringComparison.OrdinalIgnoreCase) && type.IsBean)
        {
            return true;
        }

        if (string.Equals(expected, "enum", StringComparison.OrdinalIgnoreCase) && type.IsEnum)
        {
            return true;
        }

        if (string.Equals(expected, "collection", StringComparison.OrdinalIgnoreCase) && type.IsCollection)
        {
            return true;
        }

        if (string.Equals(expected, type.TypeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A bean or enum type reports the generic "bean"/"enum" as its Luban type
        // name, so the schema spelling is what lets a rule say which bean or enum it
        // accepts.
        IReadOnlyList<string> parts = TypeComponents(rawFieldType);
        if (parts.Count == 0)
        {
            return false;
        }

        if (parts.Count > 1 && !string.Equals(parts[0], type.TypeName, StringComparison.OrdinalIgnoreCase))
        {
            // A collection field carries its tags on the element type, so the value
            // the rule receives is the element: the element's spelling is what the
            // declaration has to match, not the whole field type.
            return string.Equals(expected, parts[^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(expected, string.Join(",", parts), StringComparison.OrdinalIgnoreCase)
            || string.Equals(expected, string.Join(";", parts), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The type as written in the schema, without its <c>#tag=value</c> part.</summary>
    internal static string BaseTypeName(string? rawFieldType)
    {
        if (string.IsNullOrWhiteSpace(rawFieldType))
        {
            return string.Empty;
        }

        int depth = 0;
        for (int index = 0; index < rawFieldType.Length; index++)
        {
            switch (rawFieldType[index])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '#' when depth == 0:
                    return rawFieldType[..index].Trim();
            }
        }

        return rawFieldType.Trim();
    }

    /// <summary>
    /// Splits a schema type into its top-level components: <c>list,int</c> into
    /// <c>list</c> and <c>int</c>, <c>map,string,UnitInfo</c> into three. Nested
    /// groups stay together, so <c>(array#sep=;),int</c> keeps its container part
    /// intact.
    /// </summary>
    internal static IReadOnlyList<string> TypeComponents(string? rawFieldType)
    {
        string baseType = BaseTypeName(rawFieldType);
        if (baseType.Length == 0)
        {
            return Array.Empty<string>();
        }

        List<string> parts = new();
        int depth = 0;
        int start = 0;
        for (int index = 0; index < baseType.Length; index++)
        {
            switch (baseType[index])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' or ';' when depth == 0:
                    parts.Add(baseType[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        parts.Add(baseType[start..].Trim());
        return parts;
    }
}
