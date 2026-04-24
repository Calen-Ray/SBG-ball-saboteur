# BallSaboteur — what's next

Known follow-ups after the 0.1.3 rescue release. Roughly in priority order.

## Must-verify (cross-client)

- **Multiplayer drop/pickup round-trip.** 0.1.3 fixed this on single-host in local testing
  (deterministic `assetId` + `NetworkClient.RegisterPrefab`), but two-player validation was
  not possible in the session. Confirm:
  - Remote client can pick up a dropped BallSaboteur without crashing.
  - Remote client sees the correct "Ball Saboteur" pickup prompt (not "Global Strike").
  - Host-dropped + remote-dropped items both round-trip cleanly.
- **Mid-match reconnect.** `Patch_NetworkClient_RegisterMessageHandlers` re-runs
  `TryRegisterCustomPickupPrefab` after Mirror clears `NetworkClient.prefabs`, but this has
  only been reasoned about, not exercised. Force a disconnect/reconnect and confirm a drop
  still spawns/resolves on both sides.

## Cosmetic gaps left over from the clone-from-Orbital-Laser approach

- **Distinct inventory icon.** `CloneItemData` copies `ItemData.Icon` via `MemberwiseClone`, so
  the slot and pause-menu grid show the Orbital Laser icon. Author a 128×128 saboteur icon,
  ship it as a sprite loaded from the plugin folder, and assign it via the existing
  `itemDataIconField` reflection path (add one if not cached).
- **Distinct in-hand equipment.** `Patch_PlayerInventory_GetEffectivelyEquippedItem` remaps the
  custom type to `OrbitalLaser` so the OL mesh shows in hand. A custom `Equipment` prefab (with
  the cube motif) would be nicer. Two routes:
  1. Clone the `EquipmentCollection` entry the same way we clone `ItemData`, registered under a
     new `EquipmentType` value. Requires patching `UpdateEquipmentSwitchers` to map the custom
     `ItemType` to the custom `EquipmentType` instead of remapping the item type itself.
  2. Post-instantiate hook on the OL equipment prefab to swap the mesh/material when it's
     equipped for our item.
- **Distinct pickup-prefab appearance.** The dropped `PhysicalItem` is a clone of OL's pickup
  prefab, so it looks identical to an OL pickup in the world. Swap the mesh/tint or append a
  cube visual similar to `RuntimeBallMorph`.

## UX / flavor

- **Announcer line on sabotage.** Hook `CourseManager.ServerAnnounceForAll` (or similar) when
  `TryApplyCustomSabotageOnServer` fires so the target gets a callout. Reuse an existing line
  until a custom VO is recorded.
- **Target-side feedback.** Add a short sting/vfx on the sabotaged ball at activation so the
  target knows what happened without seeing the attacker.
- **Cube corner bounce tuning.** `RuntimeBallMorph` uses a rounded compound (8 corner spheres).
  `CornerRadiusFraction = 0.25f` and `CubeMassMultiplier = 3.0f` were picked by eye — re-tune
  once we have telemetry from a few matches (do cubes wedge in terrain seams? Do they stop too
  fast on the green?).

## Code hygiene

- **Delete dead pool-injection code.** `EnsurePoolInjected`, `TrackPoolSpawnPercent`,
  `trackedPoolSpawnPercents`, `injectedPools`, and the associated `ItemPool.spawnChances`
  reflection fields are unused after the 0.1.3 refactor. Same for the `MatchSetupRules`
  unsupported-spawn-change plumbing (`HandleUnsupportedMatchSetupSpawnChanceUpdated`,
  `Patch_MatchSetupRules_SpawnChanceUpdated`, `TryGetMatchSetupSliderIndex`,
  `CurrentMatchSetupPoolContainsUiUnsupportedItem`, `RunSafeMatchSetupUpdate`) — only needed
  if the item ever gets back into `pool.SpawnChances`, which it doesn't.
- **Stop relying on `<OnBUpdate>g__UpdateOrbitalLaserLockOnTarget|108_3`.** The compiler-
  generated local-function name is version-fragile; a game update that adds/removes any local
  function inside `PlayerInventory.OnBUpdate` can shift the `|108_3` suffix and silently break
  lock-on targeting. Reproduce the logic inline instead of reflecting into the local function.
- **Add a version handshake.** CLAUDE.md already warns that `CustomItemTypeRaw` changes must
  bump the major version so mismatched clients fail fast, but nothing enforces it. Send a
  one-shot `HandshakeMessage` with the mod version on connect; refuse custom-item Cmds if the
  server and client disagree, so a forgotten version bump surfaces as a log error instead of
  a silent desync.

## Maybe-later

- **Config: enable/disable in Driving Range.** Some players may want the item to be
  drive-range-only (practice) or explicitly excluded from it. Currently the re-roll fires there.
- **Config: exclude certain pools entirely.** Right now `SpawnChancePercent` applies uniformly
  to non-leader pools. Could split into per-distance-bucket settings if the 5% flat feels off.
- **Telemetry hook.** Emit a curated `event:BallSabotaged` line through `SBGDevHarness`
  (when present) so I can harvest apply/restore/rescue traces from test sessions without
  scraping the full log.
