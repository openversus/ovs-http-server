import express, { Request, Response } from "express";
import { DataAssetModel } from "./database/DataAssets";
import env from "./env/env";
import { logger } from "./config/logger";
import { loadAssets } from "./loadAssets";
import { UpdateCrc } from "./data/config";

const serviceName: string = "DataAssetSync";
export const syncRouter = express.Router();

interface SYNC_ASSET {
  assetType: string;
  assetPath: string;
  slug: string;
  enabled: boolean;
  character_slug: string;
  oldSlug: string;
}
syncRouter.post("/syncAsset", async (req, res) => {
  logger.info(`[${serviceName}]: Trying syncAsset`);
  try {
    if (undefined !== req.headers["authorization"] && !req.headers["authorization"] && req.headers["authorization"] !== "") {
      logger.debug(`[${serviceName}]: Received DATA_ASSET_TOKEN ${req.headers["authorization"]}`);
    }
    if (req.headers["authorization"] !== `Bearer ${env.DATA_ASSET_TOKEN}`) {
      res.sendStatus(403);
      return;
    }
    const dto = req.body as SYNC_ASSET;
    const doc = await DataAssetModel.findOneAndUpdate(
      { assetPath: dto.assetPath },
      {
        $set: {
          slug: dto.slug,
          assetType: dto.assetType,
          character_slug: dto.character_slug,
          enabled: dto.enabled,
          assetPath: dto.assetPath,
        },
      },
      { upsert: true, new: true },
    ).exec().then(()=>{
      loadAssets();
      UpdateCrc();
    });
    res.sendStatus(200);
  }
  catch (e) {
    res.status(404);
    //@ts-ignore
    res.send(e.codeName);
  }
  return;
});
