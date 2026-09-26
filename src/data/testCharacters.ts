// Test / unfinished characters that ship in the retail client. The live server
// never enables them; these slugs are kept out of the served catalog unless
// ENABLE_TEST_CHARACTERS=true (for local experiments only).
export const TEST_CHARACTER_SLUGS = [
  "character_supershaggy",
  "character_Meeseeks",
  "character_C022",
  "character_C033",
  "character_cmanny",
  "character_manny",
  "character_C037",
  "character_C099",
] as const;

const TEST_CHARACTER_SET = new Set<string>(TEST_CHARACTER_SLUGS.map((slug) => slug.toLowerCase()));

/** Case-insensitive: catalog rows are not consistent about slug casing. */
export function isTestCharacter(characterSlug: string | undefined | null): boolean {
  return !!characterSlug && TEST_CHARACTER_SET.has(characterSlug.toLowerCase());
}
