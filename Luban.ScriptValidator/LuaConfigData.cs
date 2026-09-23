using System.Globalization;
using Luban.Datas;
using Luban.Defs;
using Luban.TemplateExtensions;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>Builds a deep, read-only Lua representation of Luban's loaded tables.</summary>
internal sealed class LuaConfigData
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly GenerationContext _context;
    private readonly HashSet<DefTable> _loadedTables;
    private readonly List<TableRows> _tables;
    private readonly Dictionary<Record, Table> _rowTables = new();
    private readonly Dictionary<Table, RowLocation> _rowLocations = new();
    private readonly Dictionary<DefTable, Dictionary<string, Record>?> _keyMaps = new();
    private readonly Dictionary<DefTable, Table> _keyedViews = new();
    private readonly HashSet<string> _warnedTables = new(StringComparer.Ordinal);

    private Dictionary<string, DefTypeBase>? _typeIndex;
    private Dictionary<string, DefTable>? _tableIndex;
    private Script? _script;

    private readonly struct TableRows
    {
        public TableRows(DefTable table, List<Record> records)
        {
            Table = table;
            Records = records;
        }

        public DefTable Table { get; }

        public List<Record> Records { get; }
    }

    private readonly struct RowLocation
    {
        public RowLocation(DefTable table, Record record)
        {
            Table = table;
            Record = record;
        }

        public DefTable Table { get; }

        public Record Record { get; }
    }

    private LuaConfigData(GenerationContext context, List<TableRows> tables)
    {
        _context = context;
        _tables = tables;
        _loadedTables = new HashSet<DefTable>(context.Tables);
    }

    public static LuaConfigData Create(GenerationContext context)
    {
        List<TableRows> tables = new(context.Tables.Count);
        foreach (DefTable table in context.Tables)
        {
            tables.Add(new TableRows(table, context.GetTableAllDataList(table)));
        }

        return new LuaConfigData(context, tables);
    }

    public Table CreateLuaTable(Script script)
    {
        _script = script;
        Table tableRegistry = new(script);
        foreach (TableRows entry in _tables)
        {
            Table rows = new(script);
            foreach (Record record in entry.Records)
            {
                Table row = ToLuaBean(script, record.Data);
                row.Set("__source", DynValue.NewString(record.Source ?? string.Empty));
                row.Set("__autoIndex", DynValue.NewNumber(record.AutoIndex));
                Table proxy = ToReadOnlyTable(script, row).Table;
                rows.Append(DynValue.NewTable(proxy));
                _rowTables[record] = proxy;
                _rowLocations[proxy] = new RowLocation(entry.Table, record);
            }

            tableRegistry.Set(entry.Table.FullName, ToReadOnlyTable(script, rows));
        }

        Table cfg = new(script);
        cfg.Set("tables", ToReadOnlyTable(script, tableRegistry));
        cfg.Set("table", DynValue.NewCallback((_, args) =>
        {
            string? tableName = args.Count > 0 ? args[0].CastToString() : null;
            if (string.IsNullOrEmpty(tableName))
            {
                return DynValue.Nil;
            }

            return tableRegistry.Get(tableName);
        }));

        RegisterEnums(cfg);
        RegisterEnumHelpers(cfg);
        cfg.Set("keyed", DynValue.NewCallback((ctx, args) =>
            GetKeyedView(ResolveTable(args.Count > 0 ? args[0].CastToString() : null))));
        cfg.Set("tableInfo", DynValue.NewCallback((ctx, args) =>
            GetTableInfo(ResolveTable(args.Count > 0 ? args[0].CastToString() : null))));
        cfg.Set("ref", DynValue.NewCallback((_, args) => ResolveRef(args)));

        return ToReadOnlyTable(script, cfg).Table;
    }

    /// <summary>
    /// Returns the table's rows keyed by primary key, mirroring the DataMap of
    /// Luban's generated code. <c>cfg.tables.X</c> stays a plain row array, so the
    /// keyed form is exposed separately rather than overloading the array: an
    /// integer primary key would otherwise collide with a positional index.
    /// </summary>
    private DynValue GetKeyedView(DefTable? table)
    {
        if (table == null)
        {
            return DynValue.Nil;
        }

        if (_keyedViews.TryGetValue(table, out Table? cached))
        {
            return DynValue.NewTable(cached);
        }

        Dictionary<string, Record>? keyMap = GetKeyMap(table);
        if (keyMap == null)
        {
            return DynValue.Nil;
        }

        Table view = new(_script!);
        foreach ((string key, Record record) in keyMap)
        {
            if (!_rowTables.TryGetValue(record, out Table? row))
            {
                continue;
            }

            DynValue value = DynValue.NewTable(row);
            view.Set(key, value);

            // Keys are canonicalised to strings internally, so also register the
            // numeric form: rules naturally write keyed[100005], not keyed["100005"].
            if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numeric))
            {
                view.Set(DynValue.NewNumber(numeric), value);
            }
        }

        Table proxy = ToReadOnlyTable(_script!, view).Table;
        _keyedViews[table] = proxy;
        return DynValue.NewTable(proxy);
    }

    private DynValue GetTableInfo(DefTable? table)
    {
        if (table == null)
        {
            return DynValue.Nil;
        }

        string indexSpec = table.Index;
        if (indexSpec == null)
        {
            indexSpec = string.Empty;
        }

        Table indexFields = new(_script!);
        foreach (string part in indexSpec.Split('+', ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            indexFields.Append(DynValue.NewString(part));
        }

        bool loaded = _loadedTables.Contains(table);
        bool hasIndex = table.IndexField != null || indexSpec.Length > 0;

        Table info = new(_script!);
        info.Set("name", DynValue.NewString(table.FullName));
        info.Set("fullName", DynValue.NewString(table.FullNameWithTopModule ?? string.Empty));
        info.Set("mode", DynValue.NewString(
            table.IsSingletonTable ? "one"
            : table.IsMapTable ? "map"
            : table.IsListTable ? "list"
            : "unknown"));
        info.Set("index", DynValue.NewString(indexSpec));
        info.Set("indexFields", ToReadOnlyTable(_script!, indexFields));
        info.Set("keyType", DynValue.NewString(table.KeyTType?.TypeName ?? string.Empty));
        info.Set("count", DynValue.NewNumber(loaded ? _context.GetTableAllDataList(table).Count : 0));
        info.Set("loaded", DynValue.NewBoolean(loaded));
        info.Set("keyed", DynValue.NewBoolean(loaded && hasIndex));
        return DynValue.NewTable(ToReadOnlyTable(_script!, info).Table);
    }

    /// <summary>
    /// Publishes every enum as a constant table under <c>cfg.enums</c>, reachable
    /// by both the short and the top-module qualified type name. Rule files run in
    /// separate interpreters and cannot require a shared Lua module, so the
    /// constants are injected into every script instead.
    /// </summary>
    private void RegisterEnums(Table cfg)
    {
        EnsureIndexes();
        Table enums = new(_script!);
        Dictionary<DefEnum, Table> byEnum = new();
        foreach ((string name, DefTypeBase type) in _typeIndex!)
        {
            if (type is not DefEnum defEnum)
            {
                continue;
            }

            if (!byEnum.TryGetValue(defEnum, out Table? constants))
            {
                constants = ToReadOnlyTable(_script!, BuildEnumConstants(defEnum)).Table;
                byEnum[defEnum] = constants;
            }

            enums.Set(name, DynValue.NewTable(constants));
        }

        cfg.Set("enums", ToReadOnlyTable(_script!, enums));
    }

    /// <summary>
    /// Builds one constant table: item names map to their numeric value, and
    /// aliases map to the same value unless an item name already claimed the key.
    /// </summary>
    private Table BuildEnumConstants(DefEnum defEnum)
    {
        Table constants = new(_script!);
        foreach (DefEnum.Item item in defEnum.Items)
        {
            DynValue value = DynValue.NewNumber(item.IntValue);
            if (!string.IsNullOrEmpty(item.Name) && constants.Get(item.Name).IsNil())
            {
                constants.Set(item.Name, value);
            }

            if (!string.IsNullOrEmpty(item.Alias) && constants.Get(item.Alias).IsNil())
            {
                constants.Set(item.Alias, value);
            }
        }

        return constants;
    }

    private void RegisterEnumHelpers(Table cfg)
    {
        // cfg.enumValue("<type>", "<text as written in the sheet>") -> numeric value or nil
        // NOTE: the first parameter must not be named "_", otherwise "out _" below
        // binds to it instead of being a discard.
        cfg.Set("enumValue", DynValue.NewCallback((ctx, args) =>
            TryResolveEnum(args, out int value, out _)
                ? DynValue.NewNumber(value)
                : DynValue.Nil));

        // cfg.enumName("<type>", "<text>") -> enum item name or nil
        cfg.Set("enumName", DynValue.NewCallback((ctx, args) =>
            TryResolveEnum(args, out _, out DefEnum.Item? item) && item != null
                ? DynValue.NewString(item.Name)
                : DynValue.Nil));

        // cfg.enumItem("<type>", "<text>") -> { name, value, alias, comment } or nil
        cfg.Set("enumItem", DynValue.NewCallback((ctx, args) =>
            TryResolveEnum(args, out _, out DefEnum.Item? item) && item != null
                ? DynValue.NewTable(CreateEnumItemTable(item))
                : DynValue.Nil));

        // cfg.enumItems("<type>") -> array of { name, value, alias, comment } or nil
        cfg.Set("enumItems", DynValue.NewCallback((ctx, args) =>
        {
            string? typeName = args.Count > 0 ? args[0].CastToString() : null;
            if (!TryResolveEnumType(typeName, out DefEnum? defEnum))
            {
                return DynValue.Nil;
            }

            Table result = new(_script!);
            foreach (DefEnum.Item item in defEnum!.Items)
            {
                result.Append(DynValue.NewTable(CreateEnumItemTable(item)));
            }

            return DynValue.NewTable(ToReadOnlyTable(_script!, result).Table);
        }));
    }

    private Table CreateEnumItemTable(DefEnum.Item item)
    {
        Table result = new(_script!);
        result.Set("name", DynValue.NewString(item.Name));
        result.Set("value", DynValue.NewNumber(item.IntValue));
        result.Set("alias", DynValue.NewString(item.Alias));
        result.Set("comment", DynValue.NewString(item.Comment));
        return ToReadOnlyTable(_script!, result).Table;
    }

    private bool TryResolveEnum(CallbackArguments args, out int value, out DefEnum.Item? item)
    {
        value = 0;
        item = null;

        string? typeName = args.Count > 0 ? args[0].CastToString() : null;
        string? text = args.Count > 1 ? args[1].CastToString() : null;
        if (string.IsNullOrEmpty(text) || !TryResolveEnumType(typeName, out DefEnum? defEnum))
        {
            return false;
        }

        DefEnum resolvedEnum = defEnum!;
        if (!resolvedEnum.TryValueByNameOrAlias(text, out value))
        {
            if (!resolvedEnum.IsFlags)
            {
                return false;
            }

            try
            {
                value = resolvedEnum.GetValueByNameOrAlias(text, '|');
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Copy out of the out-parameter: it cannot be captured by the lambda below.
        int resolvedValue = value;
        item = resolvedEnum.Items.FirstOrDefault(i => i.IntValue == resolvedValue);
        return true;
    }

    private bool TryResolveEnumType(string? typeName, out DefEnum? defEnum)
    {
        defEnum = null;
        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        if (ResolveDefType(typeName) is not DefEnum resolved)
        {
            Logger.Warn("lua validator: '{0}' is not an enum type; cfg.enum* returned nil", typeName);
            return false;
        }

        defEnum = resolved;
        return true;
    }

    private DynValue ResolveRef(CallbackArguments args)
    {
        if (args.Count < 2)
        {
            return DynValue.Nil;
        }

        // cfg.ref(row, "field") - follow the field's declared #ref.
        if (args[0].Type == DataType.Table && args[0].Table != null
            && _rowLocations.TryGetValue(args[0].Table, out RowLocation location))
        {
            string? fieldName = args[1].CastToString();
            if (string.IsNullOrEmpty(fieldName))
            {
                return DynValue.Nil;
            }

            DefField? field = location.Record.Data.ImplType.HierarchyFields
                .FirstOrDefault(f => f.Name == fieldName);
            if (field == null)
            {
                return DynValue.Nil;
            }

            return ToReferencedRow(field, location.Record.Data.GetField(fieldName));
        }

        // cfg.ref("full.table.name", key) - look a row up by its primary key.
        if (args[0].Type == DataType.String)
        {
            return LookupRow(ResolveTable(args[0].String), CanonicalLuaKey(args[1]));
        }

        return DynValue.Nil;
    }

    private DynValue ToReferencedRow(DefField field, DType? value)
    {
        if (value == null)
        {
            return DynValue.Nil;
        }

        if (TypeTemplateExtension.CanGenerateRef(field))
        {
            return LookupRow(TypeTemplateExtension.GetRefTable(field), CanonicalDataKey(value));
        }

        if (TypeTemplateExtension.CanGenerateCollectionRef(field) && value.Datas != null)
        {
            // Elements whose target is missing are omitted, so the result is a
            // hole-free array but may be shorter than the source list.
            DefTable? target = TypeTemplateExtension.GetCollectionRefTable(field);
            Table result = new(_script!);
            foreach (DType element in value.Datas)
            {
                DynValue row = LookupRow(target, CanonicalDataKey(element));
                if (!row.IsNil())
                {
                    result.Append(row);
                }
            }

            return DynValue.NewTable(result);
        }

        return DynValue.Nil;
    }

    private DynValue LookupRow(DefTable? table, string? canonicalKey)
    {
        if (table == null || canonicalKey == null)
        {
            return DynValue.Nil;
        }

        Dictionary<string, Record>? keyMap = GetKeyMap(table);
        if (keyMap != null && keyMap.TryGetValue(canonicalKey, out Record? record)
            && _rowTables.TryGetValue(record, out Table? row))
        {
            return DynValue.NewTable(row);
        }

        return DynValue.Nil;
    }

    private Dictionary<string, Record>? GetKeyMap(DefTable table)
    {
        if (_keyMaps.TryGetValue(table, out Dictionary<string, Record>? cached))
        {
            return cached;
        }

        Dictionary<string, Record>? map = null;
        string? indexName = table.IndexField?.Name;
        if (string.IsNullOrEmpty(indexName) && !string.IsNullOrEmpty(table.Index))
        {
            indexName = table.Index!.Split('+', ',')[0].Trim();
        }

        if (string.IsNullOrEmpty(indexName))
        {
            WarnOnce(table, "has no single-field primary key");
        }
        else if (!_loadedTables.Contains(table))
        {
            WarnOnce(table, "is not part of the current export target, so its rows are unavailable");
        }
        else
        {
            map = new Dictionary<string, Record>(StringComparer.Ordinal);
            foreach (Record record in _context.GetTableAllDataList(table))
            {
                string? canonicalKey = CanonicalDataKey(record.Data?.GetField(indexName));
                if (canonicalKey != null)
                {
                    map[canonicalKey] = record;
                }
            }
        }

        _keyMaps[table] = map;
        return map;
    }

    private void WarnOnce(DefTable table, string reason)
    {
        if (_warnedTables.Add(table.FullName))
        {
            Logger.Warn("lua validator: cfg.ref() cannot index table '{0}' because it {1}", table.FullName, reason);
        }
    }

    private DefTypeBase? ResolveDefType(string name)
    {
        EnsureIndexes();
        return _typeIndex!.TryGetValue(name, out DefTypeBase? type) ? type : null;
    }

    private DefTable? ResolveTable(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        EnsureIndexes();
        if (_tableIndex!.TryGetValue(name, out DefTable? table))
        {
            return table;
        }

        return ResolveDefType(name) as DefTable;
    }

    /// <summary>
    /// Indexes the assembly's types under every spelling a rule may use: the
    /// schema full name, the top-module qualified name, and the short name.
    /// Ambiguous short names are dropped instead of guessed.
    /// </summary>
    private void EnsureIndexes()
    {
        if (_typeIndex != null)
        {
            return;
        }

        Dictionary<string, DefTypeBase> index = new(StringComparer.Ordinal);
        HashSet<string> ambiguous = new(StringComparer.Ordinal);

        void Add(string? key, DefTypeBase type)
        {
            if (string.IsNullOrEmpty(key) || ambiguous.Contains(key))
            {
                return;
            }

            if (index.TryGetValue(key, out DefTypeBase? existing))
            {
                // The same type reached through another spelling (e.g. a root
                // module type whose FullName equals its Name) is not ambiguous.
                if (ReferenceEquals(existing, type))
                {
                    return;
                }

                index.Remove(key);
                ambiguous.Add(key);
                return;
            }

            index[key] = type;
        }

        foreach (DefTypeBase type in _context.Assembly.TypeList)
        {
            Add(type.FullNameWithTopModule, type);
            Add(type.FullName, type);
            Add(type.Name, type);
        }

        _typeIndex = index;

        Dictionary<string, DefTable> tables = new(StringComparer.Ordinal);
        foreach (DefTable table in _context.Tables)
        {
            tables[table.FullName] = table;
            if (!string.IsNullOrEmpty(table.FullNameWithTopModule))
            {
                tables[table.FullNameWithTopModule] = table;
            }

            if (!string.IsNullOrEmpty(table.Name) && !tables.ContainsKey(table.Name))
            {
                tables[table.Name] = table;
            }
        }

        _tableIndex = tables;
    }

    /// <summary>Normalises a Luban data value so it can be used as a lookup key.</summary>
    private static string? CanonicalDataKey(DType? data)
    {
        return data switch
        {
            null => null,
            DBool value => value.Value ? "true" : "false",
            DByte value => value.Value.ToString(CultureInfo.InvariantCulture),
            DShort value => value.Value.ToString(CultureInfo.InvariantCulture),
            DInt value => value.Value.ToString(CultureInfo.InvariantCulture),
            DLong value => value.Value.ToString(CultureInfo.InvariantCulture),
            DFloat value => ((double)value.Value).ToString("R", CultureInfo.InvariantCulture),
            DDouble value => value.Value.ToString("R", CultureInfo.InvariantCulture),
            DString value => value.Value,
            DEnum value => value.Value.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>Normalises a Lua value so it can be used as a lookup key.</summary>
    private static string? CanonicalLuaKey(DynValue value)
    {
        return value.Type switch
        {
            DataType.String => value.String,
            DataType.Boolean => value.Boolean ? "true" : "false",
            DataType.Number => value.Number == Math.Floor(value.Number)
                ? ((long)value.Number).ToString(CultureInfo.InvariantCulture)
                : value.Number.ToString("R", CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static Table ToLuaBean(Script script, DBean bean)
    {
        Table table = new(script);
        IReadOnlyList<DefField> fields = bean.ImplType.HierarchyFields;
        for (int index = 0; index < fields.Count; index++)
        {
            table.Set(fields[index].Name, ToLuaValue(script, bean.Fields[index]));
        }

        table.Set("_type", DynValue.NewString(bean.TypeName));
        table.Set("__type", DynValue.NewString(GetBeanTypeName(bean)));
        return table;
    }

    private static string GetBeanTypeName(DBean bean)
    {
        DefBean? implType = bean.ImplType;
        if (implType == null)
        {
            return bean.TypeName;
        }

        return string.IsNullOrEmpty(implType.FullNameWithTopModule)
            ? implType.FullName
            : implType.FullNameWithTopModule;
    }

    private static DynValue ToLuaValue(Script script, DType? data)
    {
        if (data is null)
        {
            return DynValue.Nil;
        }

        return data switch
        {
            DBool value => DynValue.NewBoolean(value.Value),
            DByte value => DynValue.NewNumber(value.Value),
            DShort value => DynValue.NewNumber(value.Value),
            DInt value => DynValue.NewNumber(value.Value),
            DLong value => DynValue.NewString(value.Value.ToString(CultureInfo.InvariantCulture)),
            DFloat value => DynValue.NewNumber(value.Value),
            DDouble value => DynValue.NewNumber(value.Value),
            DString value => DynValue.NewString(value.Value),
            DEnum value => DynValue.NewString(value.StrValue),
            DDateTime value => DynValue.NewString(value.ToFormatString()),
            DBean value => ToReadOnlyTable(script, ToLuaBean(script, value)),
            DMap value => ToLuaMap(script, value),
            DArray value => ToLuaList(script, value.Datas),
            DList value => ToLuaList(script, value.Datas),
            DSet value => ToLuaList(script, value.Datas),
            _ => throw new NotSupportedException($"Luban data type '{data.GetType().FullName}' is not supported by the Lua validator."),
        };
    }

    private static DynValue ToLuaList(Script script, IReadOnlyList<DType> values)
    {
        Table table = new(script);
        foreach (DType value in values)
        {
            table.Append(ToLuaValue(script, value));
        }

        return ToReadOnlyTable(script, table);
    }

    private static DynValue ToLuaMap(Script script, DMap map)
    {
        Table table = new(script);
        foreach ((DType key, DType value) in map.DataMap)
        {
            table.Set(ToLuaValue(script, key), ToLuaValue(script, value));
        }

        return ToReadOnlyTable(script, table);
    }

    private static DynValue ToReadOnlyTable(Script script, Table source)
    {
        Table proxy = new(script);
        Table metaTable = new(script);
        metaTable.Set("__index", DynValue.NewTable(source));
        metaTable.Set("__newindex", DynValue.NewCallback((_, _) =>
            throw new ScriptRuntimeException("attempt to modify read-only configuration data")));
        metaTable.Set("__len", DynValue.NewCallback((_, _) => DynValue.NewNumber(source.Length)));
        metaTable.Set("__pairs", CreatePairsIterator(source));
        metaTable.Set("__ipairs", CreateIpairsIterator(source));
        metaTable.Set("__metatable", DynValue.NewString("read-only configuration data"));
        proxy.MetaTable = metaTable;
        return DynValue.NewTable(proxy);
    }

    private static DynValue CreatePairsIterator(Table source)
    {
        DynValue next = DynValue.NewCallback((_, args) =>
        {
            DynValue previousKey = args.Count > 1 ? args[1] : DynValue.Nil;
            TablePair? pair = source.NextKey(previousKey);
            return pair is null || pair.Value.Key.IsNil()
                ? DynValue.Nil
                : DynValue.NewTuple(pair.Value.Key, pair.Value.Value);
        });

        return DynValue.NewCallback((_, _) => DynValue.NewTuple(next, DynValue.Nil, DynValue.Nil));
    }

    private static DynValue CreateIpairsIterator(Table source)
    {
        DynValue next = DynValue.NewCallback((_, args) =>
        {
            int previousIndex = args.Count > 1 && args[1].Type == DataType.Number
                ? (int)args[1].Number
                : 0;
            int nextIndex = previousIndex + 1;
            DynValue value = source.Get(nextIndex);
            return value.IsNil()
                ? DynValue.Nil
                : DynValue.NewTuple(DynValue.NewNumber(nextIndex), value);
        });

        return DynValue.NewCallback((_, _) => DynValue.NewTuple(next, DynValue.Nil, DynValue.NewNumber(0)));
    }
}
