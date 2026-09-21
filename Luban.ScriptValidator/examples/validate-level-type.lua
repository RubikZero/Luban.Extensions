-- Copy this file into the directory passed to -x luaValidator.scriptDir=...
-- and replace the table/field names with the names in your project.
function validate()
    local levelTypes = {}
    for _, row in ipairs(cfg.table("TbLevelType")) do
        levelTypes[row.Type] = true
    end

    for _, level in ipairs(cfg.table("TbLevel")) do
        expect(levelTypes[level.LevelType],
            string.format("TbLevel id=%s (%s): LevelType '%s' is not defined in TbLevelType",
                tostring(level.Id), level.__source, tostring(level.LevelType)))
    end
end
