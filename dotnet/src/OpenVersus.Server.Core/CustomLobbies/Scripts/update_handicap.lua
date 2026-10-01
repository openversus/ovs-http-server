
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
l.Handicaps[ARGV[1]] = tonumber(ARGV[2])
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode(l)
