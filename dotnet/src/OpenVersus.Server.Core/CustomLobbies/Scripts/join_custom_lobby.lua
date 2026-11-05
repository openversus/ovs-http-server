
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
-- C#: a player already in the lobby is not added again (the TS script put them in a second team).
for _, team in ipairs(l.Teams) do
  if team.Players[ARGV[1]] then return 'member' end
end
if l.IsLobbyJoinable == false then return cjson.encode(false) end

local accountId   = ARGV[1]
local isSpectator = ARGV[2] == 'true'
local ts          = ARGV[3]
local loadout     = ARGV[4]
local gameplayPrefs = tonumber(ARGV[5])
local style       = l.match_config.TeamStyle

local totalPlayers = 0
for _, team in ipairs(l.Teams) do
  totalPlayers = totalPlayers + team.Length
end

local targetArrIdx = nil
for i, team in ipairs(l.Teams) do
  local ok = false
  if isSpectator then
    ok = team.TeamIndex == 4 and team.Length < 4
  elseif style == 'FFA' then
    ok = team.TeamIndex ~= 4 and team.Length < 1
  elseif style == 'Solos' or style == 'Other' then
    ok = (team.TeamIndex == 0 or team.TeamIndex == 1) and team.Length < 1
  elseif style == 'Duos' then
    ok = (team.TeamIndex == 0 or team.TeamIndex == 1) and team.Length < 2
  end
  if ok then
    targetArrIdx = i
    break
  end
end
if targetArrIdx == nil then
  if not isSpectator then
    for i, team in ipairs(l.Teams) do
      if team.TeamIndex == 4 and team.Length < 4 then
        targetArrIdx = i
        isSpectator = true
        break
      end
    end
    if targetArrIdx == nil then return nil end
  end
end

local pdata = {
  Account           = { id = accountId },
  JoinedAt          = ts,
  BotSettingSlug    = '',
  LobbyPlayerIndex  = totalPlayers,
  CrossplayPreference = 1
}

l.Teams[targetArrIdx].Players[accountId] = pdata
l.Teams[targetArrIdx].Length = l.Teams[targetArrIdx].Length + 1
l.PlayerAutoPartyPreferences[accountId] = false
l.PlayerGameplayPreferences[accountId] = gameplayPrefs or 964
l.Platforms[accountId] = 'PC'
if loadout ~= '' then
  l.LockedLoadouts[accountId] = cjson.decode(loadout)
end
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], 172800)
return cjson.encode(l)
