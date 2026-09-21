using Luban.Datas;
using Luban.Defs;
using MoonSharp.Interpreter;

namespace Luban.ScriptValidator;

/// <summary>Builds a deep, read-only Lua representation of Luban's loaded tables.</summary>
internal sealed class LuaConfigData
{
    private readonly Dictionary<string, List<Record>> _tables;

    private LuaConfigData(Dictionary<string, List<Record>> tables)
    {
        _tables = tables;
    }

    public static LuaConfigData Create(GenerationContext context)
    {
        Dictionary<string, List<Record>> tables = new(StringComparer.Ordinal);
        foreach (DefTable table in context.Tables)
        {
            tables.Add(table.FullName, context.GetTableAllDataList(table));
        }
        return new LuaConfigData(tables);
    }

    public Table CreateLuaTable(Script script)
    {
        Table tableRegistry = new(script);
        foreach ((string tableName, List<Record> records) in _tables)
        {
            Table rows = new(script);
            foreach (Record record in records)
            {
                Table row = ToLuaBean(script, record.Data);
                row.Set("__source", DynValue.NewString(record.Source ?? string.Empty));
                row.Set("__autoIndex", DynValue.NewNumber(record.AutoIndex));
                rows.Append(ToReadOnlyTable(script, row));
            }
            tableRegistry.Set(tableName, ToReadOnlyTable(script, rows));
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
        return ToReadOnlyTable(script, cfg).Table;
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
        return table;
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
            DLong value => DynValue.NewString(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
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
