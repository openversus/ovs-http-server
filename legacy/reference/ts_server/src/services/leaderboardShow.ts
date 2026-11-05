// In-game ranked leaderboard (/leaderboards/<slug>/show and /around/<id>).
//
// Official Hydra shape, as a script that read the live API used it (gist
// TBartl/4e05823b52c5c290c819d4de0c45688d) and verified in game:
//   { leaders: [{ rank, score, id, account: {...}, profile: {...} }] }
// The client names the fields it wants in the query (`fields` for the profile,
// `account_fields` for the account, partial_response=1); they are echoed back
// as flattened keys, e.g.
//   profile["server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.1v1.BestCharacter.CharacterSlug"]

export interface LeaderboardRow {
  rank: number;
  account_id: string;
  username: string;
  elo: number;
  bestCharacter?: string;
}

/** The `fields` / `account_fields` query values (one string or repeated). */
export interface LeaderboardShowQuery {
  fields?: string | string[];
  account_fields?: string | string[];
}

/** "ranked_season5_1v1_all" -> 1v1, all characters; "..._1v1_character_shaggy" -> that character. */
export function parseLeaderboardSlug(slug: string): { mode: "1v1" | "2v2"; characterSlug?: string } {
  const match = /_(1v1|2v2)(?:_(.+))?$/.exec(slug);
  const mode = (match?.[1] ?? (slug.includes("2v2") ? "2v2" : "1v1")) as "1v1" | "2v2";
  const scope = match?.[2];
  return { mode, characterSlug: scope && scope !== "all" ? scope : undefined };
}

const asList = (value: string | string[] | undefined) => (value === undefined ? [] : [value].flat());

/** Value for one requested flattened field of a leaderboard player. */
function fieldValue(field: string, row: LeaderboardRow): unknown {
  if (field.endsWith(".CharacterSlug")) return row.bestCharacter ?? "";
  if (field === "identity.username") return row.username;
  return null;
}

function leader(row: LeaderboardRow, query: LeaderboardShowQuery) {
  const profile: Record<string, unknown> = { id: row.account_id, account_id: row.account_id };
  for (const field of asList(query.fields)) profile[field] = fieldValue(field, row);
  const account: Record<string, unknown> = { id: row.account_id, public_id: row.account_id, "identity.username": row.username };
  for (const field of asList(query.account_fields)) account[field] = fieldValue(field, row);
  return { id: row.account_id, rank: row.rank, score: row.elo, account, profile };
}

export function buildLeaderboardShowBody(rows: LeaderboardRow[], query: LeaderboardShowQuery = {}) {
  return { leaders: rows.map((row) => leader(row, query)) };
}
