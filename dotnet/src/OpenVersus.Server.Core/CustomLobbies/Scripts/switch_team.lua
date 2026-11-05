
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)

local pid = ARGV[1]
local targetIdx = tonumber(ARGV[2])
local style = l.match_config.TeamStyle

local fromArrIdx = nil
local fromTeamIdx = nil
local pdata = nil
for i, team in ipairs(l.Teams) do
  if team.Players[pid] then
    fromArrIdx = i
    fromTeamIdx = team.TeamIndex
    pdata = team.Players[pid]
    break
  end
end
if fromArrIdx == nil then return nil end

local targetArrIdx = nil
local targetLen = nil
for i, team in ipairs(l.Teams) do
  if team.TeamIndex == targetIdx then
    targetArrIdx = i
    targetLen = team.Length
    break
  end
end
if targetArrIdx == nil then return nil end

if fromArrIdx == targetArrIdx then
  return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
end

-- C#: any slot the team style has room in, from anywhere (the TS script let a player move between teams only in Duos).
if targetIdx == 4 then
  if targetLen >= 4 then return nil end
elseif style == 'Duos' then
  if targetIdx ~= 0 and targetIdx ~= 1 then return nil end
  if targetLen >= 2 then return nil end
elseif style == 'FFA' then
  if targetIdx < 0 or targetIdx > 3 then return nil end
  if targetLen >= 1 then return nil end
else
  if targetIdx ~= 0 and targetIdx ~= 1 then return nil end
  if targetLen >= 1 then return nil end
end

l.Teams[fromArrIdx].Players[pid] = nil
l.Teams[fromArrIdx].Length = l.Teams[fromArrIdx].Length - 1
l.Teams[targetArrIdx].Players[pid] = pdata
l.Teams[targetArrIdx].Length = l.Teams[targetArrIdx].Length + 1

redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
