-- Deliberately failing rule, used by the shared CI action to check that a Lua
-- failure behaves exactly like a built-in validator failure: it is logged and
-- recorded, the run still exports its output, and only the strict flag
-- (--validationFailAsError on 4.x, --strict on 5.x) turns it into exit code 1.
--
-- The marker is ASCII so the check does not depend on how the runner encodes
-- non-ASCII text.
function validate()
    fail("[lua-fail-fixture] deliberate failure")
end
