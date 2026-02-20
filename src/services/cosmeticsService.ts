import { logger } from "../config/logger";
import { redisGetEquippedCosmetics, redisSaveEquippedCosmetics as redisSaveEquippedCosmetics, redisUpdatePlayerKey } from "../config/redis";
import { Cosmetics, CosmeticsModel, TauntSlotsClass, defaultTaunts } from "../database/Cosmetics";
import { PlayerTesterModel, PlayerTester } from "../database/PlayerTester";
import { getAllTauntsByChar, getAssetsByType } from "../loadAssets";
import { ITaunt, getAllTaunts, getTauntsByChar } from "../data/taunts";

const serviceName = "Services.Cosmetics";

function mergeCosmetics(cosmetics: Cosmetics): Cosmetics {
  const mergedTaunts: Record<string, TauntSlotsClass> = {};

  for (const character of getAssetsByType("CharacterData")) {
    if (cosmetics.Taunts && cosmetics.Taunts[character.slug]) {
      mergedTaunts[character.slug] = cosmetics.Taunts[character.slug];
    }
    else {
      mergedTaunts[character.slug] = {
        TauntSlots: [
          getAllTauntsByChar()[character.slug]?.Slugs?.[0] || "",
          "",
          "",
          "",
        ],
      };
    }
  }

  const mergedCosmetics = {
    ...cosmetics,
    Taunts: mergedTaunts,
  };
  return mergedCosmetics;
}

export async function updateCosmeticsBanner(accountId: string, newBanner: string) {
  var proxyBanner: string | null = await getEquippedCosmetics(accountId).then((cosmetics) => (cosmetics ? cosmetics.Banner : null));

  if (!proxyBanner || undefined === proxyBanner) {
    logger.info(`[${serviceName}]: Setting default banner for AccountId ${accountId} during banner update because no banner was equipped.`);
    proxyBanner = "default_banner";
  }
  else {
    proxyBanner = newBanner;
  }

  if (!newBanner || undefined === newBanner || newBanner === "") {
    logger.warn(`[${serviceName}]: Invalid banner provided, defaulting to "default_banner" during banner update.`);
    proxyBanner = "default_banner";
  }
  else {
    proxyBanner = newBanner;
  }

  const updatedCosmetics = (await CosmeticsModel.findOneAndUpdate(
    { _id: accountId },
    {
      $set: { Banner: proxyBanner },
      $setOnInsert: { account_id: accountId },
    },
    { new: true, upsert: true },
  ).lean()) as Cosmetics;
  await redisSaveEquippedCosmetics(accountId, mergeCosmetics(updatedCosmetics));
}

export async function updateCosmeticsAnnouncerPack(accountId: string, newAnnouncerPack: string) {
  const updatedCosmetics = (await CosmeticsModel.findOneAndUpdate(
    { _id: accountId },
    {
      $set: { AnnouncerPack: newAnnouncerPack },
      $setOnInsert: { account_id: accountId },
    },
    { new: true, upsert: true },
  ).lean()) as Cosmetics;
  await redisSaveEquippedCosmetics(accountId, mergeCosmetics(updatedCosmetics));
}

export async function updateCosmeticsRingoutVfx(accountId: string, newRingoutVfx: string) {
  var proxyRingoutVfx: string | null = await getEquippedCosmetics(accountId).then((cosmetics) => (cosmetics ? cosmetics.RingoutVfx : null));

  if (!proxyRingoutVfx || undefined === proxyRingoutVfx) {
    logger.info(
      `[${serviceName}]: Setting default RingoutVfx for AccountId ${accountId} during RingoutVfx update because no RingoutVfx was equipped.`,
    );
    proxyRingoutVfx = "ring_out_vfx_default";
  }
  else {
    proxyRingoutVfx = newRingoutVfx;
  }

  if (!newRingoutVfx || undefined === newRingoutVfx || newRingoutVfx === "") {
    logger.warn(`[${serviceName}]: Invalid RingoutVfx provided, defaulting to "ring_out_vfx_default" during RingoutVfx update.`);
    proxyRingoutVfx = "ring_out_vfx_default";
  }
  else {
    proxyRingoutVfx = newRingoutVfx;
  }

  const updatedCosmetics = (await CosmeticsModel.findOneAndUpdate(
    { _id: accountId },
    {
      $set: { RingoutVfx: proxyRingoutVfx },
      $setOnInsert: { account_id: accountId },
    },
    { new: true, upsert: true },
  ).lean()) as Cosmetics;
  await redisSaveEquippedCosmetics(accountId, mergeCosmetics(updatedCosmetics));
}

export async function updateCosmeticsStatTrackerSlot(accountId: string, index: number, value: string) {
  let doc = await CosmeticsModel.findById(accountId).lean();

  let statTrackerSlots: string[] = [
    "",
    "",
    "",
  ];

  if (doc && doc.StatTrackers && Array.isArray(doc.StatTrackers.StatTrackerSlots)) {
    statTrackerSlots = [...doc.StatTrackers.StatTrackerSlots];
  }

  statTrackerSlots[index] = value;

  const updatedCosmetics = (await CosmeticsModel.findOneAndUpdate(
    { _id: accountId },
    {
      $set: { "StatTrackers.StatTrackerSlots": statTrackerSlots },
      $setOnInsert: { account_id: accountId },
    },
    { new: true, upsert: true, setDefaultsOnInsert: true },
  ).lean()) as Cosmetics;

  await redisSaveEquippedCosmetics(accountId, mergeCosmetics(updatedCosmetics));
}

export async function updateCosmeticsTauntSlot(accountId: string, character: string, index: number, value: string) {
  let cachedCosmetics = await getEquippedCosmetics(accountId);
  if (cachedCosmetics) {
    cachedCosmetics.Taunts[character].TauntSlots[index] = value;

    const path = `Taunts.${character}.TauntSlots`;
    const updatedCosmetics = (await CosmeticsModel.findOneAndUpdate(
      { _id: accountId },
      {
        $set: { [path]: cachedCosmetics.Taunts[character].TauntSlots },
        $setOnInsert: { account_id: accountId },
      },
      { new: true, upsert: true },
    ).lean()) as Cosmetics;

    await redisSaveEquippedCosmetics(accountId, mergeCosmetics(cachedCosmetics));
  }
  else {
    logger.warn(
      `[${serviceName}]: No cached cosmetics found for AccountId ${accountId} during taunt slot update. This should not happen, as cosmetics should be cached when the player equips a taunt. Creating default cosmetics for this account.`,
    );

    const defaultCosmetics = new CosmeticsModel().toObject();
    //(await CosmeticsModel.create({ ...defaultCosmetics, _id: accountId, account_id: accountId })).save();
    await CosmeticsModel.create({ ...defaultCosmetics, _id: accountId, account_id: accountId });

    let cachedCosmetics = await getEquippedCosmetics(accountId);
    cachedCosmetics.Taunts[character].TauntSlots[index] = value;

    const path = `Taunts.${character}.TauntSlots`;
    const updatedCosmetics = (await CosmeticsModel.findOneAndUpdate(
      { _id: accountId },
      {
        $set: { [path]: cachedCosmetics.Taunts[character].TauntSlots },
        $setOnInsert: { account_id: accountId },
      },
      { new: true, upsert: true },
    ).lean()) as Cosmetics;

    await redisSaveEquippedCosmetics(accountId, mergeCosmetics(cachedCosmetics));
  }
}

export async function getEquippedCosmetics(accountId: string) {
  if (!accountId || undefined === accountId || accountId === "") {
    logger.error(`[${serviceName}]: Invalid accountId provided to getEquippedCosmetics`);
    logger.error(`[${serviceName}]: Stack trace for invalid accountId:`);
    console.trace();
  }

  let cachedCosmetics = await redisGetEquippedCosmetics(accountId);
  let comparisonCosmetics = cachedCosmetics;

  if (cachedCosmetics) {
    if (!cachedCosmetics.Banner) {
      logger.info(
        `[${serviceName}]: Setting default banner for AccountId ${accountId} during getEquippedCosmetics because no banner was equipped.`,
      );
      cachedCosmetics.Banner = "default_banner";
    }
    if (!cachedCosmetics.RingoutVfx) {
      logger.info(
        `[${serviceName}]: Setting default RingoutVfx for AccountId ${accountId} during getEquippedCosmetics because no RingoutVfx was equipped.`,
      );
      cachedCosmetics.RingoutVfx = "ring_out_vfx_default";
    }
    if (!cachedCosmetics.StatTrackers) {
      logger.info(
        `[${serviceName}]: Setting default StatTrackers for AccountId ${accountId} during getEquippedCosmetics because no StatTrackers were equipped.`,
      );
      cachedCosmetics.StatTrackers = {
        StatTrackerSlots: [
          "stat_tracking_bundle_default",
          "stat_tracking_bundle_default",
          "stat_tracking_bundle_default",
        ],
      };
    }

    if (!cachedCosmetics.Taunts) {
      logger.info(
        `[${serviceName}]: Setting default Taunts for AccountId ${accountId} during getEquippedCosmetics because no Taunts were equipped.`,
      );
      //const defaultTaunts: Record<string, TauntSlotsClass> = {};
      // for (const character of getAssetsByType("CharacterData")) {
      //   defaultTaunts[character.slug] = {
      //     TauntSlots: [
      //       getAllTauntsByChar()[character.slug]?.Slugs?.[0] || "",
      //       "",
      //       "",
      //       "",
      //     ],
      //   };
      // }
      cachedCosmetics.Taunts = defaultTaunts;
    }

    if (comparisonCosmetics != cachedCosmetics) {
      await redisSaveEquippedCosmetics(accountId, mergeCosmetics(cachedCosmetics));
    }

    return cachedCosmetics;
  }

  let cosmetics = (await CosmeticsModel.findById(accountId).lean()) as Cosmetics;
  if (!cosmetics) {
    cosmetics = new CosmeticsModel().toObject();
    //   //(await CosmeticsModel.create({ ...cosmetics, _id: accountId, account_id: accountId })).save();
    //   await CosmeticsModel.create({ ...cosmetics, _id: accountId, account_id: accountId });
    //   cosmetics = (await CosmeticsModel.findById(accountId).lean()) as Cosmetics;
    // }
    // const mergedCosmetics = mergeCosmetics(cosmetics);
    // await redisSaveEquippedCosmetics(accountId, mergedCosmetics);
    // return mergedCosmetics;

    // Make absolutely sure account_id is set
    try {
      const createdDoc = await CosmeticsModel.create({
        ...cosmetics,
        _id: accountId,
        account_id: accountId,
      });

      // Re-fetch to ensure we have the saved document
      cosmetics = (await CosmeticsModel.findById(accountId).lean()) as Cosmetics;

      if (!cosmetics) {
        throw new Error(`Failed to create or retrieve cosmetics for account ${accountId}`);
      }
    }
    catch (error) {
      logger.error(`[${serviceName}]: Error creating cosmetics for account ${accountId}:`, error);
      throw error;
    }
  }

  const mergedCosmetics = mergeCosmetics(cosmetics);
  await redisSaveEquippedCosmetics(accountId, mergedCosmetics);
  return mergedCosmetics;
}

// export async function updateProfileIcon(accountId: string, newProfileIcon: string) {
//   await PlayerTesterModel.findOneAndUpdate({ _id: accountId }, { $set: { profile_icon: newProfileIcon } }, { new: true, upsert: true }).lean();
//   await redisUpdatePlayerKey(accountId, "profile_icon", newProfileIcon);
// }

export async function updateProfileIcon(accountId: string, newProfileIcon: string) {
  const updatedCosmetics = (await CosmeticsModel.findOneAndUpdate(
    { _id: accountId },
    {
      $set: { ProfileIcon: newProfileIcon },
      $setOnInsert: { account_id: accountId },
    },
    { new: true, upsert: true },
  ).lean()) as Cosmetics;
  await redisSaveEquippedCosmetics(accountId, mergeCosmetics(updatedCosmetics));
}
