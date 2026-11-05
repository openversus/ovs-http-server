import { logger } from "../config/logger";
import { Request, Response } from "express";
import { Types } from "mongoose";
import { CosmeticsModel } from "../database/Cosmetics";
import {
  updateCosmeticsAnnouncerPack,
  updateCosmeticsBanner,
  updateCosmeticsRingoutVfx,
  updateCosmeticsStatTrackerSlot,
  updateCosmeticsTauntSlot,
  updateProfileIcon,
} from "../services/cosmeticsService";
import * as SharedTypes from "../types/shared-types";
import * as AuthUtils from "../utils/auth";
import { getAssetsByType } from "../loadAssets";

const serviceName = "Handlers.Cosmetics";
const logPrefix: string = `[${serviceName}]:`;

interface Profile_Icon_REQ {
  Slug: string;
}

interface Taunts_REQ {
  CharacterSlug: string;
  TauntSlotIndex: number;
  TauntSlug: string;
}

interface Banner_REQ {
  BannerSlug: string;
}

interface Ringout_REQ {
  RingoutVfxSlug: string;
}

interface AnnouncerPack_REQ {
  AnnouncerPackSlug: string;
}

interface StatTracker_REQ {
  StatTrackerSlotIndex: number;
  StatTrackerSlug: string;
}

export async function equip_taunt(req: Request, res: Response) {
  let account = AuthUtils.DecodeClientToken(req);
  //const account = req.token;
  const body = req.body as Taunts_REQ;
  try {
    await updateCosmeticsTauntSlot(account.id, body.CharacterSlug, body.TauntSlotIndex, body.TauntSlug);
    logger.info(account.id, body.CharacterSlug, body.TauntSlotIndex, body.TauntSlug);
    res.send({
      body: req.body,
      metadata: null,
      return_code: 0,
    });
  }
  catch (err) {
    try {
      logger.error(`${logPrefix} Error saving taunt ${body.TauntSlug} in index ${body.TauntSlotIndex} for ${account.id}: ${err}`);
    }
    catch (error) {
      try {
        logger.error(`${logPrefix} Error saving taunt for ${account.id}: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
      }
      catch (finalerror) {
        logger.error(`${logPrefix} Error saving taunt: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
        logger.error(`${logPrefix} Additional nested inner error info: ${finalerror}`);
      }
    }

    res.send({});
  }
}

export async function equip_stat_tracker(req: Request, res: Response) {
  let account = AuthUtils.DecodeClientToken(req);
  //const account = req.token;
  const body = req.body as StatTracker_REQ;
  try {
    await updateCosmeticsStatTrackerSlot(account.id, body.StatTrackerSlotIndex, body.StatTrackerSlug);
    res.send({
      body: req.body,
      metadata: null,
      return_code: 0,
    });
  }
  catch (err) {
    try {
      logger.error(`${logPrefix} Error saving stat tracker ${body.StatTrackerSlug} in index ${body.StatTrackerSlotIndex} for ${account.id}: ${err}`);
    }
    catch (error) {
      try {
        logger.error(`${logPrefix} Error saving stat tracker for ${account.id}: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
      }
      catch (finalerror) {
        logger.error(`${logPrefix} Error saving taunt: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
        logger.error(`${logPrefix} Additional nested inner error info: ${finalerror}`);
      }
    }

    res.send({});
  }
}

export async function equip_announce_pack(req: Request, res: Response) {
  let account = AuthUtils.DecodeClientToken(req);
  //const account = req.token;
  const body = req.body as AnnouncerPack_REQ;
  try {
    await updateCosmeticsAnnouncerPack(account.id, body.AnnouncerPackSlug);
    res.send({
      body: {
        EquippedAnnouncerPack: body.AnnouncerPackSlug,
      },
      metadata: null,
      return_code: 0,
    });
  }
  catch (err) {
    try {
      logger.error(`${logPrefix} Error saving announcer pack ${body.AnnouncerPackSlug} for ${account.id}: ${err}`);
    }
    catch (error) {
      try {
        logger.error(`${logPrefix} Error saving announcer pack for ${account.id}: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
      }
      catch (finalerror) {
        logger.error(`${logPrefix} Error saving taunt: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
        logger.error(`${logPrefix} Additional nested inner error info: ${finalerror}`);
      }
    }

    res.send({});
  }
}

export async function equip_ringout_vfx(req: Request, res: Response) {
  let account = AuthUtils.DecodeClientToken(req);
  //const account = req.token;
  const body = req.body as Ringout_REQ;
  try {
    await updateCosmeticsRingoutVfx(account.id, body.RingoutVfxSlug);
    res.send({
      body: {
        EquippedRingoutVfx: body.RingoutVfxSlug,
      },
      metadata: null,
      return_code: 0,
    });
  }
  catch (err) {
    try {
      logger.error(`${logPrefix} Error saving ringout VFX ${body.RingoutVfxSlug} for ${account.id}: ${err}`);
    }
    catch (error) {
      try {
        logger.error(`${logPrefix} Error saving ringout VFX for ${account.id}: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
      }
      catch (finalerror) {
        logger.error(`${logPrefix} Error saving taunt: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
        logger.error(`${logPrefix} Additional nested inner error info: ${finalerror}`);
      }
    }

    res.send({});
  }
}

export async function equip_banner(req: Request, res: Response) {
  let account = AuthUtils.DecodeClientToken(req);
  //const account = req.token;
  const body = req.body as Banner_REQ;
  try {
    await updateCosmeticsBanner(account.id, body.BannerSlug);
    res.send({
      body: {
        EquippedBanner: body.BannerSlug,
      },
      metadata: null,
      return_code: 0,
    });
  }
  catch (err) {
    try {
      logger.error(`${logPrefix} Error saving banner ${body.BannerSlug} for ${account.id}: ${err}`);
    }
    catch (error) {
      try {
        logger.error(`${logPrefix} Error saving banner for ${account.id}: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
      }
      catch (finalerror) {
        logger.error(`${logPrefix} Error saving banner: ${err}`);
        logger.error(`${logPrefix} Additional inner error info: ${error}`);
        logger.error(`${logPrefix} Additional nested inner error info: ${finalerror}`);
      }
    }

    res.send({});
  }
}

export async function set_profile_icon(req: Request, res: Response) {
  let account = AuthUtils.DecodeClientToken(req);
  const body = req.body as Profile_Icon_REQ;

  // The player record is what /access, profiles and friends read the icon from. It is keyed by
  // _id: the old findOne({ id }) matched nothing (players have no `id` field), so the icon was
  // never saved and every relaunch showed the default. Only icons the game defines are stored,
  // on the player record and in the equipped cosmetics.
  const slug = typeof body?.Slug === "string" ? body.Slug : "";
  if (!getAssetsByType("ProfileIconData").some((p) => p.slug === slug)) {
    logger.warn(`${logPrefix} Ignoring unknown profile icon "${slug}" for ${account.id}`);
    res.send({});
    return;
  }

  try {
    const result = await SharedTypes.PlayerTesterModel.updateOne({ _id: account.id }, { $set: { profile_icon: slug } });
    await updateProfileIcon(account.id, slug);
    logger.info(`${logPrefix} Profile icon for ${account.id} set to ${slug}${result.matchedCount === 0 ? " (no such player)" : ""}`);
  }
  catch (err) {
    logger.error(`${logPrefix} Error saving profile icon ${slug} for ${account.id}: ${err}`);
    res.send({});
    return;
  }

  res.send({
    body: {
      EquippedProfileIcon: slug,
    },
    metadata: null,
    return_code: 0,
  });
}
