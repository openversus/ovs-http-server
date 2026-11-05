
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end

local newConfig = cjson.decode(ARGV[3])
local newStyle  = newConfig.TeamStyle
local oldStyle  = l.match_config.TeamStyle

if oldStyle ~= newStyle then
  local players = {}
  for _, team in ipairs(l.Teams) do
    if team.TeamIndex ~= 4 then
      for pid, pdata in pairs(team.Players) do
        table.insert(players, { pid = pid, pdata = pdata })
      end
    end
  end
  local total = #players

  if newStyle == 'Solos' and total > 2 then
    return cjson.encode(l)
  end

  local teamByIdx = {}
  for i, team in ipairs(l.Teams) do
    teamByIdx[team.TeamIndex] = i
  end

  for _, team in ipairs(l.Teams) do
    if team.TeamIndex ~= 4 then
      team.Players = {}
      team.Length   = 0
    end
  end

  for i, player in ipairs(players) do
    local targetTeamIdx
    if newStyle == 'FFA' then
      targetTeamIdx = i - 1
    elseif newStyle == 'Duos' then
      targetTeamIdx = math.floor((i - 1) / 2)
    elseif newStyle == 'Solos' then
      targetTeamIdx = i - 1
    else
      targetTeamIdx = 0
    end
    local arrIdx = teamByIdx[targetTeamIdx]
    if arrIdx then
      l.Teams[arrIdx].Players[player.pid] = player.pdata
      l.Teams[arrIdx].Length = l.Teams[arrIdx].Length + 1
    end
  end
end

l.GameModeSlug = ARGV[2]
l.match_config = cjson.decode(ARGV[3])
l.Maps         = cjson.decode(ARGV[4])
l.WorldBuffs   = cjson.decode(ARGV[5])

local matrix = cjson.decode(ARGV[6])
local newBuffs = {}
for _, team in ipairs(l.Teams) do
  local teamKey = tostring(team.TeamIndex)
  local teamData = matrix[teamKey]
  if teamData then
    local teamBuffs = teamData.teamBuffs or {}
    local playerBuffsMap = teamData.players or {}
    local slotIdx = 0
    for pid, _ in pairs(team.Players) do
      local playerSpecificBuffs = playerBuffsMap[tostring(slotIdx)] or {}
      local combined = {}
      for _, b in ipairs(playerSpecificBuffs) do table.insert(combined, b) end
      for _, b in ipairs(teamBuffs) do table.insert(combined, b) end
      if #combined > 0 then newBuffs[pid] = combined end
      slotIdx = slotIdx + 1
    end
  end
end
l.PlayerBuffs = newBuffs
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode(l)
