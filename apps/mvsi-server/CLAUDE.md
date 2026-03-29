# CLAUDE.md — Project Instructions

See AGENTS.md for full architecture docs. This file covers working preferences and quick rules.

## Runtime
- **Bun only.** No npm/npx/node commands. Use `bun run`, `bun test`, etc.
- Type-check: `bun run tsc --noEmit` (from monorepo root or with correct tsconfig path)

## Code Patterns
- Atomic Redis state mutations **must** use Lua scripts (`redisClient.eval()`). Never read-then-write for lobby/match state.
- Routes follow SSC pattern: `PUT /ssc/invoke/{action}` returning `{ body, metadata: null, return_code: 0 }`
- WebSocket notifications go through Redis Pub/Sub — HTTP and WS are separate processes.
- Return plain objects from route handlers — Hydra encoding is handled by middleware.

## Shared Packages
- `@mvsi/database` — MongoDB models
- `@mvsi/redis` — `redisClient`
- `@mvsi/env` — `env.VARIABLE_NAME`
- `@mvsi/logger` — `logger.info()`, `logger.error()`
- `@mvsi/hydra` — binary protocol (don't touch unless asked)

## Module Structure
- `src/modules/{name}/{name}.route.ts` — routes (register on `MAIN_APP`)
- `src/modules/{name}/{name}.service.ts` — business logic + Lua scripts
- `src/modules/{name}/{name}.types.ts` — types + Redis channel constants
- `src/modules/{name}/{name}.ws.ts` — WebSocket Pub/Sub handlers

## Don't Forget
- `MAIN_APP.use(router)` at bottom of route files
- Import route files in `src/index.elysia.ts` as side effects
- `JSON.stringify()` before `redisClient.publish()`
- Clean up per-player Redis keys on disconnect
- MongoDB requires replica set (change streams)
