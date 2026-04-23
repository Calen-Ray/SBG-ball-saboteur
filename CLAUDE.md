# BallSaboteur — Claude context

BepInEx mod for **Super Battle Golf** (Unity 6, Mono). Adds a new runtime-registered sabotage
item. When used on another player, the target's golf ball becomes a cube until they complete a
qualifying stroke and the ball comes fully to rest, at which point it reverts to a sphere.

## Core ideas to remember

- **Runtime `ItemType`**: `CustomItemTypeRaw = 1001` cast to `ItemType`. The game's `ItemType` is
  an enum but treated as an int everywhere, so you can synthesize new values. Shadowing the
  Orbital Laser's `ItemType = 10` for the targeting/pickup flow is intentional for v0.1; the mod
  does **not** overwrite the vanilla Orbital Laser item.
- **ItemCollection injection**: the game keeps a private `Dictionary<ItemType, ItemData>` on
  `ItemCollection`. We reflect into that map and insert a cloned-from-Orbital-Laser `ItemData`
  with fields rewritten for our `CustomItemType`. Also patches `GetItemAtIndex` and `get_Count`
  so UI/pool iteration sees the new entry.
- **Random replacement for testing**: postfix on `ItemPool.GetWeightedRandomItem` re-rolls an
  Orbital Laser result into our custom item with configurable probability (BepInEx
  `ConfigEntry<float>`).
- **F8 grant hotkey**: adds one of our items directly to the local player's inventory for
  iteration (`InvokeInventoryCmdAddItem` via reflection → the private Mirror command).
- **F9 self-sabotage hotkey** (`Debug.EnableSelfSabotageHotkey`): in single-player/host-only, lets
  you sabotage your own ball to verify the state machine without another player present.
  Bypasses the normal self-target guard.

## Networking gotchas

- The mod sends custom Mirror messages (`BallSabotageStateMessage`, etc.).
- **Mirror clears message handlers on shutdown**, so re-register handlers via a
  `NetworkClient.RegisterMessageHandlers` postfix. Use `NetworkClient.ReplaceHandler<T>` (not
  `Register*`) so it's idempotent across reconnects — otherwise clients disconnect on the second
  server broadcast. This was the v0.1.1 fix.
- Target resolution uses `NetworkClient.spawned[netId]` to find the target's `NetworkIdentity`;
  don't hold these across scene changes.

## Build / deploy / release

```bash
dotnet build -c Release              # auto-deploys into r2modman Default profile
pwsh tools/package.ps1               # artifacts/Cray-BallSaboteur-<ver>.zip
```

Release: bump `manifest.json` + `CHANGELOG.md`, commit, tag, push, package, `gh release create ...`.
`.github/workflows/release.yml` publishes on `release:published`. Team **Cray**, community
**super-battle-golf**.

## Known landmines

- **Multiplayer sync is fragile.** All players need the same version — runtime `ItemType`
  numbers are consistent across clients, but only because everyone computes the same int. If you
  ever change `CustomItemTypeRaw`, bump the mod major version so mismatched clients fail fast
  instead of desyncing.
- **Never register a handler without `ReplaceHandler`** — see networking notes above.
- GreenTF/upload-thunderstore-package@v4.3 has an inverted `--repository` branch; workflow passes
  `repo: thunderstore.io` to work around it. Do not remove.
- Dev-time screenshots in the repo root are gitignored via `*.png` with `!icon.png`. If you add
  artwork the user wants tracked, put it in a `cover-art/` subfolder (not gitignored) and use
  `icon.png` for the Thunderstore 256×256.
- Claude must not commit/push under its own identity. Commit under the user's config only when
  explicitly approved, without `Co-Authored-By: Claude` trailers.
