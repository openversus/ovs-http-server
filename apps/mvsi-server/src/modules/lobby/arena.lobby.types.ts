// ─── Types ────────────────────────────────────────────────────────────────────

export type ArenaInventorySlot = {
  Xp: number;
  Level: number;
  Slug: string;
  NextLevelXp: number;
  SellValue: number;
};

export type ArenaPlayerData = {
  ShopRerollCost: number;
  FreeShopRerolls: number;
  InterestPer: number;
  Inventory: ArenaInventorySlot[];
  CurrencyAmount: number;
};

export type ArenaMatchStats = {
  KnockbackMitigated: number;
  KnockbackAdded: number;
  HealingReceived: number;
  Ringouts: number;
  GreyHealthReceived: number;
  DamagedAdded: number;
  DamageMitigated: number;
  Damage: number;
};

export type ArenaPlayerStats = {
  bRandomCharacter: boolean;
  ShopRerolls: number;
  ItemsPurchased: number;
  ItemsSold: number;
  InterestGained: number;
  MatchStats: ArenaMatchStats;
  CurrencySpent: number;
  ItemsLeveled: number;
};

export type BaseArenaPlayerInfo = {
  Loadout: { Character: string; Skin: string };
  SelectableCharacters: string[];
  AccountId: string;
  GameplayPreferences: number;
  TeamId: string;
  PlayerData: ArenaPlayerData;
  CharacterClass: number;
  AccountInfo: {
    RingoutVfx: string;
    Taunts: string[];
    Banner: string;
    ProfileIcon: string;
    Name: string;
  };
};

export type ArenaPlayerInfo = BaseArenaPlayerInfo &
  (
    | {
        bIsBot: true;
        BotSettings: { BuildType: string };
      }
    | {
        bIsBot: false;
        Stats: ArenaPlayerStats;
        CurrentShop: ArenaCurrentShop[];
        CurrentShopLocal: ArenaCurrentShopLocal[];
      }
  );

export type ArenaCurrentShopLocal = {
  CurrentShop: ArenaCurrentShop[];
};

export type ArenaCurrentShop = {
  Item: ArenaInventorySlot;
  Cost: number;
};

export type ArenaTeamInfo = {
  TeamId: string;
  TeamIndex: number;
  Players: string[];
  Stats: {
    Wins: number;
    WinStreak: number;
    Losses: number;
    LoseStreak: number;
    Draws: number;
    MatchStats: ArenaMatchStats;
  };
  LifeRemaining: number;
  FinalRank: number;
  RoundOpponents: string[];
  HasPlayedTeam: Record<string, boolean>;
  Matches: string[];
  PriorMap?: string;
};

export type ArenaData = {
  AllMultiplayParams: Record<
    string,
    { MultiplayClusterSlug: string; MultiplayProfileId: string; MultiplayRegionId: string }
  >;
  PlayerInfo: Record<string, ArenaPlayerInfo>;
  CurrentRound: number;
  TeamInfo: Record<string, ArenaTeamInfo>;
  Players: string[];
  ItemPool: Record<string, number>;
};

export type ArenaConstants = {
  FaceoffWaitTime: number;
  CurrencyPerRingout: number[];
  MaxInterest: number;
  ShopLevelWeights: {
    RarityWeight: {
      Uncommon: number;
      Rare: number;
      Epic: number;
      Legendary: number;
      Common: number;
    };
  }[];
  CurrencyForWin: number;
  InterestPerBoost: number[];
  HealthRoundScalars: number[];
  CurrencyForRandomCharacterSelect: number;
  ItemXpPerLevel: number[];
  CurrencyForWinStreak: number[];
  CharacterSelectTime: number;
  ItemAmountsByRarity: {
    Uncommon: number;
    Rare: number;
    Epic: number;
    Legendary: number;
    Mythic: number;
    Common: number;
  };
  CurrencyForLoseStreak: number[];
  RoundLength: number;
  HealthRoundValues: number[];
  ShopCharacterRerollCost: number;
  InterestPer: number;
  CurrencyPerRound: number[];
};
