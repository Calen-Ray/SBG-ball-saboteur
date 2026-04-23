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

namespace BallSaboteur
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "sbg.ballsaboteur";
        public const string ModName = "BallSaboteur";
        public const string ModVersion = "0.1.1";

        internal const int CustomItemTypeRaw = 1001;
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

        private static ItemData customItemData;
        private static GameObject customPickupPrefab;
        private static bool networkSerializersRegistered;

        private ConfigEntry<float> restoreDistanceMetersConfig;
        private ConfigEntry<float> orbitalLaserReplacementChanceConfig;
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
            orbitalLaserReplacementChanceConfig = Config.Bind(
                "Gameplay",
                "OrbitalLaserReplacementChance",
                0.35f,
                "Chance to convert a random Orbital Laser spawn into Ball Saboteur.");
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
                    {
                        InvokeInventoryCmdAddItem(inventory, CustomItemType);
                        Log.LogInfo("Granted Ball Saboteur to local player.");
                    }
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
                StrokeStartPosition = ball.transform.position,
                WaitingForBallToStop = false
            };
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
                StrokeStartPosition = ball.transform.position,
                WaitingForBallToStop = false
            };

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

        [HarmonyPatch(typeof(ItemPool), nameof(ItemPool.GetWeightedRandomItem))]
        private static class Patch_ItemPool_GetWeightedRandomItem
        {
            private static void Postfix(ref ItemType __result)
            {
                if (Instance == null || customItemData == null)
                    return;

                if (__result == OrbitalLaserItemType && UnityEngine.Random.value <= Instance.orbitalLaserReplacementChanceConfig.Value)
                    __result = CustomItemType;
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
            private GolfBall ball;
            private SphereCollider sphereCollider;
            private BoxCollider cubeCollider;
            private GameObject cubeVisual;
            private Renderer[] originalRenderers;

            private void Awake()
            {
                ball = GetComponent<GolfBall>();
                sphereCollider = ball != null ? ball.Collider : GetComponent<SphereCollider>();
                originalRenderers = ballRenderersField?.GetValue(ball) as Renderer[];
                if (originalRenderers == null || originalRenderers.Length == 0)
                    originalRenderers = GetComponentsInChildren<Renderer>(true);
            }

            public void ApplyCube()
            {
                if (sphereCollider == null)
                    sphereCollider = GetComponent<SphereCollider>();

                if (cubeCollider == null)
                {
                    cubeCollider = GetComponent<BoxCollider>();
                    if (cubeCollider == null)
                        cubeCollider = gameObject.AddComponent<BoxCollider>();
                }

                float diameter = sphereCollider != null ? sphereCollider.radius * 2f : 0.45f;
                cubeCollider.center = sphereCollider != null ? sphereCollider.center : Vector3.zero;
                cubeCollider.size = Vector3.one * diameter;
                if (sphereCollider != null)
                {
                    cubeCollider.sharedMaterial = sphereCollider.sharedMaterial;
                    sphereCollider.enabled = false;
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

                cubeVisual.transform.localPosition = sphereCollider != null ? sphereCollider.center : Vector3.zero;
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
