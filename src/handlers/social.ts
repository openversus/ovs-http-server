// import express, { Request, Response } from "express";
// import { MVSQueries } from "../interfaces/queries_types";
// import { redisClient } from "../config/redis";
// import * as AuthUtils from "../utils/auth";
// import * as KitchenSink from "../utils/garbagecan";
// import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";


// export async function getUserBlockList(userId: string) {
//   const mongoPlayer = await PlayerTesterModel.findOne({ accountId: userId });
//   if (!mongoPlayer || !mongoPlayer.blockedPlayers) return [];
//   // 4-field format wrapped in account object — game parser extracts public_id from this.
//   // Check actual online status for each blocked player
//   const blockedEntries = mongoPlayer.blockedPlayers;
//   const results = await Promise.all(
//     blockedEntries.map(async (f) => {
//       const mongoBlockedPlayer = await PlayerTesterModel.findOne({ id: f });
//       const isOnline = await redisClient.sIsMember("online_players", f);
//       return {
//         created_at: new Date().toISOString(),
//         account: {
//           public_id: f,
//           username: mongoBlockedPlayer?.name || "Unknown",
//           avatar: {
//             name: "MultiVersus",
//             image_url: "https://prod-network-images.wbagora.com/network/account-wbgames-com/multiversus-finn.jpg",
//           },
//         },
//       };
//     }),
//   );
//   return results;
// }
