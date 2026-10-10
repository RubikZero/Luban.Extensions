-- Column rules, loaded while Luban compiles the schema and called once per
-- tagged field per record. Because the call happens after the tables are loaded,
-- a rule can look up other tables; because registration happens before, it cannot
-- touch cfg here at the top level.

-- Attached to a field of the record, so the owner of the field is the record
-- itself: owner and row are the same table.
register{
    name = "iconName",
    type = "string",
    validate = function(value, row, owner)
        print("[fx-col] iconName " .. tostring(value))
        expect(owner == row, "a field of the record is owned by the record")
        expect(value ~= "", "icon must not be empty")
    end,
}

-- Declared inside the bean, so it applies to every place UnitInfo is used. The
-- enum arrives in Lua as its item name, so the mapping is keyed by name, and the
-- id is checked in whichever table that name points at. `owner` is the UnitInfo
-- the field belongs to, which is what makes this independent of the use site.
local unitTables = { Hero = "TbHero", Monster = "TbMonster" }

register{
    name = "unitRef",
    type = "int",
    validate = function(value, row, owner)
        local unitType = owner ~= nil and owner.unit_type or nil
        print(string.format("[fx-col] unitRef %s:%s", tostring(unitType), tostring(value)))

        local tableName = unitTables[unitType]
        expect(tableName ~= nil, "unknown unit type: " .. tostring(unitType))
        if tableName == nil then
            return
        end

        local keyed = cfg.keyed(tableName)
        expect(keyed ~= nil, tableName .. " is not exported")
        if keyed == nil then
            return
        end

        expect(keyed[value] ~= nil,
            string.format("%s has no unit_id=%d", tableName, value))
    end,
}
