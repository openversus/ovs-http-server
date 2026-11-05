
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local target = ARGV[1]
-- C#: only the leader promotes (ARGV[2] is who asks), and only a player, not a bot (the TS script let anyone promote anyone).
if l.LeaderID ~= ARGV[2] then return nil end
local found = false
for _, team in ipairs(l.Teams) do
  local p = team.Players[target]
  if p then found = p.BotSettingSlug == nil or p.BotSettingSlug == ''; break end
end
if not found then return nil end
l.LeaderID = target
l.ReadyPlayers[target] = true
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode(l)
