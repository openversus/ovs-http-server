export interface UpdateNotificationProfile {
  accountId: string;
  profileId: string;
  username: string;
}

/**
 * Virtual accounts used only by the legacy required-update notification test.
 * They are valid ObjectId-shaped identifiers so the game's normal profile
 * resolution path treats them exactly like live player accounts.
 */
export const UPDATE_NOTIFICATION_PROFILES: readonly UpdateNotificationProfile[] = [
  {
    accountId: "00000000000000000000a001",
    profileId: "00000000000000000000b001",
    username: "OPENVERSUS UPDATE",
  },
  {
    accountId: "00000000000000000000a002",
    profileId: "00000000000000000000b002",
    username: "REQUIRED TO PLAY ONLINE",
  },
  {
    accountId: "00000000000000000000a003",
    profileId: "00000000000000000000b003",
    username: "Update required - https://prod.openversus.org/update",
  },
];

const updateProfileByAccountId = new Map(
  UPDATE_NOTIFICATION_PROFILES.map((profile) => [profile.accountId, profile]),
);

export function getUpdateNotificationProfile(accountId: string): UpdateNotificationProfile | undefined {
  return updateProfileByAccountId.get(accountId);
}
