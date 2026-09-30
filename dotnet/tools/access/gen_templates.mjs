// Writes the response templates in src/OpenVersus.Server.Core/Access/ from the TS server's own object literals, so
// nothing in them is copied by hand. Run from the repository root:
//   node dotnet/tools/access/gen_templates.mjs
// Each literal is evaluated with markers ("{{name}}") for the values that come from the request or the player; the C#
// side fills them. Rerun when a TS literal changes; the byte tests against captured responses say whether the result
// still matches.
import fs from "node:fs";
import path from "node:path";
import { stripTypeScriptTypes } from "node:module";

const root = process.cwd();
const marker = (name) => `{{${name}}}`;

/** Evaluates the object literal that starts with `start` and ends at the first `end` after it. */
async function literal(file, start, end, skip, scope) {
  const source = fs.readFileSync(path.join(root, file), "utf8");
  const from = source.indexOf(start);
  const to = source.indexOf(end, from);
  if (from < 0 || to < 0) throw new Error(`the literal in ${file} was not found; update this script`);
  // `end` holds the literal's closing bracket (} or ]) first.
  const close = [end.indexOf("}"), end.indexOf("]")].filter((i) => i >= 0).sort((a, b) => a - b)[0];
  const text = source.slice(from + skip, to + 1 + close);
  // Node strips the literal's TypeScript annotations.
  const js = stripTypeScriptTypes(`(async () => (${text}))()`);
  return new Function(...Object.keys(scope), `return ${js}`)(...Object.values(scope));
}

function write(name, value, dir = "Access") {
  const out = path.join(root, "dotnet/src/OpenVersus.Server.Core", dir, name);
  fs.writeFileSync(out, JSON.stringify(value, null, 2) + "\n");
  console.log(`wrote ${path.relative(root, out)}`);
}

// POST /access (handlers/access.ts generateStaticAccess). The stat trackers are evaluated with no match data: their
// computed fields are filled from the player's stats. Addresses (the realtime block, the avatar URLs) are settings
// (Realtime:*, Access:*AvatarUrl) whose defaults are the TS values.
const login = await literal("src/handlers/access.ts", "  return {\n    token: token,", "\n  };\n}", "  return ".length, {
  token: marker("token"),
  ws: marker("ws"),
  account: {
    id: marker("account.id"),
    profile_id: marker("account.profile_id"),
    public_id: marker("account.public_id"),
    wb_network_id: marker("account.wb_network_id"),
    username: marker("account.username"),
    hydraUsername: marker("account.hydraUsername"),
  },
  player: { profile_icon: marker("player.profile_icon") },
  getAssetsByType: () => [{ slug: marker("player.profile_icon"), assetPath: marker("profile_icon.assetPath") }],
  EloRatingModel: { findOne: () => ({ lean: async () => null }) },
  PlayerStatsModel: { findOne: () => ({ lean: async () => null }) },
});
login.configuration.realtime = marker("realtime");
login.account.identity.avatar = marker("identity.avatar");
login.account.identity.alternate.steam[0].avatar = marker("steam.avatar");
write("login-response.json", login);

// POST /sessions/auth/token (handlers/sessions.ts): access_token echoes the request's code. The WB network's realtime
// block and the avatar image are settings (WbNetwork:*) whose defaults are the TS values.
const session = await literal("src/handlers/sessions.ts", "  res.send({", "\n  });\n}", "  res.send(".length, {
  req: { body: { code: marker("code") } },
});
if (process.argv[2] === "--defaults") {
  // What the settings' defaults were copied from.
  console.log(JSON.stringify({ cluster: session.sdk.realtime["default-cluster"], servers: session.sdk.realtime.servers, avatar: session.account.avatar.image_url }));
}
session.sdk.realtime = marker("realtime");
session.account.avatar.image_url = marker("avatar.image_url");
write("sessions-auth-token.json", session);

// Static answers (src/OpenVersus.Server.Core/Static): literals with nothing to fill.
const commerce = "src/handlers/commerce.ts";
write("commerce-products-partial.json", await literal(commerce, "  if (req.query.partial_response) {\n    res.send([", "\n    ]);", "  if (req.query.partial_response) {\n    res.send(".length, {}), "Static");
write("commerce-products.json", await literal(commerce, "  }\n  res.send([\n", "\n  ]);", "  }\n  res.send(".length, {}), "Static");
write("commerce-purchases-me.json", await literal(commerce, "  res.send({ purchases:", " });", "  res.send(".length, {}), "Static");
write("commerce-steam-mtx-user-info-me.json", await literal(commerce, "  res.send({ currency:", " });", "  res.send(".length, {}), "Static");

// GET /layout/dokken-layout-type/personalized/{variant}/{id} (handlers/layout.ts): one literal per variant, the same
// for every id.
const layouts = "src/handlers/layout.ts";
const layoutSource = fs.readFileSync(path.join(root, layouts), "utf8");
for (const variant of ["account-cosmetics-variant", "battlepass-variant", "currency-variant", "fighter-road-layout", "fighter-variant", "main-variant", "prestige-variant", "rift-variant", "skin-variant"]) {
  const fn = `handleLayout_dokken_layout_type_personalized_${variant.replaceAll("-", "_")}_id(`;
  const at = layoutSource.indexOf(`function ${fn}`);
  if (at < 0) throw new Error(`${fn} not found in ${layouts}; update this script`);
  const start = layoutSource.indexOf("  res.send(", at);
  // literal() finds the first match of its start string, so hand it this handler's own opening line and position.
  const opening = layoutSource.slice(start, layoutSource.indexOf("\n", start) + 1);
  const value = await literal(layouts, layoutSource.slice(at, start) + opening, "\n  });\n}", (layoutSource.slice(at, start) + "  res.send(").length, {});
  write(`layout-${variant}.json`, value, "Static");
}

// GET /file_storage and /file_storage/{slug} (handlers/file_storage.ts). The two openversus-update-required records are
// built per request (their download_url is this server's /assets/ URL, from the request's host): templates with
// markers in FileStorage/. The other records are the same for everyone: Static/.
const fileStorage = "src/handlers/file_storage.ts";
const fileSource = fs.readFileSync(path.join(root, fileStorage), "utf8");
fs.mkdirSync(path.join(root, "dotnet/src/OpenVersus.Server.Core/FileStorage"), { recursive: true });
const updateScope = {
  UPDATE_KEYART_FILENAME: "openversus-update-required-keyart.png",
  UPDATE_THUMBNAIL_FILENAME: "openversus-update-required-thumbnail.png",
  getAssetDownloadUrl: (_req, filename) => `{{assets}}${filename}`,
  req: {},
};
for (const [fn, name] of [["getUpdateKeyartRecord", "update-keyart"], ["getUpdateThumbnailRecord", "update-thumbnail"]]) {
  const start = `function ${fn}(req: Request) {\n  return {`;
  write(`file-storage-${name}.json`, await literal(fileStorage, start, "\n  };\n}", start.length - 1, updateScope), "FileStorage");
}
write("file-storage-list.json", await literal(fileStorage, "  res.send([\n    getUpdateKeyartRecord(req),", "\n  ]);", "  res.send(".length, {
  getUpdateKeyartRecord: () => "{{update-keyart}}",
  getUpdateThumbnailRecord: () => "{{update-thumbnail}}",
  req: {},
}), "FileStorage");
for (const slug of ["beginnermode-carousel-keyart", "beginnermode-carousel-thumbnail", "harley-rift-s5-keyart", "harley-rift-s5-thumbnail",
  "s5-bp-carousel-keyart", "s5-bp-carousel-thumbnail", "t-discord-qa-carousel-keyart", "t-discord-qa-carousel-thumbnail",
  "wonderwoman-arena-keyart", "wonderwoman-arena-thumbnail"]) {
  const fn = `function handleFile_storage_${slug.replaceAll("-", "_")}(`;
  const at = fileSource.indexOf(fn);
  if (at < 0) throw new Error(`${fn} not found in ${fileStorage}; update this script`);
  const start = fileSource.slice(at, fileSource.indexOf("  res.send({", at) + "  res.send({".length);
  write(`file-storage-${slug}.json`, await literal(fileStorage, start, "\n  });\n}", start.length - 1, {}), "Static");
}

// PUT /drives/multiversus/sync (handlers/drives.ts).
write("drives-multiversus-sync.json", await literal("src/handlers/drives.ts", "  res.send({ additions", " });", "  res.send(".length, {}), "Static");

// GET /ssc/invoke/get_country_code and /ssc/invoke/get_hiss_calendar_events (handlers/ssc.ts): fixed answers. (The hiss
// calendar has nothing to do with the hiss_amalgamation CRC; its handler answers this literal to everyone.)
const ssc = "src/handlers/ssc.ts";
write("ssc-get-country-code.json", await literal(ssc, "  res.send({ body: { region:", " });", "  res.send(".length, {}), "Static");
{
  const sscSource = fs.readFileSync(path.join(root, ssc), "utf8");
  const at = sscSource.indexOf("function handleSsc_invoke_get_hiss_calendar_events(");
  if (at < 0) throw new Error("handleSsc_invoke_get_hiss_calendar_events was not found; update this script");
  const start = sscSource.slice(at, sscSource.indexOf("  res.send({", at) + "  res.send({".length);
  write("ssc-get-hiss-calendar-events.json", await literal(ssc, start, "\n  });\n}", start.length - 1, {}), "Static");
}
// GET /ssc/invoke/get_milestone_reward_tracks: a fixed answer too (the same states for every player).
{
  const sscSource = fs.readFileSync(path.join(root, ssc), "utf8");
  const at = sscSource.indexOf("function handleSsc_invoke_get_milestone_reward_tracks(");
  if (at < 0) throw new Error("handleSsc_invoke_get_milestone_reward_tracks was not found; update this script");
  const start = sscSource.slice(at, sscSource.indexOf("  res.send({", at) + "  res.send({".length);
  write("ssc-get-milestone-reward-tracks.json", await literal(ssc, start, "\n  });\n}", start.length - 1, {}), "Static");
}

// GET /ssc/invoke/get_calendar_events (handlers/ssc.ts): the carousel, with the required-update popup that is kept only
// for a player who must update. Its start, ids and link are markers the C# side fills (CalendarService); its message is
// a marker too, filled from the same setting as the gate's answer (Clients:UpdateMessage, whose default is this text).
{
  const calendar = await literal(ssc, "  const response = {", "\n  };\n  if (!updateState.required)", "  const response = ".length, {
    updateEventStart: marker("start"),
    updateEntryId: marker("entryId"),
    updateEventRecordId: marker("recordId"),
    CLIENT_UPDATE_URL: marker("updateUrl"),
  });
  const update = calendar.body.Events.find((e) => e.data.slug === "ovs-required-update");
  if (!update) throw new Error("the required-update event was not found; update this script");
  const [key] = Object.keys(update.data.description.localizations);
  update.data.description.localizations[key] = marker("updateMessage");
  fs.mkdirSync(path.join(root, "dotnet/src/OpenVersus.Server.Core/Calendar"), { recursive: true });
  write("calendar-events.json", calendar, "Calendar");
}

// GET /ssc/invoke/get_equipped_cosmetics: the taunts a new cosmetics document starts with (database/Cosmetics.ts
// defaultTaunts, the schema default), and what a cached one without taunts is answered with.
fs.mkdirSync(path.join(root, "dotnet/src/OpenVersus.Server.Core/Cosmetics"), { recursive: true });
write("cosmetics-default-taunts.json", await literal("src/database/Cosmetics.ts", "export const defaultTaunts: IDefaultTaunts = {", "\n};", "export const defaultTaunts: IDefaultTaunts = ".length, {}), "Cosmetics");

// GET /profiles/{id}/inventory (handlers/profiles.ts): the parts that are literals. Gleamium and the toast record
// (whose count and updated_at are filled per request), and the taunt list unlockAll adds (data/taunts.ts AllTaunts).
fs.mkdirSync(path.join(root, "dotnet/src/OpenVersus.Server.Core/Inventory"), { recursive: true });
write("inventory-gleamium.json", await literal("src/data/gleamium.ts", "export const GleamiumData = {", "\n};", "export const GleamiumData = ".length, {}), "Inventory");
write("inventory-toast.json", await literal("src/data/toast.ts", "export const ToastData = {", "\n};", "export const ToastData = ".length, {}), "Inventory");
write("inventory-taunts.json", await literal("src/data/taunts.ts", "const AllTaunts: ITaunt[] = [", "\n];", "const AllTaunts: ITaunt[] = ".length, {}), "Inventory");
