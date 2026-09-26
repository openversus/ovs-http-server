export type GitHubReleaseAsset = {
  name?: unknown;
  size?: unknown;
  digest?: unknown;
  browser_download_url?: unknown;
};

export type ClientUpdateFile = {
  name: string;
  kind: "plugin" | "paks";
  size: number;
  sha256: string;
  download_url: string;
};

const RELEASE_DOWNLOAD_PREFIX = "https://github.com/openversus/ovs-client/releases/download/";
const CONTENT_ASSET = /^OVS_[A-Za-z0-9._-]+\.(pak|utoc|ucas|sig)$/i;
// The plugin: legacy C++ releases ship "OpenVersus.asi"; C# releases keep the full
// version in the name for troubleshooting: "OpenVersus_<YYYY.MM.DD.N>.asi" (N = build).
// Their ".asi.sha256" sidecars and zips never match, so they are not offered.
const PLUGIN_ASSET = /^openversus(?:_(\d{4}\.\d{2}\.\d{2}\.\d+))?\.asi$/i;

/** The version in a versioned plugin file name, or null for the legacy "OpenVersus.asi". */
export function pluginAssetVersion(name: string): string | null {
  return PLUGIN_ASSET.exec(name)?.[1] ?? null;
}

function normalizeAsset(asset: GitHubReleaseAsset): ClientUpdateFile | null {
  const name = typeof asset.name === "string" ? asset.name : "";
  const url = typeof asset.browser_download_url === "string" ? asset.browser_download_url : "";
  const size = typeof asset.size === "number" && Number.isSafeInteger(asset.size) ? asset.size : 0;
  const digest = typeof asset.digest === "string" ? asset.digest.toLowerCase() : "";

  const isPlugin = PLUGIN_ASSET.test(name);
  const isContent = CONTENT_ASSET.test(name);
  if ((!isPlugin && !isContent) || size <= 0) return null;
  if (!url.startsWith(RELEASE_DOWNLOAD_PREFIX)) return null;
  if (!/^sha256:[a-f0-9]{64}$/.test(digest)) return null;

  return {
    name,
    kind: isPlugin ? "plugin" : "paks",
    size,
    sha256: digest.slice("sha256:".length),
    download_url: url,
  };
}

/**
 * @param releaseVersion the release's own version (its tag). When given, a versioned plugin
 * ("OpenVersus_<version>.asi") must carry exactly that version, so a mislabeled release is
 * refused here instead of being offered (the C# client makes the same check on its side).
 */
export function buildClientReleaseManifest(assets: GitHubReleaseAsset[], releaseVersion?: string): ClientUpdateFile[] {
  const files = assets.map(normalizeAsset).filter((file): file is ClientUpdateFile => Boolean(file));
  const pluginFiles = files.filter((file) => file.kind === "plugin");
  if (pluginFiles.length !== 1) {
    throw new Error(`release must contain exactly one verified OpenVersus plugin (.asi) asset (found ${pluginFiles.length})`);
  }
  const pluginVersion = pluginAssetVersion(pluginFiles[0].name);
  if (releaseVersion && pluginVersion && pluginVersion !== releaseVersion) {
    throw new Error(`release ${releaseVersion} ships plugin ${pluginFiles[0].name} for a different version`);
  }

  // IoStore content is only usable as a complete .pak/.utoc/.ucas group. A
  // traditional standalone .pak remains valid, and .sig is optional.
  const contentByStem = new Map<string, Set<string>>();
  for (const file of files.filter((candidate) => candidate.kind === "paks")) {
    const dot = file.name.lastIndexOf(".");
    const stem = file.name.slice(0, dot).toLowerCase();
    const extension = file.name.slice(dot + 1).toLowerCase();
    const extensions = contentByStem.get(stem) || new Set<string>();
    extensions.add(extension);
    contentByStem.set(stem, extensions);
  }
  for (const [stem, extensions] of contentByStem) {
    if ((extensions.has("utoc") || extensions.has("ucas")) &&
        !(extensions.has("pak") && extensions.has("utoc") && extensions.has("ucas"))) {
      throw new Error(`release contains an incomplete IoStore group for ${stem}`);
    }
  }

  // Content installs before the loaded ASI. Ordering is deterministic so logs,
  // tests, and the client's progress display all agree.
  return files.sort((left, right) => {
    if (left.kind !== right.kind) return left.kind === "paks" ? -1 : 1;
    return left.name.localeCompare(right.name);
  });
}

export function flattenClientReleaseManifest(files: ClientUpdateFile[]): Record<string, string | number> {
  const flattened: Record<string, string | number> = { file_count: files.length };
  files.forEach((file, index) => {
    flattened[`file_${index}_name`] = file.name;
    flattened[`file_${index}_kind`] = file.kind;
    flattened[`file_${index}_size`] = file.size;
    flattened[`file_${index}_sha256`] = file.sha256;
    flattened[`file_${index}_url`] = file.download_url;
  });
  return flattened;
}
