-- Fixture verification for multi-key and singleton tables.
function validate()
    print("[fx] ===== tableInfo.indexes =====")
    for _, name in ipairs({ "TbSingle", "TbMulti", "TbUnion", "TbOne" }) do
        local info = cfg.tableInfo(name)
        local parts = {}
        for i = 1, #info.indexes do
            local fields = {}
            for j = 1, #info.indexes[i].fields do fields[j] = info.indexes[i].fields[j] end
            parts[i] = "{" .. table.concat(fields, "+") .. " union=" .. tostring(info.indexes[i].union) .. "}"
        end
        print(string.format("[fx] %-9s mode=%-5s index='%s' unionIndex=%-5s multiKey=%-5s keyed=%-5s indexes=%s",
            name, info.mode, info.index, tostring(info.unionIndex), tostring(info.multiKey),
            tostring(info.keyed), table.concat(parts, " ")))
    end

    print("[fx] ===== map table =====")
    local single = cfg.keyed("TbSingle")
    print("[fx] single[1]~=nil=" .. tostring(single[1] ~= nil)
        .. " single['2']~=nil=" .. tostring(single["2"] ~= nil) .. " single[9]=" .. tostring(single[9]))
    print("[fx] cfg.keyed('TbSingle','id')[3].name = " .. tostring(cfg.keyed("TbSingle", "id")[3] and cfg.keyed("TbSingle", "id")[3].name))
    print("[fx] cfg.ref('TbSingle', 3).name = " .. tostring(cfg.ref("TbSingle", 3) and cfg.ref("TbSingle", 3).name))
    print("[fx] cfg.ref('TbSingle', 999) = " .. tostring(cfg.ref("TbSingle", 999)))

    print("[fx] ===== independent indexes (index='id,name') =====")
    print("[fx] cfg.keyed('TbMulti') = " .. tostring(cfg.keyed("TbMulti")))
    local byId = cfg.keyed("TbMulti", "id")
    local byName = cfg.keyed("TbMulti", "name")
    print("[fx] byId[1].name = " .. tostring(byId[1] and byId[1].name))
    print("[fx] byName['two'].id = " .. tostring(byName["two"] and byName["two"].id))
    print("[fx] cfg.ref('TbMulti', 1) = " .. tostring(cfg.ref("TbMulti", 1)))

    print("[fx] ===== union index (index='kind+level') =====")
    local union = cfg.keyed("TbUnion")
    print("[fx] union['alpha'][10].name = " .. tostring(union["alpha"] and union["alpha"][10] and union["alpha"][10].name))
    print("[fx] union['alpha'][20].name = " .. tostring(union["alpha"] and union["alpha"][20] and union["alpha"][20].name))
    print("[fx] union['beta'][10].name = " .. tostring(union["beta"] and union["beta"][10] and union["beta"][10].name))
    print("[fx] union['beta'][99] = " .. tostring(union["beta"] and union["beta"][99]))
    local union2 = cfg.keyed("TbUnion", "kind", "level")
    print("[fx] cfg.keyed('TbUnion','kind','level')['alpha'][20].id = " .. tostring(union2["alpha"][20] and union2["alpha"][20].id))
    print("[fx] cfg.ref('TbUnion','alpha',20).name = " .. tostring(cfg.ref("TbUnion", "alpha", 20) and cfg.ref("TbUnion", "alpha", 20).name))
    print("[fx] cfg.ref('TbUnion','alpha') = " .. tostring(cfg.ref("TbUnion", "alpha")))
    print("[fx] cfg.ref('TbUnion','alpha',10,99) = " .. tostring(cfg.ref("TbUnion", "alpha", 10, 99)))
    print("[fx] cfg.keyed('TbUnion','kind') = " .. tostring(cfg.keyed("TbUnion", "kind")))

    print("[fx] ===== singleton =====")
    local one = cfg.singleton("TbOne")
    print("[fx] cfg.singleton('TbOne').name = " .. tostring(one and one.name))
    print("[fx] cfg.singleton('TbSingle') = " .. tostring(cfg.singleton("TbSingle")))

    print("[fx] ===== identity with cfg.tables =====")
    print("[fx] byId[1] == cfg.tables['test.TbMulti'][1] : " .. tostring(byId[1] == cfg.tables["test.TbMulti"][1]))
    print("[fx] union['alpha'][10] == cfg.ref(...) : " .. tostring(cfg.ref("TbUnion", "alpha", 10) == union["alpha"][10]))
    print("[fx] cfg.table('TbMulti') == cfg.tables['test.TbMulti'] : " .. tostring(cfg.table("TbMulti") == cfg.tables["test.TbMulti"]))

    print("[fx] ===== read-only =====")
    print("[fx] pcall(write leaf) ok? = " .. tostring(pcall(function() union["alpha"][10] = 1 end)))
    print("[fx] pcall(write level) ok? = " .. tostring(pcall(function() union["alpha"] = 1 end)))
    print("[fx] pcall(write root) ok? = " .. tostring(pcall(function() single[1] = 1 end)))

    -- The path-tagged field is an ordinary string here: whatever the path
    -- validator does with it must not change what Lua sees.
    print("[fx] ===== field with a validator tag =====")
    local assets = cfg.keyed("TbAsset")
    print("[fx] cfg.keyed('TbAsset')[1].icon = " .. tostring(assets[1] and assets[1].icon))
end
