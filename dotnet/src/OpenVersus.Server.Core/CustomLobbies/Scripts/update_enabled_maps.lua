
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
local enabled = {}
for i = 2, #ARGV do enabled[ARGV[i]] = true end
for i, m in ipairs(l.Maps) do
  l.Maps[i].IsSelected = enabled[m.Map] ~= nil
end
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode(l.Maps)
