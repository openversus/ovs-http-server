export interface IMatchStatus {
  Timestamp: IMatchStatusTimestamp
  Event?: string | null
  Description?: string | null
  matchId?: string | null
  key?: string | null
  NumPlayers: number
  PlayerId?: string | null
  PlayerIds: string[]
}

export interface IMatchStatusTimestamp {
  RawTimestamp: string
  Year: number
  Month: number
  Day: number
  Hour: number
  Minute: number
  Second: number
  Millisecond: number
  UTCTimestamp: string
  UnixTimestamp: number
}
