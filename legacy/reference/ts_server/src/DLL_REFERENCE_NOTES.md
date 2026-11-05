# DLL Injection Reference Notes

These are the key technical findings from the DLL (ASI) reverse engineering work.
The DLL is NO LONGER NEEDED for friends, party join, party leave, or custom lobbies.
All functionality now works server-side. This file preserves the knowledge for future reference.

---

## UObject Layout (NON-STANDARD — shifted by +8)
- ClassPrivate: +0x18 (standard UE5 is +0x10)
- FName: +0x20 (standard is +0x18)
- OuterPrivate: +0x28 (standard is +0x20)
- FField/FProperty FName: +0x18 (standard, unchanged)
- UClass SuperStruct: +0x50

## Key Game Objects
- GObjects RVA: 0x081C5130
- ProcessEvent RVA: 0x02D3D810
- FighterGame (FG): Game instance root object

## Subsystem Collection
- Location: FighterGame+0x118 (TArray data), FighterGame+0x120 (count)
- Entry stride: 24 bytes (TMap: key UClass* 8 + value USubsystem* 8 + hash 8)
- 65 subsystems total

## GameClientHydraManager Object Hierarchy
- `PGI_GameClientHydraManager_C` (extends UGameClientHydraManager)
  - +0x348: InventoryManager*
  - +0x358: PartyManager* (has JoinParty, SetIsJoinable, OnPartyChanged delegate)
  - +0x370: CentralLobbyManager*
  - +0x378: CustomGameManager*
  - +0x388: BP_PartyLobbyManager_C* (has JoinLobby — THE function for party invite join)
  - +0x390: BP_ArenaLobbyManager_C*

## PartyManager Key Offsets
- +0x38: FString PartyID
- +0x48: TArray<FPartyMember> JoinedMembers
- +0x58: TArray<FPartyMember> InvitedMembers
- +0x68: TArray<FPartyMember> DeclinedMembers
- +0x78: bool IsPartyJoinable (controls + icon visibility)
- +0x80: OnPartyChanged delegate (fires UI update including PLAY→READY)

## UMvsPlayerLobbyManager (Base for PartyLobbyManager)
- +0x208: FString HydraLobbyTemplate
- +0x218: FString CreateLobbySscEndpoint
- +0x228: FString SelectLoadoutEndpoint
- +0x238: FString StartMatchSscEndpoint
- +0x248: FString JoinByMatchIdEndpoint
- +0x258: UMvsPlayerLobby* Lobby (THE lobby pointer — DLL nulled this for rejoin)
- +0x2A0: UMvsDialog* JoiningDialog (loading screen shown during join)
- +0x2B0: UMvsHydraRealtimeSession* RealtimeSession

## JoinLobby Params (0x28 bytes) — MvsPlayerLobbyManager
- +0x00: FString LobbyId (0x10)
- +0x10: TDelegate OnResult (0x10, can be zeroed)
- +0x20: bool bIsSpectator (1 byte)
- +0x21: bool bCreateNewSession (1 byte)

## UMultiVersusFriendsList
- +0x238: TArray<UMVSFriend*> IncomingFriendRequests
- +0x248: TArray<UMVSFriend*> OutgoingFriendRequests
- +0x258: TArray<UMVSFriend*> OnlineFriends
- +0x268: TArray<UMVSFriend*> OfflineFriends
- +0x278: TArray<UMVSFriend*> OnlinePlatformFriends
- +0x288: TArray<UMVSFriend*> OfflinePlatformFriends
- +0x298: TArray<UMVSFriend*> BlockedUsers
- +0x2A8: TArray<UMVSFriend*> RecentlyPlayedWithFriends
- +0x2B8: TArray<UMVSFriend*> UserSearchResults
- +0x2C8: TArray<UMVSFriend*> IncomingLobbyInviters
- +0x2D8: TMap<FString, FString> AccountToIncomingInvitationID
- +0x328: TMap<FString, FString> AccountToOutgoingInvitationID
- +0x378: TMap<FString, UMVSFriend*> MVSFriendRecords
- +0x3D0: UMvsSocialController* MvsSocialController

## UMVSFriend
- +0x38: UHydraUser* HydraUser
- +0x40: UPlayerNetworkPlayer* PlayerNetworkInfo
- +0x48: UPlatformPlayer* PlatformPlayerInfo
- +0x50: UMvsPlayerLobbyInviteData* AssociatedLobbyInviteData
- Size: 0x58

## FFriend Struct (0x20 bytes)
- +0x00: UHydraUser* User
- +0x08: bool IsUnseenRelationship
- +0x09: EFriendType Type
- +0x10: FString ProfileIconPath

## URelationshipManager
- +0x68: OnFriendOnline delegate
- +0x78: OnFriendOffline delegate
- +0x88: OnStatusUpdated delegate
- +0x98: OnFriendsChanged delegate
- +0x118: bool FriendsLoaded
- +0x120: TArray<FFriend> Friends
- +0x130: TArray<FFriend> Followers
- +0x140: TArray<FFriend> Following
- +0x150: TArray<FFriend> Blocked
- +0x160: TArray<FPlatformFriend> PlatformFriends
- +0x170: TArray<FFriend> SearchResults
- +0x188: UHydraUserManager* HydraUserManager
- +0x1A0: int32 PageSize

## UHydraUserManager
- +0x40: TrackedUsers TMap<FString, UHydraUser*> (stride=0x20)
- Functions: FetchHydraUser(), FindHydraUser(), RegisterBotUser(FString AccountID, FString BotUsername)

## RegisterBotUser Discovery
- Creates REAL, GC-registered UHydraUser in TrackedUsers TMap
- Found via UFunction heap scan (FName "RegisterBotUser")
- Called via ProcessEvent on HydraUserManager (at HydraManager+0x140)
- Params: two FStrings (0x20 bytes total)
- NO LONGER NEEDED — multiple friends now work server-side via steam ID dedup fix

## DLL Friend Injection (SUPERSEDED — server-side fix found)
- Root cause was NOT the game parser overwriting friends
- Root cause was `identity.alternate.steam[0].id` being the same for all friends
- Game used steam ID as dedup key — duplicate IDs = 1 friend shown
- Fix: use unique ID per friend (player _id) as the steam ID in bulk response

## DLL Party Rejoin (SUPERSEDED — server-side fix found)
- DLL approach: NULL PLM+0x258 (Lobby*) then call TriggerJoinLobby
- Server-side fix: send PlayerJoinedLobby WebSocket notification to host
- Format: cmd "update", template_id "PlayerJoinedLobby", LockedLoadouts for ALL players
- Same notification template works for both custom and party lobbies

## Notification System
- NotificationManager at FG+0x5A0
- Default widget: WBP_NotificationWidget_C at manager+0x0A0 (orange info banners)
- Social notification widgets:
  - BP_SocialNotificationsSubsystem_C (entry [58] in subsystem collection)
  - +0x058: WBP_SocialNotificationWidget_Receiver_C (blue VIEW banner)
  - +0x138: WBP_SocialNotificationWidget_Sender_C (green sent banner)

## FMvsNotificationData Layout (0xA8 bytes)
- +0x00: FText Text (0x18)
- +0x18: FText Caption (0x18)
- +0x30: TSoftObjectPtr<UTexture2D> Icon (0x30)
- +0x60: float TimeoutSeconds (0x04)
- +0x68: FDataTableRowHandle Action (0x10)
- +0x78: FText ActionButtonText (0x18)
- +0x90: TSubclassOf<UMvsNotificationWidget> WidgetClass (0x08)
- +0x98: EMvsNotificationCategory Category (0x01)
- +0x99: bool bAutoDismissWhenClicked (0x01)
- +0xA0: UObject* UserData (0x08)

## EOS SDK (Epic Online Services)
- DLL Path: Engine/Binaries/Win64/EOSSDK-Win64-Shipping.dll
- Version 1.15.3, 622 exported functions
- Game imports 37 lobby functions + 28 session functions via IAT
- NOT USED by our server — game uses Hydra/AccelByte instead

## Build/Deploy Paths
- Build: MSBuild OpenVersus.vcxproj /p:Configuration=Release /p:Platform=x64
- Output: client/out/Release/Binaries/OpenVersus.asi
- Deploy: MultiVersus/Binaries/Win64/plugins/OpenVersus.asi
- DLL is LOCKED while game runs — must kill game before copy
