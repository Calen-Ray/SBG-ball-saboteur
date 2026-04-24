using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace BallSaboteur
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "sbg.ballsaboteur";
        public const string ModName = "BallSaboteur";
        public const string ModVersion = "0.1.2";

        internal const int CustomItemTypeRaw = 1001;
        internal const string CustomItemDisplayName = "Ball Saboteur";
        internal static readonly string CustomItemLocalizationFallback = $"Data/ITEM_{CustomItemTypeRaw}";
        internal static readonly ItemType CustomItemType = (ItemType)CustomItemTypeRaw;
        private static readonly ItemType OrbitalLaserItemType = (ItemType)10;

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        private static readonly Dictionary<uint, ActiveSabotage> ActiveSabotages = new Dictionary<uint, ActiveSabotage>();
        private static readonly Dictionary<uint, RuntimeBallMorph> BallMorphs = new Dictionary<uint, RuntimeBallMorph>();

        private static FieldInfo itemCollectionMapField;
        private static FieldInfo itemCollectionItemsField;
        private static FieldInfo physicalItemTypeField;
        private static FieldInfo itemDataTypeField;
        private static FieldInfo itemDataPrefabField;
        private static FieldInfo itemDataMaxUsesField;
        private static FieldInfo itemDataCanUsageAffectBallsField;
        private static FieldInfo ballRenderersField;
        private static FieldInfo itemPoolSpawnChancesField;
        private static Type itemSpawnChanceType;
        private static FieldInfo itemSpawnChanceItemField;
        private static FieldInfo itemSpawnChanceWeightField;
        private static readonly HashSet<ItemPool> injectedPools = new HashSet<ItemPool>();

        private static MethodInfo objectMemberwiseCloneMethod;
        private static MethodInfo updateOrbitalLaserLockOnTargetMethod;
        private static MethodInfo inventoryGetEffectiveSlotMethod;
        private static MethodInfo inventoryCanUseEquippedItemMethod;
        private static MethodInfo inventoryCancelItemUseMethod;
        private static MethodInfo inventoryCancelItemFlourishMethod;
        private static MethodInfo inventorySetItemUseTimestampMethod;
        private static MethodInfo inventoryIncrementAndGetCurrentItemUseIdMethod;
        private static MethodInfo inventoryCmdDecrementUseFromSlotAtMethod;
        private static MethodInfo inventoryCmdActivateOrbitalLaserMethod;
        private static MethodInfo inventorySetLockOnTargetMethod;
        private static MethodInfo inventoryOnActivatedOrbitalLaserMethod;
        private static MethodInfo inventoryCmdAddItemMethod;

        private static Type onBUpdateDisplayClassType;
        private static FieldInfo onBUpdateDisplayThisField;
        private static FieldInfo onBUpdateDisplaySlotField;
        private static FieldInfo matchSetupRulesCurrentItemPoolDirtyField;
        private static FieldInfo matchSetupRulesCurrentItemPoolIndexField;
        private static FieldInfo matchSetupRulesSpawnChanceWeightsField;
        private static FieldInfo matchSetupRulesTotalWeightPerPoolField;
        private static FieldInfo matchSetupRulesSpawnChanceSlidersField;
        private static FieldInfo matchSetupRulesItemOrderLookupField;
        private static MethodInfo matchSetupRulesServerUpdateSpawnChanceValueMethod;
        private static MethodInfo matchSetupRulesUpdateTotalWeightForPoolMethod;
        private static MethodInfo matchSetupRulesGetCurrentItemPoolMethod;
        private static MethodInfo matchSetupRulesUpdateSliderGreyedOutMethod;

        private static ItemData customItemData;
        private static GameObject customPickupPrefab;
        private static bool networkSerializersRegistered;

        private ConfigEntry<float> restoreDistanceMetersConfig;
        private ConfigEntry<float> spawnChancePercentConfig;
        private ConfigEntry<float> leaderSpawnChancePercentConfig;
        private ConfigEntry<float> hopImpulseConfig;
        private ConfigEntry<float> fallRescueThresholdConfig;
        private ConfigEntry<float> cubeMassMultiplierConfig;
        private ConfigEntry<bool> debugGrantHotkeyEnabledConfig;
        private ConfigEntry<bool> debugSelfSabotageHotkeyEnabledConfig;
        private bool debugGrantPressedLastFrame;
        private bool debugSelfSabotagePressedLastFrame;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            restoreDistanceMetersConfig = Config.Bind(
                "Gameplay",
                "RestoreDistanceMeters",
                3.0f,
                "Minimum stroke distance before a sabotaged cube ball can restore once it stops.");
            spawnChancePercentConfig = Config.Bind(
                "Gameplay",
                "SpawnChancePercent",
                5.0f,
                "Target chance (0-100) for Ball Saboteur to roll from 'behind the leader' and mobility pools. Computed against each pool's existing total weight at injection time; changes require a game restart.");
            leaderSpawnChancePercentConfig = Config.Bind(
                "Gameplay",
                "LeaderSpawnChancePercent",
                0.0f,
                "Target chance (0-100) for Ball Saboteur to roll from the 'In the lead' pool. Defaults to 0 so leaders don't receive the item.");
            hopImpulseConfig = Config.Bind(
                "Gameplay",
                "ActivationHopImpulse",
                3.0f,
                "Upward velocity (m/s) applied to the ball at sabotage activation so the cube spawns airborne. Skipped if the ball is mid-flight. Set to 0 to disable.");
            fallRescueThresholdConfig = Config.Bind(
                "Gameplay",
                "FallRescueThresholdMeters",
                15.0f,
                "If a sabotaged ball falls more than this many meters below its stroke-start position, it is rescued to the stroke-start position and the sabotage ends. Guards against cube corners tunneling through terrain.");
            cubeMassMultiplierConfig = Config.Bind(
                "Gameplay",
                "CubeMassMultiplier",
                3.0f,
                "Rigidbody mass is multiplied by this factor while the ball is a cube. Higher values make the cube roll less and thud harder on landing. Restored to the original mass when the sabotage ends. 1.0 disables.");
            debugGrantHotkeyEnabledConfig = Config.Bind(
                "Debug",
                "EnableGrantHotkey",
                true,
                "When enabled, pressing F8 grants the local player one Ball Saboteur item.");
            debugSelfSabotageHotkeyEnabledConfig = Config.Bind(
                "Debug",
                "EnableSelfSabotageHotkey",
                true,
                "When enabled and hosting locally, pressing F9 sabotages the local player's own ball (bypasses self-target guard for testing).");

            CacheReflection();
            RegisterNetworkHandlers();

            new Harmony(ModGuid).PatchAll();
            Log.LogInfo($"{ModName} v{ModVersion} loaded.");
        }

        private void Update()
        {
            UpdateDebugHotkey();
            UpdateActiveSabotages();
        }

        private static void CacheReflection()
        {
            itemCollectionMapField = AccessTools.Field(typeof(ItemCollection), "allItemData");
            itemCollectionItemsField = AccessTools.Field(typeof(ItemCollection), "items");
            physicalItemTypeField = AccessTools.Field(typeof(PhysicalItem), "itemType");
            itemDataTypeField = AccessTools.Field(typeof(ItemData), "<Type>k__BackingField");
            itemDataPrefabField = AccessTools.Field(typeof(ItemData), "<Prefab>k__BackingField");
            itemDataMaxUsesField = AccessTools.Field(typeof(ItemData), "<MaxUses>k__BackingField");
            itemDataCanUsageAffectBallsField = AccessTools.Field(typeof(ItemData), "<CanUsageAffectBalls>k__BackingField");
            ballRenderersField = AccessTools.Field(typeof(GolfBall), "renderers");
            itemPoolSpawnChancesField = AccessTools.Field(typeof(ItemPool), "spawnChances");
            itemSpawnChanceType = typeof(ItemPool).GetNestedType("ItemSpawnChance");
            itemSpawnChanceItemField = itemSpawnChanceType?.GetField("item");
            itemSpawnChanceWeightField = itemSpawnChanceType?.GetField("spawnChanceWeight");

            objectMemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
            updateOrbitalLaserLockOnTargetMethod = AccessTools.Method(typeof(PlayerInventory), "<OnBUpdate>g__UpdateOrbitalLaserLockOnTarget|108_3");
            inventoryGetEffectiveSlotMethod = AccessTools.Method(typeof(PlayerInventory), "GetEffectiveSlot");
            inventoryCanUseEquippedItemMethod = AccessTools.Method(typeof(PlayerInventory), "CanUseEquippedItem");
            inventoryCancelItemUseMethod = AccessTools.Method(typeof(PlayerInventory), "CancelItemUse");
            inventoryCancelItemFlourishMethod = AccessTools.Method(typeof(PlayerInventory), "CancelItemFlourish");
            inventorySetItemUseTimestampMethod = AccessTools.Method(typeof(PlayerInventory), "set_ItemUseTimestamp");
            inventoryIncrementAndGetCurrentItemUseIdMethod = AccessTools.Method(typeof(PlayerInventory), "IncrementAndGetCurrentItemUseId");
            inventoryCmdDecrementUseFromSlotAtMethod = AccessTools.Method(typeof(PlayerInventory), "CmdDecrementUseFromSlotAt");
            inventoryCmdActivateOrbitalLaserMethod = AccessTools.Method(typeof(PlayerInventory), "CmdActivateOrbitalLaser");
            inventorySetLockOnTargetMethod = AccessTools.Method(typeof(PlayerInventory), "SetLockOnTarget");
            inventoryOnActivatedOrbitalLaserMethod = AccessTools.Method(typeof(PlayerInventory), "OnActivatedOrbitalLaser");
            inventoryCmdAddItemMethod = AccessTools.Method(typeof(PlayerInventory), "CmdAddItem");

            onBUpdateDisplayClassType = AccessTools.Inner(typeof(PlayerInventory), "<>c__DisplayClass108_0");
            onBUpdateDisplayThisField = AccessTools.Field(onBUpdateDisplayClassType, "<>4__this");
            onBUpdateDisplaySlotField = AccessTools.Field(onBUpdateDisplayClassType, "effectiveSlot");

            matchSetupRulesCurrentItemPoolDirtyField = AccessTools.Field(typeof(MatchSetupRules), "currentItemPoolDirty");
            matchSetupRulesCurrentItemPoolIndexField = AccessTools.Field(typeof(MatchSetupRules), "currentItemPoolIndex");
            matchSetupRulesSpawnChanceWeightsField = AccessTools.Field(typeof(MatchSetupRules), "spawnChanceWeights");
            matchSetupRulesTotalWeightPerPoolField = AccessTools.Field(typeof(MatchSetupRules), "totalWeightPerPool");
            matchSetupRulesSpawnChanceSlidersField = AccessTools.Field(typeof(MatchSetupRules), "spawnChanceSliders");
            matchSetupRulesItemOrderLookupField = AccessTools.Field(typeof(MatchSetupRules), "itemOrderLookup");
            matchSetupRulesServerUpdateSpawnChanceValueMethod = AccessTools.Method(typeof(MatchSetupRules), "ServerUpdateSpawnChanceValue");
            matchSetupRulesUpdateTotalWeightForPoolMethod = AccessTools.Method(typeof(MatchSetupRules), "UpdateTotalWeightForPool");
            matchSetupRulesGetCurrentItemPoolMethod = AccessTools.Method(typeof(MatchSetupRules), "GetCurrentItemPool");
            matchSetupRulesUpdateSliderGreyedOutMethod = AccessTools.Method(typeof(MatchSetupRules), "UpdateSliderGreyedOut");
        }

        private static void RegisterNetworkHandlers()
        {
            if (!networkSerializersRegistered)
            {
                Writer<BallSabotageStateMessage>.write = WriteBallSabotageState;
                Reader<BallSabotageStateMessage>.read = ReadBallSabotageState;
                networkSerializersRegistered = true;
            }

            NetworkClient.ReplaceHandler<BallSabotageStateMessage>(OnBallSabotageStateMessage, false);
        }

        private static void WriteBallSabotageState(NetworkWriter writer, BallSabotageStateMessage message)
        {
            NetworkWriterExtensions.WriteUInt(writer, message.BallNetId);
            NetworkWriterExtensions.WriteBool(writer, message.IsActive);
        }

        private static BallSabotageStateMessage ReadBallSabotageState(NetworkReader reader)
        {
            return new BallSabotageStateMessage
            {
                BallNetId = NetworkReaderExtensions.ReadUInt(reader),
                IsActive = NetworkReaderExtensions.ReadBool(reader)
            };
        }

        private static void OnBallSabotageStateMessage(BallSabotageStateMessage message)
        {
            if (!TryGetGolfBall(message.BallNetId, out GolfBall ball))
                return;

            if (message.IsActive)
                EnsureCubeApplied(ball);
            else
                EnsureSphereApplied(ball);
        }

        private void UpdateDebugHotkey()
        {
            Keyboard keyboard = Keyboard.current;

            if (debugGrantHotkeyEnabledConfig.Value)
            {
                bool grantPressed = keyboard != null && keyboard.f8Key.isPressed;
                if (grantPressed && !debugGrantPressedLastFrame)
                {
                    PlayerInventory inventory = GameManager.LocalPlayerInventory;
                    if (inventory != null)
                        TryDebugGrantItem(inventory);
                }
                debugGrantPressedLastFrame = grantPressed;
            }

            if (debugSelfSabotageHotkeyEnabledConfig.Value)
            {
                bool selfPressed = keyboard != null && keyboard.f9Key.isPressed;
                if (selfPressed && !debugSelfSabotagePressedLastFrame)
                    TryDebugSelfSabotage();
                debugSelfSabotagePressedLastFrame = selfPressed;
            }
        }

        private static void TryDebugSelfSabotage()
        {
            if (!NetworkServer.active)
            {
                Log?.LogWarning("Self-sabotage hotkey requires hosting locally (server authority).");
                return;
            }

            PlayerInventory inventory = GameManager.LocalPlayerInventory;
            PlayerInfo localPlayer = inventory != null ? inventory.PlayerInfo : null;
            PlayerGolfer golfer = localPlayer != null ? localPlayer.AsGolfer : null;
            GolfBall ball = golfer != null ? golfer.OwnBall : null;
            if (ball == null || ball.netIdentity == null)
            {
                Log?.LogWarning("Self-sabotage: local player has no owned ball yet.");
                return;
            }

            uint ballNetId = ball.netId;
            ActiveSabotages[ballNetId] = new ActiveSabotage
            {
                BallNetId = ballNetId,
                Ball = ball,
                Owner = golfer,
                StrokeStartPosition = ResolveRescueAnchor(ball),
                WaitingForBallToStop = false
            };
            ApplyActivationHopIfStationary(ball);
            EnsureCubeApplied(ball);
            BroadcastSabotageState(ballNetId, true);
            Log?.LogInfo($"Self-sabotage applied to {golfer.name} (ball net id {ballNetId}).");
        }

        private void UpdateActiveSabotages()
        {
            if (!NetworkServer.active || ActiveSabotages.Count == 0)
                return;

            List<uint> restoreList = null;

            foreach (var pair in ActiveSabotages)
            {
                ActiveSabotage sabotage = pair.Value;
                if (sabotage.Ball == null || sabotage.Owner == null)
                {
                    if (restoreList == null) restoreList = new List<uint>();
                    restoreList.Add(pair.Key);
                    continue;
                }

                float fallDistance = sabotage.StrokeStartPosition.y - sabotage.Ball.transform.position.y;
                if (fallDistance > fallRescueThresholdConfig.Value)
                {
                    RescueFallenBall(sabotage);
                    if (restoreList == null) restoreList = new List<uint>();
                    restoreList.Add(pair.Key);
                    continue;
                }

                if (!sabotage.WaitingForBallToStop)
                    continue;

                if (sabotage.Owner.IsSwinging || !sabotage.Ball.IsStationary)
                    continue;

                sabotage.WaitingForBallToStop = false;
                float travel = Vector3.Distance(sabotage.StrokeStartPosition, sabotage.Ball.transform.position);
                if (travel >= restoreDistanceMetersConfig.Value)
                {
                    if (restoreList == null) restoreList = new List<uint>();
                    restoreList.Add(pair.Key);
                }
            }

            if (restoreList == null)
                return;

            foreach (uint ballNetId in restoreList)
                RestoreSabotage(ballNetId);
        }

        private void RestoreSabotage(uint ballNetId)
        {
            if (!ActiveSabotages.TryGetValue(ballNetId, out ActiveSabotage sabotage))
                return;

            ActiveSabotages.Remove(ballNetId);
            if (sabotage.Ball != null)
                EnsureSphereApplied(sabotage.Ball);
            BroadcastSabotageState(ballNetId, false);
            Log?.LogInfo($"Restored sphere on ball net id {ballNetId}.");
        }

        internal static void EnsureCustomItemRegistered(ItemCollection collection)
        {
            if (collection == null || itemCollectionMapField == null)
                return;

            Dictionary<ItemType, ItemData> itemMap = itemCollectionMapField.GetValue(collection) as Dictionary<ItemType, ItemData>;
            if (itemMap == null)
                return;

            if (customItemData != null && itemMap.ContainsKey(CustomItemType))
                return;

            if (!itemMap.TryGetValue(OrbitalLaserItemType, out ItemData orbitalLaserData) || orbitalLaserData == null)
            {
                Log?.LogWarning("Ball Saboteur could not find Orbital Laser data to clone.");
                return;
            }

            EnsureCustomPickupPrefab(orbitalLaserData);
            if (customPickupPrefab == null)
                return;

            customItemData = CloneItemData(orbitalLaserData, customPickupPrefab);
            itemMap[CustomItemType] = customItemData;
            Log?.LogInfo("Registered Ball Saboteur runtime item.");
        }

        private static void EnsureCustomPickupPrefab(ItemData orbitalLaserData)
        {
            if (customPickupPrefab != null)
                return;

            GameObject sourcePrefab = itemDataPrefabField?.GetValue(orbitalLaserData) as GameObject;
            if (sourcePrefab == null)
                return;

            customPickupPrefab = Instantiate(sourcePrefab);
            customPickupPrefab.name = "BallSaboteurPickupRuntime";
            customPickupPrefab.hideFlags = HideFlags.HideAndDontSave;
            customPickupPrefab.SetActive(false);
            DontDestroyOnLoad(customPickupPrefab);

            PhysicalItem physicalItem = customPickupPrefab.GetComponent<PhysicalItem>();
            if (physicalItem != null && physicalItemTypeField != null)
                physicalItemTypeField.SetValue(physicalItem, CustomItemType);
        }

        private static ItemData CloneItemData(ItemData source, GameObject prefab)
        {
            ItemData clone = (ItemData)objectMemberwiseCloneMethod.Invoke(source, null);
            itemDataTypeField?.SetValue(clone, CustomItemType);
            itemDataPrefabField?.SetValue(clone, prefab);
            itemDataMaxUsesField?.SetValue(clone, 1);
            itemDataCanUsageAffectBallsField?.SetValue(clone, true);
            return clone;
        }

        internal static bool TryUseCustomItem(PlayerInventory inventory, ref bool shouldEatInput)
        {
            shouldEatInput = true;

            if (inventory == null || !inventory.isLocalPlayer)
                return false;

            InventorySlot equippedSlot = InvokeInventoryGetEffectiveSlot(inventory, inventory.EquippedItemIndex);
            if (equippedSlot.itemType != CustomItemType)
                return false;

            ItemData equippedItemData = null;
            bool shouldEat = true;
            bool isFlourish = false;
            if (!InvokeInventoryCanUseEquippedItem(inventory, false, false, ref equippedSlot, ref equippedItemData, ref shouldEat, ref isFlourish))
            {
                shouldEatInput = shouldEat;
                return false;
            }

            if (isFlourish)
            {
                shouldEatInput = false;
                return false;
            }

            LockOnTarget target = inventory.LockOnTarget;
            Entity targetEntity = target != null ? target.AsEntity : null;
            PlayerInfo targetPlayer = targetEntity != null && targetEntity.IsPlayer ? targetEntity.PlayerInfo : null;
            PlayerInfo user = inventory.PlayerInfo;
            if (targetPlayer == null || user == null || targetPlayer == user)
            {
                shouldEatInput = false;
                return false;
            }

            PlayerGolfer targetGolfer = targetPlayer.AsGolfer;
            GolfBall targetBall = targetGolfer != null ? targetGolfer.OwnBall : null;
            Hittable targetHittable = targetPlayer.AsHittable;
            if (targetBall == null || targetHittable == null)
            {
                shouldEatInput = false;
                return false;
            }

            inventoryCancelItemUseMethod?.Invoke(inventory, null);
            inventoryCancelItemFlourishMethod?.Invoke(inventory, null);
            inventorySetItemUseTimestampMethod?.Invoke(inventory, new object[] { Time.timeAsDouble });
            user.CancelEmote(false);

            ItemUseId itemUseId = InvokeInventoryIncrementAndGetCurrentItemUseId(inventory, CustomItemType);
            inventoryCmdDecrementUseFromSlotAtMethod?.Invoke(inventory, new object[] { inventory.EquippedItemIndex });
            inventoryCmdActivateOrbitalLaserMethod?.Invoke(inventory, new object[] { targetHittable, targetBall.transform.position, itemUseId });
            inventorySetLockOnTargetMethod?.Invoke(inventory, new object[] { null });

            try
            {
                inventoryOnActivatedOrbitalLaserMethod?.Invoke(inventory, null);
            }
            catch (Exception ex)
            {
                Log?.LogDebug($"Local Orbital Laser activation reuse failed: {ex.Message}");
            }

            shouldEatInput = false;
            return true;
        }

        internal static void UpdateCustomLockOnTargeting(PlayerInventory inventory)
        {
            if (inventory == null || updateOrbitalLaserLockOnTargetMethod == null || onBUpdateDisplayClassType == null)
                return;

            InventorySlot slot = InvokeInventoryGetEffectiveSlot(inventory, inventory.EquippedItemIndex);
            if (slot.itemType != CustomItemType)
                return;

            object displayClass = Activator.CreateInstance(onBUpdateDisplayClassType);
            onBUpdateDisplayThisField.SetValue(displayClass, inventory);
            onBUpdateDisplaySlotField.SetValue(displayClass, slot);
            updateOrbitalLaserLockOnTargetMethod.Invoke(inventory, new[] { displayClass });
        }

        internal static bool TryApplyCustomSabotageOnServer(ItemUseId itemUseId, Hittable target)
        {
            if (itemUseId.itemType != CustomItemType)
                return false;

            if (!NetworkServer.active)
                return true;

            PlayerInfo targetPlayer = target != null ? target.GetComponent<PlayerInfo>() : null;
            if (targetPlayer == null && target != null)
            {
                Entity entity = target.GetComponent<Entity>();
                if (entity != null)
                    targetPlayer = entity.PlayerInfo;
            }

            PlayerGolfer golfer = targetPlayer != null ? targetPlayer.AsGolfer : null;
            GolfBall ball = golfer != null ? golfer.OwnBall : null;
            if (ball == null || ball.netIdentity == null)
            {
                Log?.LogWarning("Ball Saboteur activation had no valid target ball.");
                return true;
            }

            uint ballNetId = ball.netId;
            ActiveSabotages[ballNetId] = new ActiveSabotage
            {
                BallNetId = ballNetId,
                Ball = ball,
                Owner = golfer,
                StrokeStartPosition = ResolveRescueAnchor(ball),
                WaitingForBallToStop = false
            };

            ApplyActivationHopIfStationary(ball);
            EnsureCubeApplied(ball);
            BroadcastSabotageState(ballNetId, true);
            Log?.LogInfo($"Applied Ball Saboteur to {golfer.name}.");
            return true;
        }

        internal static void MarkShotStarted(PlayerGolfer golfer)
        {
            if (!NetworkServer.active || golfer == null)
                return;

            GolfBall ball = golfer.OwnBall;
            if (ball == null || !ActiveSabotages.TryGetValue(ball.netId, out ActiveSabotage sabotage))
                return;

            sabotage.StrokeStartPosition = ball.ServerLastStrokePosition;
            if (sabotage.StrokeStartPosition == Vector3.zero)
                sabotage.StrokeStartPosition = ball.transform.position;
            sabotage.WaitingForBallToStop = true;
            Log?.LogInfo($"Shot started on sabotaged ball {ball.netId} from {sabotage.StrokeStartPosition}.");
        }

        private static void BroadcastSabotageState(uint ballNetId, bool isActive)
        {
            if (!NetworkServer.active)
                return;

            NetworkServer.SendToAll(new BallSabotageStateMessage
            {
                BallNetId = ballNetId,
                IsActive = isActive
            });
        }

        private static bool TryGetGolfBall(uint ballNetId, out GolfBall ball)
        {
            ball = null;
            if (!NetworkClient.spawned.TryGetValue(ballNetId, out NetworkIdentity identity) || identity == null)
                return false;

            ball = identity.GetComponent<GolfBall>();
            return ball != null;
        }

        private static InventorySlot InvokeInventoryGetEffectiveSlot(PlayerInventory inventory, int index)
        {
            if (inventoryGetEffectiveSlotMethod == null)
                return default;

            return (InventorySlot)inventoryGetEffectiveSlotMethod.Invoke(inventory, new object[] { index });
        }

        private static bool InvokeInventoryCanUseEquippedItem(
            PlayerInventory inventory,
            bool altUse,
            bool isAirhornReaction,
            ref InventorySlot equippedSlot,
            ref ItemData equippedItemData,
            ref bool shouldEatInput,
            ref bool isFlourish)
        {
            if (inventoryCanUseEquippedItemMethod == null)
                return false;

            object[] args = { altUse, isAirhornReaction, equippedSlot, equippedItemData, shouldEatInput, isFlourish };
            bool result = (bool)inventoryCanUseEquippedItemMethod.Invoke(inventory, args);
            equippedSlot = (InventorySlot)args[2];
            equippedItemData = args[3] as ItemData;
            shouldEatInput = (bool)args[4];
            isFlourish = (bool)args[5];
            return result;
        }

        private static ItemUseId InvokeInventoryIncrementAndGetCurrentItemUseId(PlayerInventory inventory, ItemType itemType)
        {
            if (inventoryIncrementAndGetCurrentItemUseIdMethod == null)
                return default;

            return (ItemUseId)inventoryIncrementAndGetCurrentItemUseIdMethod.Invoke(inventory, new object[] { itemType });
        }

        private static void InvokeInventoryCmdAddItem(PlayerInventory inventory, ItemType itemType)
        {
            inventoryCmdAddItemMethod?.Invoke(inventory, new object[] { itemType });
        }

        private static void TryDebugGrantItem(PlayerInventory inventory)
        {
            int maxUses = customItemData != null ? customItemData.MaxUses : 1;
            if (NetworkServer.active)
            {
                bool added = inventory.ServerTryAddItem(CustomItemType, maxUses);
                Log?.LogInfo($"Granted Ball Saboteur to local player via ServerTryAddItem (result: {added}).");
            }
            else
            {
                InvokeInventoryCmdAddItem(inventory, CustomItemType);
                Log?.LogInfo("Sent CmdAddItem for Ball Saboteur (requires cheats enabled on host to succeed).");
            }
        }

        private static Vector3 ResolveRescueAnchor(GolfBall ball)
        {
            if (ball == null)
                return Vector3.zero;
            Vector3 serverLast = ball.ServerLastStrokePosition;
            return serverLast != Vector3.zero ? serverLast : ball.transform.position;
        }

        private static void ApplyActivationHopIfStationary(GolfBall ball)
        {
            if (ball == null || Instance == null)
                return;
            float impulse = Instance.hopImpulseConfig.Value;
            if (impulse <= 0f || !ball.IsStationary)
                return;
            Rigidbody rb = ball.GetComponent<Rigidbody>();
            if (rb == null)
                return;
            rb.AddForce(Vector3.up * impulse, ForceMode.VelocityChange);
        }

        private static void RescueFallenBall(ActiveSabotage sabotage)
        {
            Vector3 fallPosition = sabotage.Ball.transform.position;
            Rigidbody rb = sabotage.Ball.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = sabotage.StrokeStartPosition;
            }
            sabotage.Ball.transform.position = sabotage.StrokeStartPosition;
            Log?.LogInfo($"Rescued sabotaged ball {sabotage.BallNetId} from y={fallPosition.y:F1} → {sabotage.StrokeStartPosition} (fell {sabotage.StrokeStartPosition.y - fallPosition.y:F1}m).");
        }

        private static void EnsureCubeApplied(GolfBall ball)
        {
            if (ball == null || ball.netIdentity == null)
                return;

            uint ballNetId = ball.netId;
            if (!BallMorphs.TryGetValue(ballNetId, out RuntimeBallMorph morph) || morph == null)
            {
                morph = ball.GetComponent<RuntimeBallMorph>();
                if (morph == null)
                    morph = ball.gameObject.AddComponent<RuntimeBallMorph>();
                BallMorphs[ballNetId] = morph;
            }

            morph.ApplyCube();
        }

        private static void EnsureSphereApplied(GolfBall ball)
        {
            if (ball == null || ball.netIdentity == null)
                return;

            uint ballNetId = ball.netId;
            if (BallMorphs.TryGetValue(ballNetId, out RuntimeBallMorph morph) && morph != null)
                morph.RestoreSphere();
            BallMorphs.Remove(ballNetId);
        }

        [HarmonyPatch(typeof(ItemCollection), "Initialize")]
        private static class Patch_ItemCollection_Initialize
        {
            private static void Postfix(ItemCollection __instance) => EnsureCustomItemRegistered(__instance);
        }

        [HarmonyPatch(typeof(ItemCollection), "OnEnable")]
        private static class Patch_ItemCollection_OnEnable
        {
            private static void Postfix(ItemCollection __instance) => EnsureCustomItemRegistered(__instance);
        }

        [HarmonyPatch(typeof(ItemCollection), "get_Count")]
        private static class Patch_ItemCollection_Count
        {
            private static void Postfix(ref int __result)
            {
                if (customItemData != null)
                    __result += 1;
            }
        }

        [HarmonyPatch(typeof(ItemCollection), nameof(ItemCollection.GetItemAtIndex))]
        private static class Patch_ItemCollection_GetItemAtIndex
        {
            private static bool Prefix(ItemCollection __instance, int index, ref ItemData __result)
            {
                if (customItemData == null || itemCollectionItemsField == null)
                    return true;

                ItemData[] items = itemCollectionItemsField.GetValue(__instance) as ItemData[];
                if (items == null)
                    return true;

                if (index == items.Length)
                {
                    __result = customItemData;
                    return false;
                }

                return true;
            }
        }

        private static void EnsurePoolInjected(ItemPool pool, float targetPercent)
        {
            try
            {
            if (pool == null || Instance == null || customItemData == null)
                return;
            if (itemPoolSpawnChancesField == null || itemSpawnChanceType == null ||
                itemSpawnChanceItemField == null || itemSpawnChanceWeightField == null)
                return;
            if (injectedPools.Contains(pool))
                return;

            if (!(itemPoolSpawnChancesField.GetValue(pool) is Array existing))
                return;

            for (int i = 0; i < existing.Length; i++)
            {
                object entry = existing.GetValue(i);
                ItemType entryType = (ItemType)itemSpawnChanceItemField.GetValue(entry);
                if (entryType == CustomItemType)
                {
                    injectedPools.Add(pool);
                    return;
                }
            }

            // Convert target percent → weight relative to this pool's existing total.
            // weight / (existingTotal + weight) = p/100 → weight = p/100 * existingTotal / (1 - p/100).
            // For p = 0 we inject weight 0 so the item shows in the UI at 0% but never rolls.
            float clamped = Mathf.Clamp(targetPercent, 0f, 99.99f);
            float p = clamped / 100f;
            float existingTotal = pool.TotalSpawnChanceWeight;
            float weight = (p <= 0f) ? 0f : (p * existingTotal) / (1f - p);

            object newEntry = Activator.CreateInstance(itemSpawnChanceType);
            itemSpawnChanceItemField.SetValue(newEntry, CustomItemType);
            itemSpawnChanceWeightField.SetValue(newEntry, weight);

            Array grown = Array.CreateInstance(itemSpawnChanceType, existing.Length + 1);
            Array.Copy(existing, grown, existing.Length);
            grown.SetValue(newEntry, existing.Length);
            itemPoolSpawnChancesField.SetValue(pool, grown);

            pool.UpdateTotalWeight();
            injectedPools.Add(pool);
            Log?.LogInfo($"Injected Ball Saboteur into item pool '{pool.name}' at weight {weight:F3} (target {clamped:F1}%).");
            }
            catch (Exception ex)
            {
                Log?.LogError($"EnsurePoolInjected failed for pool '{pool?.name}': {ex}");
            }
        }

        private static bool localizationEntryRegistered;

        internal static void TryRegisterCustomLocalizationEntry()
        {
            if (localizationEntryRegistered)
                return;
            try
            {
                LocalizedStringDatabase db = LocalizationSettings.StringDatabase;
                if (db == null)
                    return;
                UnityEngine.Localization.Tables.StringTable table = db.GetTable("Data");
                if (table == null)
                    return;
                string key = "ITEM_" + CustomItemTypeRaw;
                if (table.GetEntryFromReference(key) == null)
                    table.AddEntry(key, CustomItemDisplayName);
                localizationEntryRegistered = true;
                Log?.LogInfo($"Registered localization entry 'Data/{key}' = '{CustomItemDisplayName}'.");
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"Localization registration failed (name will show as fallback): {ex.Message}");
                localizationEntryRegistered = true; // don't retry every call
            }
        }

        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.TryUseItem))]
        private static class Patch_PlayerInventory_TryUseItem
        {
            private static bool Prefix(PlayerInventory __instance, bool isAirhornReaction, ref bool shouldEatInput, ref bool __result)
            {
                if (isAirhornReaction)
                    return true;

                InventorySlot slot = InvokeInventoryGetEffectiveSlot(__instance, __instance.EquippedItemIndex);
                if (slot.itemType != CustomItemType)
                    return true;

                __result = TryUseCustomItem(__instance, ref shouldEatInput);
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.OnBUpdate))]
        private static class Patch_PlayerInventory_OnBUpdate
        {
            private static void Postfix(PlayerInventory __instance)
            {
                UpdateCustomLockOnTargeting(__instance);
            }
        }

        [HarmonyPatch(typeof(PlayerInventory), "UserCode_CmdActivateOrbitalLaser__Hittable__Vector3__ItemUseId")]
        private static class Patch_PlayerInventory_CmdActivateOrbitalLaser
        {
            private static bool Prefix(ItemUseId itemUseId, Hittable target)
            {
                return !TryApplyCustomSabotageOnServer(itemUseId, target);
            }
        }

        [HarmonyPatch(typeof(PlayerGolfer), "OnPlayerHitOwnBall")]
        private static class Patch_PlayerGolfer_OnPlayerHitOwnBall
        {
            private static void Postfix(PlayerGolfer __instance)
            {
                MarkShotStarted(__instance);
            }
        }

        [HarmonyPatch(typeof(NetworkClient), "RegisterMessageHandlers")]
        private static class Patch_NetworkClient_RegisterMessageHandlers
        {
            private static void Postfix() => RegisterNetworkHandlers();
        }

        [HarmonyPatch(typeof(LocalizedString), nameof(LocalizedString.GetLocalizedString), new Type[0])]
        private static class Patch_LocalizedString_GetLocalizedString
        {
            private static void Postfix(ref string __result)
            {
                try
                {
                    if (__result == CustomItemLocalizationFallback)
                        __result = CustomItemDisplayName;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(ItemSpawnerSettings), "ResetRuntimeData")]
        private static class Patch_ItemSpawnerSettings_ResetRuntimeData
        {
            private static void Postfix(ItemSpawnerSettings __instance)
            {
                try
                {
                    // Ensure our custom item is registered before injecting into pools so the UI
                    // can resolve the ItemType when it iterates the pool's SpawnChances.
                    if (customItemData == null)
                    {
                        ItemCollection collection = GameManager.AllItems;
                        if (collection != null)
                            EnsureCustomItemRegistered(collection);
                    }
                    if (customItemData == null)
                        return;

                    TryRegisterCustomLocalizationEntry();

                    float mainPercent = Instance.spawnChancePercentConfig.Value;
                    float leaderPercent = Instance.leaderSpawnChancePercentConfig.Value;

                    if (__instance.AheadOfBallItemPool != null)
                        EnsurePoolInjected(__instance.AheadOfBallItemPool, mainPercent);
                    for (int i = 0; i < __instance.ItemPools.Count; i++)
                    {
                        ItemSpawnerSettings.ItemPoolData data = __instance.ItemPools[i];
                        if (data.pool == null)
                            continue;
                        // The first entry in ItemPools is the leader pool (minDistanceBehindLeader = 0).
                        float percent = (i == 0) ? leaderPercent : mainPercent;
                        EnsurePoolInjected(data.pool, percent);
                    }
                }
                catch (Exception ex)
                {
                    Log?.LogError($"ResetRuntimeData postfix failed: {ex}");
                }
            }
        }

        // MatchSetupRules.SpawnChanceUpdated(ItemPoolId) indexes itemOrderLookup[itemType - 1]
        // which is sized to the vanilla item count. Bail early for our item — it has no slider
        // to update in the match-setup UI anyway.
        private static bool TryGetMatchSetupSliderIndex(MatchSetupRules rules, ItemType itemType, out int sliderIndex)
        {
            sliderIndex = -1;
            if (rules == null || matchSetupRulesItemOrderLookupField == null || matchSetupRulesSpawnChanceSlidersField == null)
                return false;

            int rawItemIndex = (int)itemType - 1;
            if (rawItemIndex < 0)
                return false;

            int[] itemOrderLookup = matchSetupRulesItemOrderLookupField.GetValue(rules) as int[];
            if (itemOrderLookup == null || rawItemIndex >= itemOrderLookup.Length)
                return false;

            List<SliderOption> spawnChanceSliders = matchSetupRulesSpawnChanceSlidersField.GetValue(rules) as List<SliderOption>;
            if (spawnChanceSliders == null)
                return false;

            sliderIndex = itemOrderLookup[rawItemIndex];
            return sliderIndex >= 0 && sliderIndex < spawnChanceSliders.Count;
        }

        private static bool CurrentMatchSetupPoolContainsUiUnsupportedItem(MatchSetupRules rules)
        {
            if (rules == null || matchSetupRulesGetCurrentItemPoolMethod == null)
                return false;

            ItemPool pool = matchSetupRulesGetCurrentItemPoolMethod.Invoke(rules, null) as ItemPool;
            if (pool == null)
                return false;

            ItemPool.ItemSpawnChance[] spawnChances = pool.SpawnChances;
            for (int i = 0; i < spawnChances.Length; i++)
            {
                if (!TryGetMatchSetupSliderIndex(rules, spawnChances[i].item, out _))
                    return true;
            }

            return false;
        }

        private static bool HandleUnsupportedMatchSetupSpawnChanceUpdated(MatchSetupRules rules, MatchSetupRules.ItemPoolId itemPoolId)
        {
            if (rules == null)
                return false;
            if (TryGetMatchSetupSliderIndex(rules, itemPoolId.itemType, out _))
                return false;

            if (rules.isServer && matchSetupRulesServerUpdateSpawnChanceValueMethod != null)
                matchSetupRulesServerUpdateSpawnChanceValueMethod.Invoke(rules, new object[] { itemPoolId });

            int currentItemPoolIndex = matchSetupRulesCurrentItemPoolIndexField != null
                ? (int)matchSetupRulesCurrentItemPoolIndexField.GetValue(rules)
                : -1;
            if (currentItemPoolIndex == itemPoolId.itemPoolIndex && matchSetupRulesCurrentItemPoolDirtyField != null)
                matchSetupRulesCurrentItemPoolDirtyField.SetValue(rules, true);

            if (matchSetupRulesUpdateTotalWeightForPoolMethod != null)
                matchSetupRulesUpdateTotalWeightForPoolMethod.Invoke(rules, new object[] { itemPoolId.itemPoolIndex });

            if (PauseMenu.IsPaused && SingletonBehaviour<PauseMenu>.HasInstance)
                SingletonBehaviour<PauseMenu>.Instance.UpdateItemProbabilites();

            return true;
        }

        private static bool RunSafeMatchSetupUpdate(MatchSetupRules rules)
        {
            if (rules == null)
                return false;
            if (!MatchSetupMenu.IsActive || matchSetupRulesCurrentItemPoolDirtyField == null)
                return false;

            bool currentItemPoolDirty = (bool)matchSetupRulesCurrentItemPoolDirtyField.GetValue(rules);
            if (!currentItemPoolDirty)
                return false;

            if (matchSetupRulesCurrentItemPoolIndexField == null ||
                matchSetupRulesTotalWeightPerPoolField == null ||
                matchSetupRulesSpawnChanceWeightsField == null ||
                matchSetupRulesSpawnChanceSlidersField == null ||
                matchSetupRulesGetCurrentItemPoolMethod == null ||
                matchSetupRulesUpdateSliderGreyedOutMethod == null)
                return false;

            int currentItemPoolIndex = (int)matchSetupRulesCurrentItemPoolIndexField.GetValue(rules);
            Dictionary<int, float> totalWeightPerPool = matchSetupRulesTotalWeightPerPoolField.GetValue(rules) as Dictionary<int, float>;
            IDictionary<MatchSetupRules.ItemPoolId, float> spawnChanceWeights =
                matchSetupRulesSpawnChanceWeightsField.GetValue(rules) as IDictionary<MatchSetupRules.ItemPoolId, float>;
            List<SliderOption> spawnChanceSliders = matchSetupRulesSpawnChanceSlidersField.GetValue(rules) as List<SliderOption>;
            ItemPool currentItemPool = matchSetupRulesGetCurrentItemPoolMethod.Invoke(rules, null) as ItemPool;
            if (totalWeightPerPool == null || spawnChanceWeights == null || spawnChanceSliders == null || currentItemPool == null)
                return false;

            totalWeightPerPool.TryGetValue(currentItemPoolIndex, out float totalWeight);

            foreach (SliderOption spawnChanceSlider in spawnChanceSliders)
            {
                if (!spawnChanceSlider.Slider.interactable)
                {
                    spawnChanceSlider.SetValueText($"{0:0.#}%");
                    matchSetupRulesUpdateSliderGreyedOutMethod.Invoke(rules, new object[] { spawnChanceSlider });
                }
            }

            ItemPool.ItemSpawnChance[] spawnChances = currentItemPool.SpawnChances;
            for (int i = 0; i < spawnChances.Length; i++)
            {
                ItemPool.ItemSpawnChance itemSpawnChance = spawnChances[i];
                if (!TryGetMatchSetupSliderIndex(rules, itemSpawnChance.item, out int sliderIndex))
                    continue;

                SliderOption sliderOption = spawnChanceSliders[sliderIndex];
                spawnChanceWeights.TryGetValue(MatchSetupRules.ItemPoolId.Get(currentItemPoolIndex, itemSpawnChance.item), out float weight);
                float normalizedWeight = totalWeight > float.Epsilon ? weight / totalWeight : 0f;
                sliderOption.SetValueText($"{normalizedWeight * 100f:0.#}%");
                matchSetupRulesUpdateSliderGreyedOutMethod.Invoke(rules, new object[] { sliderOption });
            }

            matchSetupRulesCurrentItemPoolDirtyField.SetValue(rules, false);
            return true;
        }

        [HarmonyPatch(typeof(MatchSetupRules), "SpawnChanceUpdated", new Type[] { typeof(MatchSetupRules.ItemPoolId) })]
        private static class Patch_MatchSetupRules_SpawnChanceUpdated
        {
            private static bool Prefix(MatchSetupRules __instance, MatchSetupRules.ItemPoolId itemPoolId)
            {
                return !HandleUnsupportedMatchSetupSpawnChanceUpdated(__instance, itemPoolId);
            }
        }

        [HarmonyPatch(typeof(MatchSetupRules), "Update")]
        private static class Patch_MatchSetupRules_Update
        {
            private static bool Prefix(MatchSetupRules __instance)
            {
                if (!CurrentMatchSetupPoolContainsUiUnsupportedItem(__instance))
                    return true;

                return !RunSafeMatchSetupUpdate(__instance);
            }
        }

        private struct BallSabotageStateMessage : NetworkMessage
        {
            public uint BallNetId;
            public bool IsActive;
        }

        private sealed class ActiveSabotage
        {
            public uint BallNetId;
            public GolfBall Ball;
            public PlayerGolfer Owner;
            public Vector3 StrokeStartPosition;
            public bool WaitingForBallToStop;
        }

        private sealed class RuntimeBallMorph : MonoBehaviour
        {
            private const float CornerRadiusFraction = 0.25f;

            private GolfBall ball;
            private SphereCollider sphereCollider;
            private BoxCollider cubeCollider;
            private SphereCollider[] cornerColliders;
            private GameObject cubeVisual;
            private Renderer[] originalRenderers;
            private Rigidbody ballRigidbody;
            private CollisionDetectionMode originalCollisionMode;
            private bool hasStoredCollisionMode;
            private float originalMass;
            private bool hasStoredMass;

            private void Awake()
            {
                ball = GetComponent<GolfBall>();
                sphereCollider = ball != null ? ball.Collider : GetComponent<SphereCollider>();
                ballRigidbody = GetComponent<Rigidbody>();
                originalRenderers = ballRenderersField?.GetValue(ball) as Renderer[];
                if (originalRenderers == null || originalRenderers.Length == 0)
                    originalRenderers = GetComponentsInChildren<Renderer>(true);
            }

            public void ApplyCube()
            {
                if (sphereCollider == null)
                    sphereCollider = GetComponent<SphereCollider>();

                float diameter = sphereCollider != null ? sphereCollider.radius * 2f : 0.45f;
                float halfSize = diameter * 0.5f;
                Vector3 boxCenter = sphereCollider != null ? sphereCollider.center : Vector3.zero;

                if (cubeCollider == null)
                {
                    cubeCollider = GetComponent<BoxCollider>();
                    if (cubeCollider == null)
                        cubeCollider = gameObject.AddComponent<BoxCollider>();
                }
                cubeCollider.center = boxCenter;
                cubeCollider.size = Vector3.one * diameter;
                if (sphereCollider != null)
                    cubeCollider.sharedMaterial = sphereCollider.sharedMaterial;
                cubeCollider.enabled = true;

                // Rounded-cube compound: 8 corner spheres. Radius r, inset d = r/√3 from each face,
                // so the cube apex sits exactly on the sphere surface — no sharp point to wedge into
                // terrain seams. Spheres protrude ~r·(1 − 1/√3) past cube faces, so the ball rests
                // on its corner spheres rather than flat cube faces.
                float cornerRadius = halfSize * CornerRadiusFraction;
                float cornerOffset = halfSize - (cornerRadius / Mathf.Sqrt(3f));

                if (cornerColliders == null)
                    cornerColliders = new SphereCollider[8];

                int idx = 0;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    SphereCollider corner = cornerColliders[idx];
                    if (corner == null)
                    {
                        corner = gameObject.AddComponent<SphereCollider>();
                        cornerColliders[idx] = corner;
                    }
                    corner.center = boxCenter + new Vector3(sx * cornerOffset, sy * cornerOffset, sz * cornerOffset);
                    corner.radius = cornerRadius;
                    if (sphereCollider != null)
                        corner.sharedMaterial = sphereCollider.sharedMaterial;
                    corner.enabled = true;
                    idx++;
                }

                if (sphereCollider != null)
                    sphereCollider.enabled = false;

                if (ballRigidbody != null)
                {
                    if (!hasStoredCollisionMode)
                    {
                        originalCollisionMode = ballRigidbody.collisionDetectionMode;
                        hasStoredCollisionMode = true;
                    }
                    ballRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                    if (!hasStoredMass)
                    {
                        originalMass = ballRigidbody.mass;
                        hasStoredMass = true;
                    }
                    float multiplier = Instance != null ? Instance.cubeMassMultiplierConfig.Value : 1f;
                    ballRigidbody.mass = originalMass * Mathf.Max(multiplier, 0.01f);
                }

                if (cubeVisual == null)
                {
                    cubeVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cubeVisual.name = "BallSaboteurCubeVisual";
                    cubeVisual.transform.SetParent(transform, false);
                    Collider visualCollider = cubeVisual.GetComponent<Collider>();
                    if (visualCollider != null)
                        Destroy(visualCollider);
                }

                cubeVisual.transform.localPosition = boxCenter;
                cubeVisual.transform.localRotation = Quaternion.identity;
                cubeVisual.transform.localScale = Vector3.one * diameter;
                cubeVisual.SetActive(true);

                if (originalRenderers != null)
                {
                    foreach (Renderer renderer in originalRenderers)
                    {
                        if (renderer != null)
                            renderer.enabled = false;
                    }
                }
            }

            public void RestoreSphere()
            {
                if (sphereCollider != null)
                    sphereCollider.enabled = true;

                if (cubeCollider != null)
                    cubeCollider.enabled = false;

                if (cornerColliders != null)
                {
                    foreach (SphereCollider corner in cornerColliders)
                    {
                        if (corner != null)
                            corner.enabled = false;
                    }
                }

                if (ballRigidbody != null && hasStoredCollisionMode)
                {
                    ballRigidbody.collisionDetectionMode = originalCollisionMode;
                    hasStoredCollisionMode = false;
                }

                if (ballRigidbody != null && hasStoredMass)
                {
                    ballRigidbody.mass = originalMass;
                    hasStoredMass = false;
                }

                if (cubeVisual != null)
                    cubeVisual.SetActive(false);

                if (originalRenderers != null)
                {
                    foreach (Renderer renderer in originalRenderers)
                    {
                        if (renderer != null)
                            renderer.enabled = true;
                    }
                }
            }
        }
    }
}
