# Project Rules

## Workflow
- Bypass permissions is enabled. Do NOT ask for permission to run commands, read files, edit files, or use tools. Just do the work.
- The ONLY exception: pushing to GitHub. Before `git push`, confirm with the user that they've tested and want to push.
- ALWAYS test changes before pushing to GitHub. Never push untested code.

## Git
- Push to `fork` remote (tuggernuts1123), never to `origin` (openversus org).
- Origin push is disabled (`no_push_to_org`) as a safeguard.
- Branch: local `rankedfix` tracks remote `fork/rankfixes`.

## Code
- Character slugs from the game client have inconsistent casing (e.g., `character_Jason`, `character_C030`, `character_BananaGuard`). Store and return them exactly as the game client sends them — do NOT normalize case. The game looks up `character_wins[slug]`, `character_ringouts[slug]` etc. using the exact casing it sent, so normalizing breaks badge tracking for mixed-case characters.
