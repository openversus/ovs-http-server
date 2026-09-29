export interface UpdateNotificationProfile {
  accountId: string;
  profileId: string;
  username: string;
}

/**
 * The virtual sender of the required-update toast an outdated client gets (see
 * CLIENT_UPDATE_MODAL_CHANNEL in websocket.ts). Its ids are ObjectId-shaped, so the game
 * looks its profile up like any player's, and the profile lookups answer for it.
 */
export const UPDATE_NOTIFICATION_PROFILES: readonly UpdateNotificationProfile[] = [
  {
    accountId: "00000000000000000000a003",
    profileId: "00000000000000000000b003",
    // The toast shows this name; keep it in step with CLIENT_UPDATE_URL (clientUpdateGate.ts).
    username: "Update: github.com/openversus/ovs-client/releases",
  },
];

const updateProfileByAccountId = new Map(
  UPDATE_NOTIFICATION_PROFILES.map((profile) => [profile.accountId, profile]),
);

export function getUpdateNotificationProfile(accountId: string): UpdateNotificationProfile | undefined {
  return updateProfileByAccountId.get(accountId);
}
