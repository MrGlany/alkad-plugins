using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Remover Tool", "MrCodder", "5.0.3")]
    [Description("Compact hammer remover with refunds and no removal prices.")]
    public class RemoverTool : RustPlugin
    {
        private const string UiName = "CompactRemoverUI";
        private const string PermissionUse = "removertool.use";

        private const int LayerTarget =
            ~(1 << 2 | 1 << 3 | 1 << 4 | 1 << 10 | 1 << 18 | 1 << 28 | 1 << 29);

        private ConfigData _config;
        private readonly HashSet<ulong> _enabled = new HashSet<ulong>();
        private readonly Dictionary<string, string> _deployableToItem = new Dictionary<string, string>();

        #region Config

        private class ConfigData
        {
            [JsonProperty("Требовать permission removertool.use")]
            public bool RequirePermission = false;

            [JsonProperty("Расстояние удаления")]
            public float Distance = 4.5f;

            [JsonProperty("Процент возврата ресурсов")]
            public float RefundPercent = 100f;

            [JsonProperty("Возвращать установленный предмет (дверь, ящик, печь и т.д.)")]
            public bool RefundDeployables = true;

            [JsonProperty("Возвращать замки и другие предметы в слотах")]
            public bool RefundSlots = true;

            [JsonProperty("Запрещать удаление непустых контейнеров")]
            public bool BlockNonEmptyContainers = true;

            [JsonProperty("Разрешать удаление своих объектов вне зоны шкафа")]
            public bool AllowOwnEntitiesWithoutCupboard = true;

            [JsonProperty("Админ может удалять без проверки доступа")]
            public bool AdminBypass = true;

            [JsonProperty("Показывать обломки при удалении")]
            public bool Gibs = true;

            [JsonProperty("Выключать режим при смене киянки на другой предмет")]
            public bool DisableWhenHammerUnequipped = false;

            [JsonProperty("Компактный интерфейс включен")]
            public bool UiEnabled = true;

            [JsonProperty("Частота обновления интерфейса, секунд")]
            public float UiRefresh = 0.25f;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null)
                    throw new Exception("Config is null");
            }
            catch
            {
                _config = new ConfigData();
            }

            _config.Distance = Mathf.Clamp(_config.Distance, 1.5f, 10f);
            _config.RefundPercent = Mathf.Clamp(_config.RefundPercent, 0f, 100f);
            _config.UiRefresh = Mathf.Clamp(_config.UiRefresh, 0.1f, 1f);

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionUse, this);
            BuildDeployableLookup();
        }

        private void OnServerInitialized()
        {
            BuildDeployableLookup();

            timer.Every(_config.UiRefresh, UpdateAllUi);
            Puts("Compact Remover Tool loaded.");
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
                DestroyUi(player);

            _enabled.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;

            _enabled.Remove(player.userID);
            DestroyUi(player);
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (!_config.DisableWhenHammerUnequipped || player == null || !_enabled.Contains(player.userID))
                return;

            if (!IsHammer(newItem))
                Disable(player, false);
        }

        private object OnHammerHit(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null || info.HitEntity == null)
                return null;

            if (!_enabled.Contains(player.userID))
                return null;

            if (!IsHammer(player.GetActiveItem()))
                return null;

            var target = NormalizeTarget(info.HitEntity);
            if (target == null)
                return false;

            string reason;
            Dictionary<string, RefundEntry> refund;

            if (!CanRemove(player, target, out reason, out refund))
            {
                SendReply(player, reason);
                UpdateUi(player);
                return false;
            }

            GiveRefund(player, refund);

            if (!target.IsDestroyed)
                target.Kill(_config.Gibs ? BaseNetworkable.DestroyMode.Gib : BaseNetworkable.DestroyMode.None);

            UpdateUi(player);
            return false;
        }

        #endregion

        #region Commands

        [ChatCommand("remove")]
        private void CmdRemove(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (_config.RequirePermission &&
                !player.IsAdmin &&
                !permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                SendReply(player, "Нет права removertool.use.");
                return;
            }

            if (_enabled.Contains(player.userID))
            {
                Disable(player, true);
                return;
            }

            Enable(player);
        }

        private void Enable(BasePlayer player)
        {
            _enabled.Add(player.userID);

            SendReply(player, "Режим удаления включён. Возьми киянку и ударь по объекту. /remove — выключить.");

            if (_config.UiEnabled)
                UpdateUi(player);
        }

        private void Disable(BasePlayer player, bool message)
        {
            _enabled.Remove(player.userID);
            DestroyUi(player);

            if (message && player != null && player.IsConnected)
                SendReply(player, "Режим удаления выключен.");
        }

        #endregion

        #region Removal checks

        private bool CanRemove(
            BasePlayer player,
            BaseEntity target,
            out string reason,
            out Dictionary<string, RefundEntry> refund)
        {
            refund = new Dictionary<string, RefundEntry>();

            if (target == null || target.IsDestroyed)
            {
                reason = "Объект не найден.";
                return false;
            }

            if (!IsRemovable(target))
            {
                reason = "Этот объект удалить нельзя.";
                return false;
            }

            if (_config.BlockNonEmptyContainers && HasItems(target))
            {
                reason = "Сначала опустошите контейнер.";
                return false;
            }

            if (!HasAccess(player, target))
            {
                reason = "Нет доступа к этому объекту.";
                return false;
            }

            refund = GetRefund(target);
            reason = "Можно удалить.";
            return true;
        }

        private bool HasAccess(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)
                return false;

            if (_config.AdminBypass && player.IsAdmin)
                return true;

            // Own entity is always removable when this option is enabled.
            if (_config.AllowOwnEntitiesWithoutCupboard &&
                entity.OwnerID.IsSteamId() &&
                entity.OwnerID == player.userID)
                return true;

            // In a building privilege zone Rust already knows whether this player is authorized.
            if (!player.IsBuildingBlocked(entity.WorldSpaceBounds()))
            {
                var privilege = entity.GetBuildingPrivilege();

                if (privilege != null)
                    return privilege.IsAuthed(player);

                // No cupboard nearby: only the owner's object is allowed.
                return entity.OwnerID.IsSteamId() && entity.OwnerID == player.userID;
            }

            return false;
        }

        private bool IsRemovable(BaseEntity entity)
        {
            if (entity == null)
                return false;

            if (entity is BasePlayer)
                return false;

            if (entity is BuildingBlock)
                return true;

            if (_deployableToItem.ContainsKey(entity.ShortPrefabName))
                return true;

            // Some deployables expose pickup data even if their prefab was not found in the lookup.
            var combat = entity as BaseCombatEntity;
            if (combat != null && combat.pickup.itemTarget != null)
                return true;

            return false;
        }

        private bool HasItems(BaseEntity entity)
        {
            var storage = entity as StorageContainer;
            if (storage != null && storage.inventory != null && storage.inventory.itemList.Count > 0)
                return true;

            var io = entity as ContainerIOEntity;
            if (io != null && io.inventory != null && io.inventory.itemList.Count > 0)
                return true;

            return false;
        }

        #endregion

        #region Refunds

        private class RefundEntry
        {
            public string Shortname;
            public int Amount;
            public ulong Skin;

            public RefundEntry(string shortname, int amount, ulong skin = 0)
            {
                Shortname = shortname;
                Amount = amount;
                Skin = skin;
            }
        }

        private Dictionary<string, RefundEntry> GetRefund(BaseEntity entity)
        {
            var result = new Dictionary<string, RefundEntry>();

            var block = entity as BuildingBlock;
            if (block != null)
            {
                var grade = block.blockDefinition.GetGrade(block.grade, block.skinID);
                if (grade != null)
                {
                    var costs = grade.CostToBuild();
                    if (costs != null)
                    {
                        foreach (var amount in costs)
                        {
                            if (amount == null || amount.itemDef == null)
                                continue;

                            int refundAmount = Mathf.RoundToInt(amount.amount * _config.RefundPercent / 100f);
                            AddRefund(result, amount.itemDef.shortname, refundAmount, 0);
                        }
                    }
                }

                if (_config.RefundSlots)
                    AddSlotRefunds(entity, result);

                return result;
            }

            if (_config.RefundDeployables)
            {
                string shortname;

                if (!_deployableToItem.TryGetValue(entity.ShortPrefabName, out shortname))
                {
                    var combat = entity as BaseCombatEntity;
                    if (combat != null && combat.pickup.itemTarget != null)
                        shortname = combat.pickup.itemTarget.shortname;
                }

                if (!string.IsNullOrEmpty(shortname))
                    AddRefund(result, shortname, 1, entity.skinID);
            }

            if (_config.RefundSlots)
                AddSlotRefunds(entity, result);

            return result;
        }

        private void AddSlotRefunds(BaseEntity entity, Dictionary<string, RefundEntry> result)
        {
            foreach (BaseEntity.Slot slot in Enum.GetValues(typeof(BaseEntity.Slot)))
            {
                if (!entity.HasSlot(slot))
                    continue;

                var child = entity.GetSlot(slot);
                if (child == null)
                    continue;

                string shortname;
                if (_deployableToItem.TryGetValue(child.ShortPrefabName, out shortname))
                    AddRefund(result, shortname, 1, child.skinID);
            }
        }

        private static void AddRefund(
            Dictionary<string, RefundEntry> result,
            string shortname,
            int amount,
            ulong skin)
        {
            if (string.IsNullOrEmpty(shortname) || amount <= 0)
                return;

            string key = shortname + ":" + skin;

            RefundEntry current;
            if (result.TryGetValue(key, out current))
            {
                current.Amount += amount;
                return;
            }

            result[key] = new RefundEntry(shortname, amount, skin);
        }

        private void GiveRefund(BasePlayer player, Dictionary<string, RefundEntry> refund)
        {
            if (player == null || refund == null)
                return;

            foreach (var entry in refund.Values)
            {
                var definition = ItemManager.FindItemDefinition(entry.Shortname);
                if (definition == null)
                {
                    PrintWarning("Refund item not found: " + entry.Shortname);
                    continue;
                }

                var item = ItemManager.CreateByItemID(definition.itemid, entry.Amount, entry.Skin);
                if (item == null)
                    continue;

                if (!player.inventory.GiveItem(item))
                    item.Drop(player.transform.position + Vector3.up, Vector3.zero);
            }
        }

        #endregion

        #region Targeting

        private BaseEntity GetTargetEntity(BasePlayer player)
        {
            if (player == null)
                return null;

            BaseEntity target = null;
            var hitInfos = Pool.Get<List<RaycastHit>>();

            GamePhysics.TraceAll(player.eyes.HeadRay(), 0f, hitInfos, _config.Distance, LayerTarget);

            foreach (var hit in hitInfos)
            {
                var entity = hit.GetEntity();
                if (entity == null)
                    continue;

                if (target == null)
                {
                    target = entity;
                }
                else if (entity.GetParentEntity() == target)
                {
                    target = entity;
                    break;
                }
            }

            Pool.FreeUnmanaged(ref hitInfos);
            return NormalizeTarget(target);
        }

        private BaseEntity NormalizeTarget(BaseEntity entity)
        {
            if (entity == null)
                return null;

            // If the ray/hit catches a lock or child slot, operate on the parent object.
            var parent = entity.GetParentEntity();
            if (parent != null && (entity is BaseLock || !IsRemovable(entity)) && IsRemovable(parent))
                return parent;

            return entity;
        }

        private static bool IsHammer(Item item)
        {
            return item != null && item.info != null && item.info.shortname == "hammer";
        }

        #endregion

        #region Compact UI

        private void UpdateAllUi()
        {
            if (!_config.UiEnabled)
                return;

            foreach (var id in _enabled.ToArray())
            {
                var player = BasePlayer.FindByID(id);
                if (player == null || !player.IsConnected)
                {
                    _enabled.Remove(id);
                    continue;
                }

                UpdateUi(player);
            }
        }

        private void UpdateUi(BasePlayer player)
        {
            if (!_config.UiEnabled || player == null || !player.IsConnected || !_enabled.Contains(player.userID))
                return;

            var target = GetTargetEntity(player);

            string title = "<color=#E9C46A>УДАЛЕНИЕ</color>  <color=#BDBDBD>/remove — выкл.</color>";
            string targetText = "Наведитесь на объект";
            string refundText = "Возврат: —";
            string statusText = IsHammer(player.GetActiveItem())
                ? "<color=#8BC34A>Ударьте киянкой, чтобы удалить</color>"
                : "<color=#FFB74D>Возьмите киянку</color>";

            if (target != null)
            {
                targetText = GetEntityDisplayName(target);

                string reason;
                Dictionary<string, RefundEntry> refund;

                if (CanRemove(player, target, out reason, out refund))
                {
                    refundText = "Вернётся: " + FormatRefund(refund);
                    statusText = IsHammer(player.GetActiveItem())
                        ? "<color=#8BC34A>Можно удалить</color>"
                        : "<color=#FFB74D>Возьмите киянку</color>";
                }
                else
                {
                    refundText = "Вернётся: —";
                    statusText = "<color=#EF5350>" + EscapeRichText(reason) + "</color>";
                }
            }

            DrawUi(player, title, targetText, refundText, statusText);
        }

        private void DrawUi(
            BasePlayer player,
            string title,
            string target,
            string refund,
            string status)
        {
            CuiHelper.DestroyUi(player, UiName);

            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.88" },
                RectTransform =
                {
                    AnchorMin = "0 0.42",
                    AnchorMax = "0 0.42",
                    OffsetMin = "20 -67",
                    OffsetMax = "345 67"
                },
                CursorEnabled = false
            }, "Hud", UiName);

            AddLabel(container, UiName, title, 14, "0.04 0.73", "0.96 0.96", TextAnchor.MiddleLeft);
            AddLabel(container, UiName, "<color=#FFFFFF>" + EscapeRichText(target) + "</color>", 14, "0.04 0.49", "0.96 0.73", TextAnchor.MiddleLeft);
            AddLabel(container, UiName, refund, 13, "0.04 0.22", "0.96 0.49", TextAnchor.MiddleLeft);
            AddLabel(container, UiName, status, 12, "0.04 0.03", "0.96 0.22", TextAnchor.MiddleLeft);

            CuiHelper.AddUi(player, container);
        }

        private static void AddLabel(
            CuiElementContainer container,
            string parent,
            string text,
            int size,
            string anchorMin,
            string anchorMax,
            TextAnchor align)
        {
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = text,
                    FontSize = size,
                    Align = align,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax
                }
            }, parent);
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player != null)
                CuiHelper.DestroyUi(player, UiName);
        }

        private string FormatRefund(Dictionary<string, RefundEntry> refund)
        {
            if (refund == null || refund.Count == 0)
                return "ничего";

            var parts = new List<string>();

            foreach (var entry in refund.Values.Take(5))
            {
                string name = GetItemDisplayName(entry.Shortname);
                parts.Add(name + " <color=#8BC34A>x" + entry.Amount + "</color>");
            }

            string result = string.Join("  ", parts.ToArray());

            if (refund.Count > 5)
                result += "  …";

            return result;
        }

        private string GetEntityDisplayName(BaseEntity entity)
        {
            var block = entity as BuildingBlock;
            if (block != null)
            {
                var construction = block.blockDefinition;
                if (construction != null &&
                    !string.IsNullOrEmpty(construction.info.name.english))
                    return construction.info.name.english;
            }

            string itemShortname;
            if (_deployableToItem.TryGetValue(entity.ShortPrefabName, out itemShortname))
                return GetItemDisplayName(itemShortname);

            return entity.ShortPrefabName;
        }

        private static string GetItemDisplayName(string shortname)
        {
            switch (shortname)
            {
                case "wood": return "Дерево";
                case "stones": return "Камень";
                case "metal.fragments": return "Металл";
                case "metal.refined": return "МВК";
                case "cloth": return "Ткань";
                case "scrap": return "Скрап";
            }

            var def = ItemManager.FindItemDefinition(shortname);
            if (def != null && def.displayName != null && !string.IsNullOrEmpty(def.displayName.english))
                return def.displayName.english;

            return shortname;
        }

        private static string EscapeRichText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("<", "＜").Replace(">", "＞");
        }

        #endregion

        #region Lookup

        private void BuildDeployableLookup()
        {
            _deployableToItem.Clear();

            foreach (var itemDefinition in ItemManager.GetItemDefinitions())
            {
                if (itemDefinition == null)
                    continue;

                var deployable = itemDefinition.GetComponent<ItemModDeployable>();
                if (deployable == null || deployable.entityPrefab == null)
                    continue;

                var prefab = deployable.entityPrefab.resourcePath;
                if (string.IsNullOrEmpty(prefab))
                    continue;

                string shortPrefab = Path.GetFileNameWithoutExtension(prefab);
                if (string.IsNullOrEmpty(shortPrefab))
                    continue;

                if (!_deployableToItem.ContainsKey(shortPrefab))
                    _deployableToItem.Add(shortPrefab, itemDefinition.shortname);
            }
        }

        #endregion
    }
}
