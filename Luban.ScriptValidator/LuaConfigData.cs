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
    private readonly Dictionary<DefTable, Dictionary<string, Dictionary<string, Record>?>> _keyMaps = new();
    private readonly Dictionary<DefTable, Dictionary<string, Table>> _keyedViews = new();
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);

    /// <summary>
    /// Joins the components of a union index into one flat canonical key. The
    /// character cannot appear in a canonicalised value, so the parts stay
    /// unambiguous.
    /// </summary>
    private const char KeySeparator = '\u0001';

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

            // Accept the same spellings as cfg.keyed / cfg.ref / cfg.tableInfo.
            // cfg.tables itself stays keyed by the canonical full name only, so a
            // rule that enumerates it never sees the same table twice.
            DefTable? table = ResolveTable(tableName);
            return table == null ? DynValue.Nil : tableRegistry.Get(table.FullName);
        }));

        RegisterEnums(cfg);
        RegisterEnumHelpers(cfg);
        cfg.Set("keyed", DynValue.NewCallback((ctx, args) =>
        {
            DefTable? table = ResolveTable(args.Count > 0 ? args[0].CastToString() : null);
            List<string>? fields = null;
            if (args.Count > 1)
            {
                fields = new List<string>(args.Count - 1);
                for (int i = 1; i < args.Count; i++)
                {
                    string? field = args[i].CastToString();
                    if (string.IsNullOrEmpty(field))
                    {
                        return DynValue.Nil;
                    }

                    fields.Add(field!);
                }
            }

            return GetKeyedView(table, fields);
        }));
        cfg.Set("singleton", DynValue.NewCallback((ctx, args) =>
            GetSingletonRow(ResolveTable(args.Count > 0 ? args[0].CastToString() : null))));
        cfg.Set("tableInfo", DynValue.NewCallback((ctx, args) =>
            GetTableInfo(ResolveTable(args.Count > 0 ? args[0].CastToString() : null))));
        cfg.Set("ref", DynValue.NewCallback((_, args) => ResolveRef(args)));

        return ToReadOnlyTable(script, cfg).Table;
    }

    /// <summary>
    /// Returns the table's rows keyed by the selected index, mirroring the
    /// <c>DataMap</c> / <c>GetByXxx</c> / <c>Get(k1, k2)</c> APIs of Luban's
    /// generated code. A union index produces one nesting level per component.
    /// </summary>
    private DynValue GetKeyedView(DefTable? table, IReadOnlyList<string>? requestedFields)
    {
        if (table == null)
        {
            return DynValue.Nil;
        }

        List<TableIndex> indexes = GetTableIndexes(table);
        TableIndex? index = SelectIndex(table, indexes, requestedFields);
        if (index == null)
        {
            return DynValue.Nil;
        }

        if (!_keyedViews.TryGetValue(table, out Dictionary<string, Table>? byIndex))
        {
            byIndex = new Dictionary<string, Table>(StringComparer.Ordinal);
            _keyedViews[table] = byIndex;
        }

        if (byIndex.TryGetValue(index.Spec, out Table? cached))
        {
            return DynValue.NewTable(cached);
        }

        Dictionary<string, Record>? keyMap = GetKeyMap(table, index);
        if (keyMap == null)
        {
            return DynValue.Nil;
        }

        Table root = new(_script!);
        Dictionary<string, Table> levels = new(StringComparer.Ordinal) { [string.Empty] = root };

        foreach ((string joinedKey, Record record) in keyMap)
        {
            if (!_rowTables.TryGetValue(record, out Table? row))
            {
                continue;
            }

            string[] parts = index.FieldNames.Count == 1 ? new[] { joinedKey } : joinedKey.Split(KeySeparator);
            Table current = root;
            string prefix = string.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                prefix = i == 0 ? parts[i] : prefix + KeySeparator + parts[i];
                if (i == parts.Length - 1)
                {
                    SetKey(current, parts[i], DynValue.NewTable(row));
                    break;
                }

                if (!levels.TryGetValue(prefix, out Table? child))
                {
                    child = new Table(_script!);
                    levels[prefix] = child;
                    SetKey(current, parts[i], DynValue.NewTable(child));
                }

                current = child;
            }
        }

        Table proxy = MakeReadOnlyTree(root, levels);
        byIndex[index.Spec] = proxy;
        return DynValue.NewTable(proxy);
    }

    /// <summary>
    /// Wraps every level of a keyed view read-only, bottom-up. Only the levels
    /// built here are wrapped — the leaves are the very row objects that
    /// <c>cfg.tables</c> exposes, so rules keep comparing rows by identity.
    /// </summary>
    private Table MakeReadOnlyTree(Table root, Dictionary<string, Table> levels)
    {
        HashSet<Table> intermediate = new(levels.Values);

        Table Wrap(Table raw)
        {
            List<TablePair> children = raw.Pairs
                .Where(p => p.Value.Type == DataType.Table && p.Value.Table != null
                    && intermediate.Contains(p.Value.Table))
                .ToList();

            // Replace the children while raw still has no metatable of its own.
            foreach (TablePair child in children)
            {
                raw.Set(child.Key, DynValue.NewTable(Wrap(child.Value.Table)));
            }

            return ToReadOnlyTable(_script!, raw).Table;
        }

        return Wrap(root);
    }

    /// <summary>
    /// Registers a canonical string key and, when it is integral, the numeric
    /// form as well: rules naturally write <c>keyed[10001]</c>, not
    /// <c>keyed["10001"]</c>.
    /// </summary>
    private static void SetKey(Table table, string key, DynValue value)
    {
        table.Set(key, value);
        if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numeric))
        {
            table.Set(DynValue.NewNumber(numeric), value);
        }
    }

    /// <summary>Returns the only row of a <c>mode="one"</c> table, or nil.</summary>
    private DynValue GetSingletonRow(DefTable? table)
    {
        if (table == null)
        {
            return DynValue.Nil;
        }

        if (!table.IsSingletonTable)
        {
            WarnOnce($"cfg.singleton(\"{table.FullName}\") is not available: the table is not a singleton (mode != one); its rows are in cfg.tables.{table.Name}");
            return DynValue.Nil;
        }

        if (!_loadedTables.Contains(table))
        {
            return DynValue.Nil;
        }

        List<Record> records = _context.GetTableAllDataList(table);
        return records.Count == 1 && _rowTables.TryGetValue(records[0], out Table? row)
            ? DynValue.NewTable(row)
            : DynValue.Nil;
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
        List<TableIndex> declared = GetTableIndexes(table);

        Table indexes = new(_script!);
        foreach (TableIndex index in declared)
        {
            Table fields = new(_script!);
            foreach (string field in index.FieldNames)
            {
                fields.Append(DynValue.NewString(field));
            }

            Table keyTypes = new(_script!);
            foreach (string keyType in index.KeyTypes)
            {
                keyTypes.Append(DynValue.NewString(keyType));
            }

            Table entry = new(_script!);
            entry.Set("spec", DynValue.NewString(index.Spec));
            entry.Set("fields", ToReadOnlyTable(_script!, fields));
            entry.Set("keyTypes", ToReadOnlyTable(_script!, keyTypes));
            entry.Set("union", DynValue.NewBoolean(index.Union));
            indexes.Append(DynValue.NewTable(ToReadOnlyTable(_script!, entry).Table));
        }

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
        info.Set("keyed", DynValue.NewBoolean(loaded && declared.Count > 0));
        info.Set("indexes", ToReadOnlyTable(_script!, indexes));
        info.Set("unionIndex", DynValue.NewBoolean(declared.Any(i => i.Union)));
        info.Set("multiKey", DynValue.NewBoolean(table.MultiKey));
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

        // cfg.ref("full.table.name", key...) - look a row up by its index.
        if (args[0].Type == DataType.String)
        {
            DefTable? table = ResolveTable(args[0].String);
            if (table == null)
            {
                return DynValue.Nil;
            }

            List<TableIndex> indexes = GetTableIndexes(table);
            int keyCount = args.Count - 1;
            TableIndex? index = keyCount == 1
                ? SingleIndexForLookup(table, indexes)
                : indexes.FirstOrDefault(i => i.Union && i.FieldNames.Count == keyCount);

            if (index == null)
            {
                if (keyCount > 1)
                {
                    WarnOnce($"cfg.ref(\"{table.FullName}\", ...) is not available: the table has no union index over {keyCount} fields (declared: {DescribeIndexes(indexes)})");
                }

                return DynValue.Nil;
            }

            return LookupRow(table, index, JoinedLuaKey(args, 1, keyCount));
        }

        return DynValue.Nil;
    }

    /// <summary>
    /// Resolves a single-value lookup. Only a table whose sole index is one field
    /// is unambiguous: several independent indexes need a field name, and a union
    /// index needs all of its components.
    /// </summary>
    private TableIndex? SingleIndexForLookup(DefTable table, List<TableIndex> indexes)
    {
        if (indexes.Count == 1 && !indexes[0].Union && indexes[0].FieldNames.Count == 1)
        {
            return indexes[0];
        }

        if (indexes.Count == 1 && indexes[0].Union)
        {
            WarnOnce($"cfg.ref(\"{table.FullName}\", key...) needs all {indexes[0].FieldNames.Count} values of its union index ({indexes[0].FieldList})");
        }
        else if (indexes.Count > 1)
        {
            WarnOnce($"cfg.ref(\"{table.FullName}\", key) is ambiguous: the table declares {indexes.Count} independent indexes; use cfg.keyed(\"{table.FullName}\", \"<field>\")[key] instead");
        }
        else
        {
            WarnOnce($"cfg.ref(\"{table.FullName}\", key) is not available: the table declares no index");
        }

        return null;
    }

    /// <summary>Resolves a <c>#ref</c> through the target table's only index.</summary>
    private DynValue LookupBySingleValue(DefTable? table, string? canonicalKey)
    {
        if (table == null || canonicalKey == null)
        {
            return DynValue.Nil;
        }

        List<TableIndex> indexes = GetTableIndexes(table);
        if (indexes.Count != 1 || indexes[0].FieldNames.Count != 1)
        {
            WarnOnce($"a #ref points at table '{table.FullName}', which has no single-field index to resolve against");
            return DynValue.Nil;
        }

        return LookupRow(table, indexes[0], canonicalKey);
    }

    private DynValue ToReferencedRow(DefField field, DType? value)
    {
        if (value == null)
        {
            return DynValue.Nil;
        }

        if (TypeTemplateExtension.CanGenerateRef(field))
        {
            return LookupBySingleValue(TypeTemplateExtension.GetRefTable(field), CanonicalDataKey(value));
        }

        if (TypeTemplateExtension.CanGenerateCollectionRef(field) && value.Datas != null)
        {
            // Elements whose target is missing are omitted, so the result is a
            // hole-free array but may be shorter than the source list.
            DefTable? target = TypeTemplateExtension.GetCollectionRefTable(field);
            Table result = new(_script!);
            foreach (DType element in value.Datas)
            {
                DynValue row = LookupBySingleValue(target, CanonicalDataKey(element));
                if (!row.IsNil())
                {
                    result.Append(row);
                }
            }

            return DynValue.NewTable(result);
        }

        return DynValue.Nil;
    }

    private DynValue LookupRow(DefTable? table, TableIndex index, string? joinedKey)
    {
        if (table == null || joinedKey == null)
        {
            return DynValue.Nil;
        }

        Dictionary<string, Record>? keyMap = GetKeyMap(table, index);
        if (keyMap != null && keyMap.TryGetValue(joinedKey, out Record? record)
            && _rowTables.TryGetValue(record, out Table? row))
        {
            return DynValue.NewTable(row);
        }

        return DynValue.Nil;
    }

    /// <summary>
    /// Builds and caches the canonical key to record map for one declared index.
    /// A union index joins its components so the map stays flat here; the Lua
    /// view nests them back into one level per component.
    /// </summary>
    private Dictionary<string, Record>? GetKeyMap(DefTable table, TableIndex index)
    {
        if (_keyMaps.TryGetValue(table, out Dictionary<string, Dictionary<string, Record>?>? byIndex)
            && byIndex.TryGetValue(index.Spec, out Dictionary<string, Record>? cached))
        {
            return cached;
        }

        Dictionary<string, Record>? map = null;
        if (!_loadedTables.Contains(table))
        {
            WarnOnce($"cfg.ref()/cfg.keyed() cannot index table '{table.FullName}': it is not part of the current export target, so its rows are unavailable");
        }
        else
        {
            map = new Dictionary<string, Record>(StringComparer.Ordinal);
            foreach (Record record in _context.GetTableAllDataList(table))
            {
                string? joinedKey = JoinedDataKey(record.Data, index);
                if (joinedKey != null)
                {
                    map[joinedKey] = record;
                }
            }
        }

        if (byIndex == null)
        {
            byIndex = new Dictionary<string, Dictionary<string, Record>?>(StringComparer.Ordinal);
            _keyMaps[table] = byIndex;
        }

        byIndex[index.Spec] = map;
        return map;
    }

    private static string? JoinedDataKey(DBean? bean, TableIndex index)
    {
        if (bean == null)
        {
            return null;
        }

        string[] parts = new string[index.FieldNames.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            string? part = CanonicalDataKey(bean.GetField(index.FieldNames[i]));
            if (part == null)
            {
                return null;
            }

            parts[i] = part;
        }

        return string.Join(KeySeparator, parts);
    }

    private static string? JoinedLuaKey(CallbackArguments args, int start, int count)
    {
        string[] parts = new string[count];
        for (int i = 0; i < count; i++)
        {
            string? part = CanonicalLuaKey(args[start + i]);
            if (part == null)
            {
                return null;
            }

            parts[i] = part;
        }

        return string.Join(KeySeparator, parts);
    }

    private void WarnOnce(string message)
    {
        if (_warnings.Add(message))
        {
            Logger.Warn("lua validator: {0}", message);
        }
    }

    /// <summary>One declared index: a single field, or the fields of a union index.</summary>
    private sealed class TableIndex
    {
        public TableIndex(string spec, bool union, List<string> fieldNames, List<string> keyTypes)
        {
            Spec = spec;
            Union = union;
            FieldNames = fieldNames;
            KeyTypes = keyTypes;
        }

        public string Spec { get; }

        public bool Union { get; }

        public List<string> FieldNames { get; }

        public List<string> KeyTypes { get; }

        public string FieldList => string.Join(", ", FieldNames);
    }

    /// <summary>
    /// Mirrors how Luban itself models indexes. A map table has a single index
    /// field; a list table has either several independent indexes
    /// (<c>index="a,b"</c>, each unique on its own) or one union index whose
    /// fields are unique only together (<c>index="a+b"</c>); a singleton table
    /// (<c>mode="one"</c>) has none.
    /// </summary>
    private static List<TableIndex> GetTableIndexes(DefTable table)
    {
        List<TableIndex> indexes = new();

        if (table.IsUnionIndex && table.IndexList != null && table.IndexList.Count > 0)
        {
            // Luban sets IsUnionIndex for any table whose key is a single tuple,
            // which includes a plain map table keyed by one field. Here "union"
            // means what a rule cares about: unique only when several fields are
            // combined, so it needs one nesting level per field.
            List<string> fieldNames = table.IndexList.Select(i => i.IndexField.Name).ToList();
            indexes.Add(new TableIndex(
                table.Index ?? string.Join("+", fieldNames),
                fieldNames.Count > 1,
                fieldNames,
                table.IndexList.Select(i => i.Type?.TypeName ?? string.Empty).ToList()));
            return indexes;
        }

        if (table.IndexList != null)
        {
            foreach (IndexInfo info in table.IndexList)
            {
                indexes.Add(new TableIndex(
                    info.IndexField.Name,
                    false,
                    new List<string> { info.IndexField.Name },
                    new List<string> { info.Type?.TypeName ?? string.Empty }));
            }
        }

        if (indexes.Count == 0 && table.IndexField != null)
        {
            indexes.Add(new TableIndex(
                table.IndexField.Name,
                false,
                new List<string> { table.IndexField.Name },
                new List<string> { table.KeyTType?.TypeName ?? string.Empty }));
        }

        return indexes;
    }

    private static string DescribeIndexes(List<TableIndex> indexes)
    {
        return indexes.Count == 0
            ? "none"
            : string.Join(" | ", indexes.Select(i => i.Union ? "(" + i.FieldList + ")" : i.FieldList));
    }

    /// <summary>
    /// Picks the index a <c>cfg.keyed</c> call refers to. Naming no field works
    /// only when the choice is unambiguous, which includes a single union index.
    /// </summary>
    private TableIndex? SelectIndex(DefTable table, List<TableIndex> indexes, IReadOnlyList<string>? requestedFields)
    {
        if (requestedFields == null || requestedFields.Count == 0)
        {
            if (indexes.Count == 1)
            {
                return indexes[0];
            }

            WarnOnce(indexes.Count == 0
                ? $"cfg.keyed(\"{table.FullName}\") is not available: the table is a singleton or declares no index"
                : $"cfg.keyed(\"{table.FullName}\") is ambiguous: the table declares {indexes.Count} independent indexes, so name one, e.g. cfg.keyed(\"{table.FullName}\", \"{indexes[0].FieldNames[0]}\")");
            return null;
        }

        if (requestedFields.Count == 1)
        {
            string name = requestedFields[0];
            TableIndex? match = indexes.FirstOrDefault(i =>
                !i.Union && i.FieldNames.Count == 1 && string.Equals(i.FieldNames[0], name, StringComparison.Ordinal));
            if (match == null)
            {
                WarnOnce($"cfg.keyed(\"{table.FullName}\", \"{name}\") is not available: the table has no such index (declared: {DescribeIndexes(indexes)})");
            }

            return match;
        }

        TableIndex? union = indexes.FirstOrDefault(i =>
            i.Union && i.FieldNames.Count == requestedFields.Count
            && i.FieldNames.SequenceEqual(requestedFields, StringComparer.Ordinal));
        if (union == null)
        {
            WarnOnce($"cfg.keyed(\"{table.FullName}\", ...) is not available: the table has no union index over ({string.Join(", ", requestedFields)}) (declared: {DescribeIndexes(indexes)})");
        }

        return union;
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
