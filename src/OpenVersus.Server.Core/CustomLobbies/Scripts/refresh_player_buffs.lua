
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local matrix = cjson.decode(ARGV[1])
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
return 1
