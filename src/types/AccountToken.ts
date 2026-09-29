export interface IAccountToken {
  public_id: string;
  wb_network_id: string;
  id: string;
  profile_id: string;
  username: string;
  hydraUsername: string;
  current_ip: string;
  lobby_id: string;
  GameplayPreferences: number;
  steamId: string;
  epicId: string;
  hardwareId: string;
  hardwareIdVersion?: string;
  hardwareIdQuality?: string;
  installId?: string;
  clientVersion?: string;
  /** "1" only when this session was bootstrapped through /api/identify. */
  identityRegistered?: string;
  // token: AccountToken;
  // account: AccountToken;
}

export class AccountToken implements IAccountToken {
  public_id: string;
  wb_network_id: string;
  id: string;
  profile_id: string;
  username: string;
  hydraUsername: string;
  current_ip: string;
  lobby_id: string;
  GameplayPreferences: number;
  steamId: string;
  epicId: string;
  hardwareId: string;
  hardwareIdVersion?: string;
  hardwareIdQuality?: string;
  installId?: string;
  clientVersion?: string;
  identityRegistered?: string;
  // token: AccountToken;
  // account: AccountToken;

  constructor() {
    this.public_id = "";
    this.wb_network_id = "";
    this.id = "";
    this.profile_id = "";
    this.username = "";
    this.hydraUsername = "";
    this.current_ip = "";
    this.lobby_id = "";
    this.GameplayPreferences = 964;
    this.steamId = "";
    this.epicId = "";
    this.hardwareId = "";
    this.hardwareIdVersion = "";
    this.hardwareIdQuality = "";
    this.installId = "";
    this.clientVersion = "";
    this.identityRegistered = "";
    // this.token = this;
    // this.account = this;
  }
}
