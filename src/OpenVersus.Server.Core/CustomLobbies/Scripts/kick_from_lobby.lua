
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return redis.error_reply('Not the leader') end
local pid = ARGV[2]
if pid == ARGV[1] then return redis.error_reply('Cannot kick yourself') end

local fromArrIdx = nil
local pdata = nil
for i, team in ipairs(l.Teams) do
  if team.Players[pid] then
    fromArrIdx = i
    pdata = team.Players[pid]
    break
  end
end
if fromArrIdx == nil then return nil end

l.Teams[fromArrIdx].Players[pid] = nil
l.Teams[fromArrIdx].Length = l.Teams[fromArrIdx].Length - 1
l.ReadyPlayers[pid] = nil
l.PlayerAutoPartyPreferences[pid] = nil
l.PlayerGameplayPreferences[pid] = nil
l.Platforms[pid] = nil
l.LockedLoadouts[pid] = nil
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
