-- C# only: a player's locked loadout in the lobby, in one step (the TS server rewrote the whole lobby outside any
-- script, losing a change made in between: a ready). ARGV: player, character, skin.
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if type(l.LockedLoadouts) ~= 'table' then l.LockedLoadouts = {} end
l.LockedLoadouts[ARGV[1]] = { Character = ARGV[2], Skin = ARGV[3] }
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return 1
