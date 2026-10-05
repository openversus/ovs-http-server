import assert from "node:assert/strict";
import test from "node:test";
import { Types } from "mongoose";
import { getProfileBulk } from "../src/modules/friends/friends.service";
import { getProfileForMatch } from "../src/services/profileService";
import { UPDATE_NOTIFICATION_PROFILES } from "../src/services/updateNotificationProfiles";

test("required-update virtual accounts use valid, unique ObjectId-shaped identifiers", () => {
  const accountIds = UPDATE_NOTIFICATION_PROFILES.map((profile) => profile.accountId);
  const profileIds = UPDATE_NOTIFICATION_PROFILES.map((profile) => profile.profileId);

  assert.equal(new Set(accountIds).size, accountIds.length);
  assert.equal(new Set(profileIds).size, profileIds.length);
  assert.ok(accountIds.every((id) => Types.ObjectId.isValid(id)));
  assert.ok(profileIds.every((id) => Types.ObjectId.isValid(id)));
});

test("match-profile lookup returns the live dotted-field shape for virtual accounts", async () => {
  for (const virtualProfile of UPDATE_NOTIFICATION_PROFILES) {
    const profile = await getProfileForMatch(virtualProfile.accountId);

    assert.equal(profile?.account_id, virtualProfile.accountId);
    assert.equal(profile?.id, virtualProfile.profileId);
    assert.equal(profile?.account.id, virtualProfile.accountId);
    assert.equal(profile?.account.public_id, virtualProfile.accountId);
    assert.equal(profile?.account["identity.username"], virtualProfile.username);
    assert.equal(profile?.account["server_data.ProfileIcon.Slug"], "profile_icon_default");
  }
});

test("bulk-profile lookup returns the live nested-field shape for virtual accounts", async () => {
  const profiles = await getProfileBulk(
    UPDATE_NOTIFICATION_PROFILES.map((profile) => profile.accountId),
  );

  assert.equal(profiles.length, UPDATE_NOTIFICATION_PROFILES.length);
  for (const virtualProfile of UPDATE_NOTIFICATION_PROFILES) {
    const profile = profiles.find((candidate) => candidate.account_id === virtualProfile.accountId);
    assert.equal(profile?.id, virtualProfile.accountId);
    assert.equal(profile?.account.id, virtualProfile.accountId);
    assert.equal(profile?.account.public_id, virtualProfile.accountId);
    assert.equal(profile?.account.identity.username, virtualProfile.username);
    assert.equal(profile?.account.server_data.ProfileIcon.Slug, "profile_icon_default");
  }
});
