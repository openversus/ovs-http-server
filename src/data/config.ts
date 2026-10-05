import { ConfigDataModel } from "../database/Config";

let CRC = 1267552956;
// Increment when static HISS payloads change so retail clients cannot reuse a
// matching cached catalog. 1: End Game BP cosmetics. 2-3: StressInducer's profile icons (3 once
// their DataAssets rows existed). 4: the game's unreleased cosmetics in the battle pass. 5: Painter Beetlejuice.
// 6: the battle pass's last tier no longer recurs, so it can be claimed. 7: the OVS Dev badge.
// 8: the One Tough Banana taunt. 9: the MultiVersus Ink profile icon. 10: the battle pass is "OVS Season 1".
const HISS_CONTENT_REVISION = 11;

export function getCurrentCRC() {
  return CRC + HISS_CONTENT_REVISION;
}
// Bump when queue/game-mode catalog data changes so clients invalidate their
// cached matchmaking configuration.
export const MATCHMAKING_CRC = 2;

export async function LoadConfig() {
  const existing = await ConfigDataModel.findOne().exec();
  if (!existing) {
    await ConfigDataModel.create({ CRC: 1 });
  }

  if (existing) {
    CRC = existing.CRC;
  }
}

export async function UpdateCrc() {
  const doc = await ConfigDataModel.findOneAndUpdate(
    {}, // match the single entry
    { $inc: { CRC: 1 } }, // increment
    { upsert: true, new: true }, // create if missing, return updated doc
  )
    .lean()
    .exec();
  CRC = doc.CRC;
}
