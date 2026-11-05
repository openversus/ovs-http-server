
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local pid = ARGV[1]

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

local newLeader = nil
if l.LeaderID == pid then
  -- C#: the remaining player (not a bot) who joined first, on any team; ties by id. The TS script took the first player
  -- Lua's pairs() gave on the first team with anyone left, a bot included. No player left: the lobby goes.
  local firstAt = nil
  for _, team in ipairs(l.Teams) do
    for otherId, other in pairs(team.Players) do
      if otherId ~= pid and (other.BotSettingSlug == nil or other.BotSettingSlug == '') then
        local at = type(other.JoinedAt) == 'string' and other.JoinedAt or '~'
        if newLeader == nil or at < firstAt or (at == firstAt and otherId < newLeader) then
          newLeader = otherId
          firstAt = at
        end
      end
    end
  end
  if newLeader then
    l.LeaderID = newLeader
    l.ReadyPlayers[newLeader] = true
  else
    if l.LobbyCode and l.LobbyCode ~= '' then
      redis.call('DEL', 'lobby_code:' .. l.LobbyCode)
    end
    redis.call('DEL', KEYS[1])
    return cjson.encode({ playerData = pdata, readyPlayers = {}, leaderID = pid, gameModeSlug = l.GameModeSlug })
  end
end

redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
local leaderID = newLeader or l.LeaderID
return cjson.encode({ playerData = pdata, readyPlayers = l.ReadyPlayers, leaderID = leaderID, gameModeSlug = l.GameModeSlug })
