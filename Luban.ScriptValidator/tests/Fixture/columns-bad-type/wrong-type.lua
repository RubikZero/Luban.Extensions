-- Registers iconName with a declared input type that does not match the field it
-- is attached to, which has to be rejected while the schema is compiled.

register{
    name = "unitRef",
    type = "UnitInfo",
    validate = function(unit, row)
    end,
}

register{
    name = "iconName",
    type = "int",
    validate = function(value, row)
    end,
}
