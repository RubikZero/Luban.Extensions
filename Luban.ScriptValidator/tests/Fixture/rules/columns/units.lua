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
        -- Sheets spell an enum by item name, alias or numeric value; canonicalise
        -- before keying the mapping. TbUnit row 2 spells it as "2".
        local raw = owner ~= nil and owner.unit_type or nil
        local unitType = raw ~= nil and (cfg.enumName("UnitType", raw) or raw) or nil
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

-- A tag inside the container's parentheses attaches to the container, so this one
-- runs once per record and receives the whole collection.
register{
    name = "wholeRule",
    type = "list",
    validate = function(value, row, owner)
        local parts = {}
        for _, item in ipairs(value) do
            parts[#parts + 1] = tostring(item)
        end

        print(string.format("[fx-col] wholeRule count=%d values=%s", #value, table.concat(parts, "|")))
        expect(owner == row, "a field of the record is owned by the record")
        expect(#value > 0, "the list must not be empty")
    end,
}

-- A tag after the element type attaches to the element, so these two run once per
-- element and receive the element as their value. The declaration describes the
-- element, not the field: "int" for a list<int>, and the bean's name for a
-- list<Member>.
register{
    name = "weightRule",
    type = "int",
    validate = function(value, row, owner)
        print("[fx-col] weightRule " .. tostring(value))
        expect(owner == row, "an element of a record's field is owned by the record")
        expect(value > 0, "weight must be positive")
    end,
}

register{
    name = "memberRule",
    type = "Member",
    validate = function(value, row, owner)
        -- The member is a bean, so its own fields are readable, and the whole
        -- collection is reachable through the owner.
        local unitType = cfg.enumName("UnitType", value.member_type) or value.member_type
        print(string.format("[fx-col] memberRule %s:%s", tostring(unitType), tostring(value.member_id)))

        expect(owner == row, "an element of a record's field is owned by the record")
        expect(owner ~= nil and owner.scores ~= nil, "the owner still exposes the collection")
    end,
}
