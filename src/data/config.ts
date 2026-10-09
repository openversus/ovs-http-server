import { ConfigDataModel } from "../database/Config";

let CRC = 1267552956;
// Increment when static HISS payloads change so retail clients cannot reuse a
// matching cached catalog. 1: End Game BP cosmetics. 2-3: StressInducer's profile icons (3 once
// their DataAssets rows existed). 4: the game's unreleased cosmetics in the battle pass. 5: Painter Beetlejuice.
// 6: the battle pass's last tier no longer recurs, so it can be claimed. 7: the OVS Dev badge.
// 8: the One Tough Banana taunt. 9: the No Regrets profile icon (first "MultiVersus Ink"). 10: the battle pass is "OVS Season 1". 11: One Tough Banana at tier 3. 12: each fighter's level shows its portrait (XpIcon).
// 13: the final battle pass order (50 tiers, Twerk It Out last).
// 14: custom FFA uses the CustomFFA map rotation (WB's PlaytestCustomFFA had the unfinished M025 / M026 maps).
// 15: Get Rich or Dunk Tryin' (LeBron, Glassconsumer69) at battle pass tier 49; 51 tiers, Twerk It Out last.
const HISS_CONTENT_REVISION = 15;

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
