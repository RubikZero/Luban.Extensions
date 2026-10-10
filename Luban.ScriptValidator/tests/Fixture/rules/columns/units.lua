-- Column rules, loaded while Luban compiles the schema and called once per
-- tagged field per record. Because the call happens after the tables are loaded,
-- a rule can look up other tables; because registration happens before, it cannot
-- touch cfg here at the top level.

register{
    name = "iconName",
    type = "string",
    validate = function(value, row)
        print("[fx-col] iconName " .. tostring(value))
        expect(value ~= "", "icon must not be empty")
    end,
}

-- Which table holds the id depends on the enum value. The enum arrives in Lua as
-- its item name, so the mapping is keyed by name.
local unitTables = { Hero = "TbHero", Monster = "TbMonster" }

register{
    name = "unitRef",
    type = "UnitInfo",
    validate = function(unit, row)
        print("[fx-col] unitRef " .. tostring(unit.unit_id))

        local tableName = unitTables[unit.unit_type]
        expect(tableName ~= nil, "unknown unit type: " .. tostring(unit.unit_type))
        if tableName == nil then
            return
        end

        local keyed = cfg.keyed(tableName)
        expect(keyed ~= nil, tableName .. " is not exported")
        if keyed == nil then
            return
        end

        expect(keyed[unit.unit_id] ~= nil,
            string.format("%s has no unit_id=%d", tableName, unit.unit_id))
    end,
}
