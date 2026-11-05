-- C# only: a match's end takes every ready flag down, in one step (the TS match end rewrote the whole lobby outside any
-- script, losing a change made in between). No ARGV. nil when there is no lobby.
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
l.ReadyPlayers = {}
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return 1
