
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local pid = ARGV[1]
local ready = ARGV[2] == 'true'
if ready then
  l.ReadyPlayers[pid] = true
else
  l.ReadyPlayers[pid] = nil
end
local readyCount = 0
for _ in pairs(l.ReadyPlayers) do readyCount = readyCount + 1 end
local totalPlayers = 0
for _, team in ipairs(l.Teams) do
  if team.TeamIndex ~= 4 then totalPlayers = totalPlayers + team.Length end
end
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return (readyCount >= totalPlayers and totalPlayers > 0) and 1 or 0
