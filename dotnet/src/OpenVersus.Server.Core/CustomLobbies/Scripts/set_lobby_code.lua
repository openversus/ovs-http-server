-- C# only: the lobby's code, in one step (the TS server rewrote the whole lobby outside any script). ARGV: the code.
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
l.LobbyCode = ARGV[1]
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return 1
