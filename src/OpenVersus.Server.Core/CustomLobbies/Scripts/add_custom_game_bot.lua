
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
local botId     = ARGV[2]
local targetIdx = tonumber(ARGV[3])
local targetArrIdx = nil
local globalPlayerCount = 0
for i, team in ipairs(l.Teams) do
  globalPlayerCount = globalPlayerCount + team.Length
  if team.TeamIndex == targetIdx then
    targetArrIdx = i
  end
end
if targetArrIdx == nil then return nil end
local pdata = cjson.decode(ARGV[4])
pdata.LobbyPlayerIndex = globalPlayerCount
l.Teams[targetArrIdx].Players[botId] = pdata
l.Teams[targetArrIdx].Length = l.Teams[targetArrIdx].Length + 1
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
