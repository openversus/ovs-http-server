
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
local botId = ARGV[2]
local teamArrIdx = nil
for i, team in ipairs(l.Teams) do
  if team.Players[botId] then
    teamArrIdx = i
    break
  end
end
if teamArrIdx == nil then return nil end
l.Teams[teamArrIdx].Players[botId].Fighter       = cjson.decode(ARGV[3])
l.Teams[teamArrIdx].Players[botId].Skin           = cjson.decode(ARGV[4])
l.Teams[teamArrIdx].Players[botId].BotSettingSlug = ARGV[5]
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode(l.Teams[teamArrIdx].Players[botId])
