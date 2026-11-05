import { AccountToken, IAccountToken } from "./AccountToken";

import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";

export * from "./AccountToken";
export * from "./PlayerConnection";

export { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";

// export interface IGameInstall {
//   account_platform_id: string;
//   hydra_public_id: string;
//   is_production: boolean;
//   os: string;
//   platform_name: string;
//   resolution: string;
//   user_name: string;
// }

// export class GameInstall implements IGameInstall {
//   account_platform_id: string;
//   hydra_public_id: string;
//   is_production: boolean;
//   os: string;
//   platform_name: string;
//   resolution: string;
//   user_name: string;

//   constructor()
//   constructor(
//     account_platform_id: string = "",
//     hydra_public_id: string = "",
//     is_production: boolean = true,
//     os: string = "",
//     platform_name: string = "",
//     resolution: string = "",
//     user_name: string = "",
//   ) {
//     this.account_platform_id = account_platform_id;
//     this.hydra_public_id = hydra_public_id;
//     this.is_production = is_production;
//     this.os = os;
//     this.platform_name = platform_name;
//     this.resolution = resolution;
//     this.user_name = user_name;
//   }
// }
