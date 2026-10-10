-- The same rule names as rules/columns, except that iconName always fails. CI
-- points -x luaValidator.columnDir here to check that a failing column rule is
-- reported like any other validation failure: logged, recorded, the export still
-- written, and exit code 1 only under the strict flag.
--
-- unitRef is registered as well, and with the input type of the field it is
-- attached to, so that this run reaches the failure instead of stopping at a
-- registration that does not match the schema.

register{
    name = "unitRef",
    type = "int",
    validate = function(value, row, owner)
    end,
}

register{
    name = "iconName",
    type = "string",
    validate = function(value, row, owner)
        fail("[lua-column-fail-fixture] deliberate failure")
    end,
}
