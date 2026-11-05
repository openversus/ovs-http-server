// import { logger } from "../config/logger";
// import express from "express";
// import { AccountToken, IAccountToken } from "./AccountToken";
// import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";

// export interface IPlayerConnection {
//   account: AccountToken;
//   token: AccountToken;
//   public_id: string;
//   wb_network_id: string;
//   id: string;
//   profile_id: string;
//   username: string;
//   hydraUsername: string;
//   current_ip: string;
//   lobby_id: string;
//   // player: PlayerTester | undefined;
//   is_default_object: boolean;
//   Create(token: IAccountToken, player: PlayerTester): void;
// }

// export interface IPlayerConnections {
//   connections: IPlayerConnection[];
//   AddConnection(connection: PlayerConnection): void;
//   RemoveConnection(connection: PlayerConnection): void;
//   UpdateConnection(connection: PlayerConnection): void;
//   UpdateConnections(connections: PlayerConnection[]): void;
//   ClearConnections(): void;
//   Count(): number;
//   GetGlobalObject(app: express.Application): IPlayerConnections;
//   Get(app: express.Application): Array<IPlayerConnection>;
//   Set(app: express.Application, connections: IPlayerConnections): void;
// }

// export class PlayerConnection implements IPlayerConnection {
//   account: AccountToken;
//   token: AccountToken;
//   public_id: string;
//   wb_network_id: string;
//   id: string;
//   profile_id: string;
//   username: string;
//   hydraUsername: string;
//   current_ip: string;
//   lobby_id: string;
//   // player: PlayerTester | undefined;
//   is_default_object: boolean;

//   constructor(player: PlayerTester)
//   constructor(player: PlayerTester, token: IAccountToken)
//   constructor() {
//     this.public_id = '';
//     this.wb_network_id = '';
//     this.id = '';
//     this.profile_id = '';
//     this.username = '';
//     this.hydraUsername = '';
//     this.current_ip = '';
//     this.lobby_id = '';
//     this.token = new AccountToken();
//     this.account = new AccountToken();
//     this.is_default_object = false;

//     switch (arguments.length) {
//       case 0:
//         // this.player = new PlayerTesterModel({ ip: "224.0.0.254", name: "Default Player" });
//         break;
//       case 1:
//         // this.player = arguments[0];
//         break;
//       case 2:
//         // this.player = arguments[0];
//         this.token = arguments[1];
//         this.account = arguments[1];
//         break;
//     }
//   }

//   Create(token: IAccountToken, player: PlayerTester): void {
//     this.public_id = token.public_id;
//     this.wb_network_id = token.wb_network_id;
//     this.id = token.id;
//     this.profile_id = token.profile_id;
//     this.username = token.username;
//     this.hydraUsername = token.hydraUsername;
//     this.current_ip = token.current_ip;
//     this.lobby_id = token.lobby_id;
//     this.token = token as IAccountToken;
//     this.account = token as IAccountToken;
//     // this.player = player;
//   }
// }

// export class PlayerConnections implements IPlayerConnections {
//   connections: IPlayerConnection[];

//   constructor() {
//     this.connections = [];
//   }

//   public AddConnection(connection: PlayerConnection): void {
//     if (!connection) {
//       return;
//     }  
//     this.connections.push(connection);
//   }

//   public RemoveConnection(connection: PlayerConnection): void {
//     if (!connection || !connection.id) {
//       return;
//     }
//     this.connections = this.connections.filter(
//       (conn) => conn.id !== connection.id
//     );
//   }

//   public UpdateConnection(connection: PlayerConnection): void {
//     if (!connection || !connection.id) {
//       return;
//     }
//     this.connections = this.connections.map(
//       (conn) => (conn.id === connection.id ? connection : conn)
//     );
//   }

//   public UpdateConnections(connections: PlayerConnection[]): void {
//     if (!connections) {
//       return;
//     }
//     connections.forEach((connection) => {
//       if (!connection || !connection.id) {
//         return;
//       }
//       this.UpdateConnection(connection);
//     });
//   }

//   public ClearConnections(): void {
//     this.connections = [];
//   }

//   public Count(): number {
//     return this.connections.length || 0;
//   }

//   public GetGlobalObject(app: express.Application): IPlayerConnections {
//     let storedConnections = null;

//     try {
//       storedConnections = app.get("PlayerConnections") as IPlayerConnections;
//     } catch (error) {
//       logger.warn("Error retrieving PlayerConnections from app:", error);
//       logger.warn("Reinitializing PlayerConnections.");
//       storedConnections = new PlayerConnections() as IPlayerConnections;
//     }
    
//     if (storedConnections === null || storedConnections === undefined) {
//       logger.warn("PlayerConnections not found in app. Initializing new instance.");
//       storedConnections = new PlayerConnections() as IPlayerConnections;
//       this.Set(app, storedConnections);
//     }

//     return storedConnections;
//   }

//   public Get(app: express.Application): Array<IPlayerConnection> {

//     let storedConnections = this.GetGlobalObject(app);
//     return storedConnections.connections || new Array<IPlayerConnection>();
//   }

//   public Set(app: express.Application, connections: IPlayerConnections): void {
//     try {
//       app.set("PlayerConnections", connections); 
//     } catch (error) {
//       logger.error("Error setting PlayerConnections in app:", error);
//     }
//   }
// }
