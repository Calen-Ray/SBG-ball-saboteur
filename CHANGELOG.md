# Changelog

## 0.2.0

- Fix the inventory hotkey label showing "Global Strike" while Ball Saboteur is equipped. The
  hotkey UI looked up the localized name through `GetEffectivelyEquippedItem`, which our remap
  redirected to Orbital Laser. A targeted `HotkeyUi.SetName` prefix now substitutes our
  `LocalizedName` whenever the local player's actual equipped slot is the saboteur item.
- Fix the lock-on reticle flickering while the saboteur is equipped. The vanilla `OnBUpdate`
  switch keys on the unmapped slot type and was nulling the lock-on target every frame; our
  postfix immediately re-set it, producing a per-frame null→target transition. Replaced with a
  `Prefix` that skips the vanilla switch and runs the OL targeting path once per frame.
- Fix the saboteur item not disappearing from the inventory after activation. The use flow now
  calls `DecrementUseFromSlotAt` (mirroring the local override and updating the hotkey icon)
  followed by `RemoveIfOutOfUses`, matching the vanilla Orbital Laser activation routine.
- Tint the dropped pickup model so it's visually distinct from the vanilla Orbital Laser.
  Configurable via `Visuals.TintPickupModel` and `Visuals.TintPickupColorHex` (default magenta).

## 0.1.3

- Fix a main-thread freeze when loading into the Driving Range (and any host-local lobby). v0.1.2
  injected the runtime item into `ItemPool.spawnChances`, which caused `MatchSetupRules.Update`
  to iterate a pool containing our `ItemType 1001` and index past `itemOrderLookup`. The pool
  mutation path (`Patch_ItemSpawnerSettings_ResetRuntimeData` and `Patch_MatchSetupRules_Update`)
  is removed in favor of a postfix on `ItemSpawnerSettings.GetRandomItemFor` that re-rolls the
  result into the custom item at a configured chance per pool.
- Fix item name showing as "Global Strike" in the pickup prompt and inventory. `ItemData.name`
  is a private `LocalizedString` that `MemberwiseClone` copies verbatim; the clone now nulls it
  so the getter lazy-re-resolves against `ITEM_1001`.
- Fix the runtime item appearing with no in-hand visual. Added a postfix on
  `PlayerInventory.GetEffectivelyEquippedItem` that remaps the custom `ItemType` back to
  `OrbitalLaser` for consumers that drive the hand-equipment switch, lock-on validation, and
  aim reticle. The actual use-path still reads `GetEffectiveSlot` directly so sabotage fires.
- Fix the item missing from the pause-menu item-probability grid. Restored the targeted
  `ItemCollection.get_Count` and `ItemCollection.GetItemAtIndex` patches so the grid's
  `for (int i = 0; i < AllItems.Count; i++)` loop sees the custom slot at `items.Length`.
- Make the pause-menu percentage reflect the configured spawn chance. Postfixes on
  `MatchSetupRules.GetWeight` and `GetItemPoolTotalWeight` inject a virtual weight
  `w = p · T / (1 − p)` for the custom item so `weight / totalWeight` displays as the
  configured percent without perturbing vanilla items' true shares after the re-roll.
- Fix the dropped `PhysicalItem` NRE'ing in `CourseManager.ServerSpawnItem` and disappearing.
  The runtime pickup template was stored with `SetActive(false)`, so clones inherited
  `activeSelf=false` and `PhysicalItem.Awake` never ran, leaving `AsEntity` null. The template
  is now parented under an inactive root GameObject — instances spawned at world root wake up
  normally.
- Fix remote players crashing when they pick up (or receive the `SpawnMessage` for) the dropped
  item. The runtime clone carried Orbital Laser's `NetworkIdentity.assetId` and wasn't in
  Mirror's prefab registry. Clear the inherited id via reflection, assign a deterministic id
  derived from `ModGuid + ":pickup:" + CustomItemTypeRaw` (FNV-1a), and register it with
  `NetworkClient.RegisterPrefab(prefab, assetId)`. Re-register from the existing
  `NetworkClient.RegisterMessageHandlers` postfix because Mirror clears
  `NetworkClient.prefabs` in `ClearSpawners()` on shutdown.
- Defer `TryRegisterCustomLocalizationEntry` from `ItemCollection.OnEnable` to the first
  `LocalizedString.GetLocalizedString` call. Touching `LocalizationSettings.StringDatabase`
  during `OnEnable` blocks on async localization init and deadlocks the main thread at game
  load.
- Broaden the `LocalizedString.GetLocalizedString` fallback rewrite to match any result
  containing `ITEM_1001`, not just the exact `"Data/ITEM_1001"` form, since Unity's
  missing-entry fallback varies by configuration.

## 0.1.2

- Fix the match-setup and item-probability UI paths so the runtime-added Ball Saboteur item no
  longer indexes past vanilla item lookup arrays. This removes the launch/lobby blocker while
  preserving the custom spawn-pool injection and sabotage behavior.
- Add the missing `UnityEngine.UI` project reference required by the guarded match-setup refresh
  path.

## 0.1.1

- Fix client disconnect when the server broadcasted ball-state messages: Mirror clears message
  handlers on shutdown, so re-register via a `NetworkClient.RegisterMessageHandlers` postfix and
  use `ReplaceHandler` to remain idempotent across reconnects.
- Add `Debug.EnableSelfSabotageHotkey` (F9) for single-player verification: while hosting
  locally, pressing F9 sabotages the local player's own ball, bypassing the self-target guard.

## 0.1.0

- Initial Ball Saboteur prototype.
