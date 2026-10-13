-- Registers iconName with a declared input type that does not match the value it
-- receives, which has to be rejected while the schema is compiled.

register{
    name = "unitRef",
    type = "int",
    validate = function(value, row, owner)
    end,
}

register{
    name = "wholeRule",
    type = "list",
    validate = function(value, row, owner)
    end,
}

register{
    name = "weightRule",
    type = "int",
    validate = function(value, row, owner)
    end,
}

register{
    name = "memberRule",
    type = "Member",
    validate = function(value, row, owner)
    end,
}

register{
    name = "iconName",
    type = "int",
    validate = function(value, row, owner)
    end,
}
