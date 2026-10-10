-- The same rule names as rules/columns, except that iconName always fails. CI
-- points -x luaValidator.columnDir here to check that a failing column rule is
-- reported like any other validation failure: logged, recorded, the export still
-- written, and exit code 1 only under the strict flag.
--
-- unitRef is registered as well so that this run reaches the failure instead of
-- stopping at an unresolved rule name.

register{
    name = "unitRef",
    type = "UnitInfo",
    validate = function(unit, row)
    end,
}

register{
    name = "iconName",
    type = "string",
    validate = function(value, row)
        fail("[lua-column-fail-fixture] deliberate failure")
    end,
}
