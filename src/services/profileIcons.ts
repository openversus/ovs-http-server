import { getAssetsByType } from "../loadAssets";

export const DEFAULT_PROFILE_ICON = "profile_icon_default";

/**
 * The asset path the game loads a profile icon from. The game draws a profile icon by this
 * path, not by its slug: an empty or unknown path shows its WB fallback icon instead. An
 * unknown slug gets the default icon's path.
 */
export function profileIconAssetPath(slug: string | undefined | null): string {
  const icons = getAssetsByType("ProfileIconData");
  return icons.find((icon) => icon.slug === slug)?.assetPath
    ?? icons.find((icon) => icon.slug === DEFAULT_PROFILE_ICON)?.assetPath
    ?? "";
}
