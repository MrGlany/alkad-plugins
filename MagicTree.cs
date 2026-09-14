using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MagicTree", "MrCodder", "2.3.3")]
    [Description("Magic tree seeds, staged growth and loot crates that fall when the mature tree is destroyed.")]
    public class MagicTree : RustPlugin
    {
        private PluginConfig _config;
        private StoredData _data;

        // Runtime-only: crates are recreated after plugin/server reload.
        private readonly Dictionary<ulong, List<BaseEntity>> _boxesByTree = new Dictionary<ulong, List<BaseEntity>>();
        private readonly HashSet<ulong> _lockedBoxIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _internalKills = new HashSet<ulong>();
        private readonly HashSet<ulong> _processingSeeds = new HashSet<ulong>();

        private class FallingBoxState
        {
            public BaseEntity Entity;
            public float Velocity;
            public float StartedAt;
        }

        private readonly Dictionary<ulong, FallingBoxState> _fallingBoxes =
            new Dictionary<ulong, FallingBoxState>();

        #region Configuration

        private class SeedSettings
        {
            [JsonProperty("Shortname предмета")]
            public string Shortname = "seed.hemp";

            [JsonProperty("Название")]
            public string Name = "Семена магического дерева";

            [JsonProperty("Skin ID")]
            public ulong SkinId = 1787823357;
        }

        private class LootEntry
        {
            [JsonProperty("Shortname предмета")]
            public string Shortname;

            [JsonProperty("Минимальное количество")]
            public int MinAmount = 1;

            [JsonProperty("Максимальное количество")]
            public int MaxAmount = 1;

            [JsonProperty("Шанс попадания в ящик, %")]
            public float Chance = 100f;

            [JsonProperty("Skin ID")]
            public ulong SkinId = 0;

            [JsonProperty("Кастомное имя")]
            public string CustomName = "";

            [JsonProperty("Выдавать как чертеж")]
            public bool Blueprint = false;
        }

        private class PluginConfig
        {
            [JsonProperty("Шанс выпадения семечки с обычного дерева, %")]
            public float SeedChance = 5f;

            [JsonProperty("Общее время роста дерева, секунд")]
            public float TotalGrowthSeconds = 600f;

            [JsonProperty("Время существования зрелого дерева, секунд (0 = бесконечно)")]
            public float MatureLifetimeSeconds = 3600f;

            [JsonProperty("Запрещать посадку в грядках")]
            public bool DisallowPlanters = true;

            [JsonProperty("Запрещать посадку в зоне чужого шкафа")]
            public bool RequireBuildingAuth = true;

            [JsonProperty("Защищать дерево от урона пока оно растет")]
            public bool ProtectWhileGrowing = true;

            [JsonProperty("Показывать текст прогресса над магическим деревом")]
            public bool ShowGrowthInfo = true;

            [JsonProperty("Радиус показа текста прогресса")]
            public float InfoRadius = 7f;

            [JsonProperty("Высота текста над основанием дерева")]
            public float InfoHeight = 2.2f;

            [JsonProperty("Множитель дерева при финальной добыче")]
            public float WoodGatherMultiplier = 1f;

            [JsonProperty("Количество ящиков на зрелом дереве")]
            public int BoxCount = 4;

            [JsonProperty("Количество разных предметов в одном ящике")]
            public int ItemsPerBox = 3;

            [JsonProperty("Горизонтальный радиус размещения ящиков")]
            public float HorizontalRadius = 4.5f;

            [JsonProperty("Минимальная высота ящиков над основанием дерева")]
            public float MinBoxHeight = 3f;

            [JsonProperty("Максимальная высота ящиков над основанием дерева")]
            public float MaxBoxHeight = 6f;

            [JsonProperty("Префаб ящика")]
            public string CratePrefab = "assets/bundled/prefabs/radtown/crate_basic.prefab";

            [JsonProperty("Права на команду выдачи семян")]
            public string Permission = "magictree.giveseed";

            [JsonProperty("Настройка семечки")]
            public SeedSettings Seed = new SeedSettings();

            [JsonProperty("Стадии роста дерева (по порядку)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Stages = new List<string>
            {
                "assets/bundled/prefabs/autospawn/resource/v3_temp_field/birch_tiny_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_field/birch_small_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_forest/birch_medium_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_forest/birch_large_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_forest_pine/douglas_fir_c.prefab"
            };

            [JsonProperty("Лут в ящиках", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootEntry> Loot = new List<LootEntry>
            {
                new LootEntry { Shortname = "scrap", MinAmount = 25, MaxAmount = 75, Chance = 100f },
                new LootEntry { Shortname = "metal.refined", MinAmount = 5, MaxAmount = 20, Chance = 75f },
                new LootEntry { Shortname = "techparts", MinAmount = 1, MaxAmount = 3, Chance = 45f },
                new LootEntry { Shortname = "gears", MinAmount = 1, MaxAmount = 4, Chance = 55f },
                new LootEntry { Shortname = "smgbody", MinAmount = 1, MaxAmount = 2, Chance = 25f },
                new LootEntry { Shortname = "riflebody", MinAmount = 1, MaxAmount = 2, Chance = 18f },
                new LootEntry { Shortname = "explosives", MinAmount = 1, MaxAmount = 2, Chance = 8f }
            };
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config is null");
            }
            catch (Exception ex)
            {
                PrintWarning("Не удалось прочитать MagicTree.json, создаю новый: " + ex.Message);
                _config = new PluginConfig();
            }

            NormalizeConfig();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void NormalizeConfig()
        {
            _config.SeedChance = Mathf.Clamp(_config.SeedChance, 0f, 100f);
            _config.TotalGrowthSeconds = Mathf.Max(1f, _config.TotalGrowthSeconds);
            _config.MatureLifetimeSeconds = Mathf.Max(0f, _config.MatureLifetimeSeconds);
            _config.WoodGatherMultiplier = Mathf.Max(0.01f, _config.WoodGatherMultiplier);
            _config.InfoRadius = Mathf.Max(1f, _config.InfoRadius);
            _config.InfoHeight = Mathf.Max(0.5f, _config.InfoHeight);
            _config.BoxCount = Mathf.Clamp(_config.BoxCount, 1, 20);
            _config.ItemsPerBox = Mathf.Clamp(_config.ItemsPerBox, 1, 30);
            _config.HorizontalRadius = Mathf.Max(0.5f, _config.HorizontalRadius);
            _config.MinBoxHeight = Mathf.Max(0.5f, _config.MinBoxHeight);
            _config.MaxBoxHeight = Mathf.Max(_config.MinBoxHeight, _config.MaxBoxHeight);

            // Older MagicTree builds used the underwater/freeable crate.
            // It behaves badly in the air on some Alkad builds (ghost-like/no projectile collision).
            if (string.IsNullOrEmpty(_config.CratePrefab) ||
                _config.CratePrefab.Equals(
                    "assets/bundled/prefabs/radtown/crate_underwater_basic.prefab",
                    StringComparison.OrdinalIgnoreCase))
            {
                _config.CratePrefab = "assets/bundled/prefabs/radtown/crate_basic.prefab";
            }

            if (_config.Seed == null)
                _config.Seed = new SeedSettings();

            if (_config.Stages == null || _config.Stages.Count == 0)
                _config.Stages = GetSafeDefaultStages();

            // Older releases used hemp + unrelated tree species.
            // Besides producing weird size jumps, oak_b can spawn natural beehives.
            _config.Stages.RemoveAll(x =>
                !string.IsNullOrEmpty(x) &&
                x.Equals("assets/prefabs/plants/hemp/hemp.entity.prefab", StringComparison.OrdinalIgnoreCase));

            bool hasOldMixedPreset = _config.Stages.Any(x =>
                !string.IsNullOrEmpty(x) &&
                (x.IndexOf("american_beech_e.prefab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 x.IndexOf("/oak_e.prefab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 x.IndexOf("/oak_b.prefab", StringComparison.OrdinalIgnoreCase) >= 0));

            bool hasOldBirchFinal =
                _config.Stages.Any(x =>
                    !string.IsNullOrEmpty(x) &&
                    x.IndexOf("/birch_big_temp.prefab", StringComparison.OrdinalIgnoreCase) >= 0);

            if (_config.Stages.Count == 0 || hasOldMixedPreset || hasOldBirchFinal)
                _config.Stages = GetSafeDefaultStages();

            // Json.NET used to append the saved stage list to the field initializer
            // on every reload. That could turn 5 stages into 10, 15, 20 ... 40.
            // It also made the visual growth repeat from tiny to big over and over.
            var safeStages = GetSafeDefaultStages();
            bool onlySafeStages = _config.Stages.All(x =>
                safeStages.Any(s => string.Equals(s, x, StringComparison.OrdinalIgnoreCase)));

            bool hasDuplicateStages =
                _config.Stages.Count != _config.Stages
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

            if (onlySafeStages && (_config.Stages.Count != safeStages.Count || hasDuplicateStages))
            {
                PrintWarning("MagicTree: repaired duplicated growth stages in config.");
                _config.Stages = safeStages;
            }

            if (_config.Loot == null)
                _config.Loot = new List<LootEntry>();

            // Repair duplicated loot rows caused by the same collection-append behavior.
            _config.Loot = _config.Loot
                .Where(x => x != null && !string.IsNullOrEmpty(x.Shortname))
                .GroupBy(
                    x => $"{x.Shortname}|{x.SkinId}|{x.Blueprint}|{x.CustomName}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static List<string> GetSafeDefaultStages()
        {
            return new List<string>
            {
                "assets/bundled/prefabs/autospawn/resource/v3_temp_field/birch_tiny_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_field/birch_small_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_forest/birch_medium_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_forest/birch_large_temp.prefab",
                "assets/bundled/prefabs/autospawn/resource/v3_temp_forest_pine/douglas_fir_c.prefab"
            };
        }

        #endregion

        #region Data

        private class TreeData
        {
            public ulong EntityId;
            public ulong OwnerId;
            public int StageIndex;
            public double NextStageUnix;
            public bool Mature;
            public double MatureUnix;
            public Vector3 Position;
        }

        private class StoredData
        {
            public List<TreeData> Trees = new List<TreeData>();
        }

        private const string DataFileName = "MagicTree_Modern";

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName);
            }
            catch
            {
                _data = null;
            }

            if (_data == null)
                _data = new StoredData();

            if (_data.Trees == null)
                _data.Trees = new List<TreeData>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);
        }

        private TreeData FindTree(ulong entityId)
        {
            return _data.Trees.FirstOrDefault(x => x.EntityId == entityId);
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(_config.Permission, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            var invalid = new List<TreeData>();

            foreach (var data in _data.Trees.ToList())
            {
                var entity = FindEntity(data.EntityId);
                if (entity == null || entity.IsDestroyed)
                {
                    invalid.Add(data);
                    continue;
                }

                data.Position = entity.transform.position;

                // Existing trees may have StageIndex like 23/40 from the old duplicated
                // config. Recover the real stage from the prefab that is actually spawned.
                if (!data.Mature)
                {
                    int actualStage = _config.Stages.FindIndex(x =>
                        string.Equals(x, entity.PrefabName, StringComparison.OrdinalIgnoreCase));

                    if (actualStage < 0)
                    {
                        actualStage = _config.Stages.FindIndex(x =>
                            !string.IsNullOrEmpty(x) &&
                            entity.PrefabName != null &&
                            entity.PrefabName.EndsWith(
                                x.Substring(x.LastIndexOf('/') + 1),
                                StringComparison.OrdinalIgnoreCase));
                    }

                    data.StageIndex = actualStage >= 0
                        ? actualStage
                        : Mathf.Clamp(data.StageIndex, 0, Mathf.Max(0, _config.Stages.Count - 1));
                }

                RemoveNaturalBeehives(entity);

                if (data.Mature)
                {
                    SpawnBoxes(data, entity);
                    ScheduleMatureExpiry(data);
                }
                else
                {
                    ScheduleNextStage(data);
                }
            }

            foreach (var data in invalid)
                _data.Trees.Remove(data);

            SaveData();

            // Do not rely only on OnEntityDeath/OnEntityKill. Some tree resource
            // prefabs in Alkad disappear without those hooks reaching the plugin.
            timer.Every(0.5f, CheckTrackedTrees);

            // Drive crate falling from the plugin itself. On some Alkad builds
            // Rigidbody/MonoBehaviour movement on loot prefabs is not networked.
            timer.Every(0.05f, UpdateFallingBoxes);

            if (_config.ShowGrowthInfo)
                timer.Every(1f, DrawAllTreeInfo);

            Puts("MagicTree 2.3.3 loaded. Active magic trees: " + _data.Trees.Count);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            if (gameObject == null)
                return;

            var entity = gameObject.ToBaseEntity() as GrowableEntity;
            if (!IsMagicSeedEntity(entity))
                return;

            QueueSeedProcessing(entity);
        }

        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            var entity = networkable as GrowableEntity;
            if (!IsMagicSeedEntity(entity))
                return;

            QueueSeedProcessing(entity);
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || entity.net == null)
                return null;

            // Fallback #1: some crate prefabs/builds reach this hook.
            if (TryReleaseMagicBox(entity))
                return true;

            if (!_config.ProtectWhileGrowing)
                return null;

            ulong entityId = entity.net.ID.Value;
            var data = FindTree(entityId);

            if (data != null && !data.Mature)
                return true;

            return null;
        }

        private object OnPlayerAttack(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null || info.HitEntity == null)
                return null;

            // Fallback #2: on Alkad the loot crate does not always invoke
            // OnEntityTakeDamage, but the attack hook still knows what was hit.
            var hitEntity = info.HitEntity as BaseEntity;

            if (hitEntity != null && TryReleaseMagicBox(hitEntity))
                return true;

            return null;
        }

        private object OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (dispenser == null || player == null || item == null)
                return null;

            if (dispenser.gatherType != ResourceDispenser.GatherType.Tree)
                return null;

            var tree = dispenser.GetComponentInParent<TreeEntity>();
            if (tree != null && tree.net != null)
            {
                var magic = FindTree(tree.net.ID.Value);
                if (magic != null)
                {
                    if (magic.Mature && _config.WoodGatherMultiplier != 1f)
                        item.amount = Mathf.Max(1, Mathf.RoundToInt(item.amount * _config.WoodGatherMultiplier));

                    // A magic tree should not farm more magic seeds.
                    return null;
                }
            }

            if (UnityEngine.Random.Range(0f, 100f) < _config.SeedChance)
                GiveSeed(player, 1, true);

            return null;
        }

        private object OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (dispenser == null || player == null || item == null)
                return null;

            if (dispenser.gatherType != ResourceDispenser.GatherType.Tree)
                return null;

            var tree = dispenser.GetComponentInParent<TreeEntity>();
            if (tree == null || tree.net == null)
                return null;

            var magic = FindTree(tree.net.ID.Value);
            if (magic != null && magic.Mature && _config.WoodGatherMultiplier != 1f)
                item.amount = Mathf.Max(1, Mathf.RoundToInt(item.amount * _config.WoodGatherMultiplier));

            return null;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (container == null || container.net == null)
                return null;

            if (!IsHangingMagicBox(container))
                return null;

            if (player != null)
                SendReply(player, "Сбейте ящик оружием или срубите магическое дерево.");

            return false;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || entity.net == null)
                return;

            HandleTreeDestroyed(entity.net.ID.Value);
        }

        private object OnEntityKill(BaseNetworkable networkable)
        {
            if (networkable == null || networkable.net == null)
                return null;

            ulong id = networkable.net.ID.Value;

            // Internal Kill() is used when one growth stage is replaced by the next one.
            if (_internalKills.Remove(id))
                return null;

            HandleTreeDestroyed(id);
            return null;
        }

        private void HandleTreeDestroyed(ulong id)
        {
            // If this is a stage transition, OnEntityDeath may arrive before OnEntityKill.
            if (_internalKills.Contains(id))
                return;

            var data = FindTree(id);
            if (data == null)
                return;

            if (data.Mature)
                DropBoxes(id);
            else
                KillBoxes(id);

            _data.Trees.Remove(data);
            SaveData();
        }

        private void CheckTrackedTrees()
        {
            if (_data == null || _data.Trees == null || _data.Trees.Count == 0)
                return;

            bool changed = false;

            foreach (var data in _data.Trees.ToList())
            {
                if (data == null)
                    continue;

                var entity = FindEntity(data.EntityId);

                // This is the important Alkad fallback: visually chopped resource
                // trees may disappear without OnEntityDeath/OnEntityKill firing.
                if (entity == null || entity.IsDestroyed)
                {
                    if (data.Mature)
                        DropBoxes(data.EntityId);
                    else
                        KillBoxes(data.EntityId);

                    _data.Trees.Remove(data);
                    changed = true;
                }
            }

            if (changed)
                SaveData();
        }

        private void Unload()
        {
            // Attached crates are runtime-only. Remove them so reload cannot duplicate them.
            foreach (var pair in _boxesByTree.ToList())
                KillBoxes(pair.Key);

            _lockedBoxIds.Clear();
            _processingSeeds.Clear();
            _fallingBoxes.Clear();
            SaveData();
        }

        #endregion

        #region Seed Logic

        private bool IsMagicSeedEntity(GrowableEntity entity)
        {
            return entity != null &&
                   !entity.IsDestroyed &&
                   entity.skinID == _config.Seed.SkinId;
        }

        private void QueueSeedProcessing(GrowableEntity entity)
        {
            if (entity == null || entity.net == null)
                return;

            ulong id = entity.net.ID.Value;
            if (!_processingSeeds.Add(id))
                return;

            NextTick(() =>
            {
                _processingSeeds.Remove(id);

                if (entity == null || entity.IsDestroyed)
                    return;

                ProcessSeed(entity);
            });
        }

        private void ProcessSeed(GrowableEntity seedEntity)
        {
            if (!IsMagicSeedEntity(seedEntity))
                return;

            var player = BasePlayer.FindByID(seedEntity.OwnerID);

            if (_config.DisallowPlanters && seedEntity.GetPlanter() != null)
            {
                if (player != null)
                {
                    GiveSeed(player, 1, false);
                    SendReply(player, "Семена магического дерева можно сажать только в землю.");
                }
                else
                {
                    DropSeed(seedEntity.transform.position, 1);
                }

                seedEntity.Kill();
                return;
            }

            if (_config.RequireBuildingAuth && player != null)
            {
                var privilege = player.GetBuildingPrivilege();
                if (privilege != null && !privilege.IsAuthed(player))
                {
                    GiveSeed(player, 1, false);
                    SendReply(player, "Нельзя посадить магическое дерево в зоне чужого шкафа.");
                    seedEntity.Kill();
                    return;
                }
            }

            Vector3 position = seedEntity.transform.position;
            ulong ownerId = seedEntity.OwnerID;

            seedEntity.Kill();

            var first = SpawnStageEntity(0, position, ownerId);
            if (first == null)
            {
                if (player != null)
                    GiveSeed(player, 1, false);
                else
                    DropSeed(position, 1);

                return;
            }

            var data = new TreeData
            {
                EntityId = first.net.ID.Value,
                OwnerId = ownerId,
                StageIndex = 0,
                Mature = _config.Stages.Count <= 1,
                Position = first.transform.position,
                NextStageUnix = UnixNow() + GetStageDuration()
            };

            if (data.Mature)
            {
                data.MatureUnix = UnixNow();
                _data.Trees.Add(data);
                SpawnBoxes(data, first);
                ScheduleMatureExpiry(data);
            }
            else
            {
                _data.Trees.Add(data);
                ScheduleNextStage(data);
            }

            SaveData();

            if (player != null)
                SendReply(player, "Вы посадили магическое дерево. Оно начало расти.");
        }

        private void GiveSeed(BasePlayer player, int amount, bool notify)
        {
            if (player == null || amount <= 0)
                return;

            var item = CreateSeed(amount);
            if (item == null)
                return;

            if (!player.inventory.GiveItem(item))
                item.Drop(player.transform.position + Vector3.up, Vector3.zero);

            if (notify)
                SendReply(player, "Вам выпало семечко магического дерева!");
        }

        private Item CreateSeed(int amount)
        {
            var item = ItemManager.CreateByName(_config.Seed.Shortname, amount, _config.Seed.SkinId);
            if (item == null)
            {
                PrintError("Не удалось создать семечко: " + _config.Seed.Shortname);
                return null;
            }

            item.name = _config.Seed.Name;
            return item;
        }

        private void DropSeed(Vector3 position, int amount)
        {
            var item = CreateSeed(amount);
            if (item != null)
                item.Drop(position + Vector3.up * 0.25f, Vector3.zero);
        }

        #endregion

        private void RemoveNaturalBeehives(BaseEntity tree)
        {
            if (tree == null || tree.IsDestroyed)
                return;

            var children = tree.GetComponentsInChildren<BaseEntity>(true);
            if (children == null)
                return;

            foreach (var child in children)
            {
                if (child == null || child == tree || child.IsDestroyed)
                    continue;

                string shortName = child.ShortPrefabName ?? string.Empty;
                string prefabName = child.PrefabName ?? string.Empty;

                if (shortName.IndexOf("beehive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prefabName.IndexOf("/beehive/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    child.Kill();
                }
            }
        }

        #region Growth Info

        private void DrawAllTreeInfo()
        {
            if (!_config.ShowGrowthInfo || _data == null || _data.Trees == null)
                return;

            foreach (var data in _data.Trees.ToList())
            {
                if (data == null)
                    continue;

                var entity = FindEntity(data.EntityId);
                if (entity == null || entity.IsDestroyed)
                {
                    if (data.Mature)
                        DropBoxes(data.EntityId);
                    else
                        KillBoxes(data.EntityId);

                    _data.Trees.Remove(data);
                    SaveData();
                    continue;
                }

                DrawTreeInfo(entity, data);
            }
        }

        private void DrawTreeInfo(BaseEntity tree, TreeData data)
        {
            if (tree == null || data == null)
                return;

            string message;

            if (data.Mature)
            {
                message =
                    "<size=21><color=#8BC34A>МАГИЧЕСКОЕ ДЕРЕВО</color></size>\n" +
                    "<size=16>Созрело! Срубите дерево, чтобы ящики упали.</size>";
            }
            else
            {
                int totalStages = Mathf.Max(1, _config.Stages.Count);
                int currentStage = Mathf.Clamp(data.StageIndex + 1, 1, totalStages);
                float seconds = Mathf.Max(0f, (float)(data.NextStageUnix - UnixNow()));

                message =
                    "<size=21><color=#FFD54F>МАГИЧЕСКОЕ ДЕРЕВО</color></size>\n" +
                    $"<size=16>Стадия: {currentStage}/{totalStages}\n" +
                    $"До следующей стадии: {FormatTime(seconds)}</size>";
            }

            Vector3 position = tree.transform.position + Vector3.up * _config.InfoHeight;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected)
                    continue;

                if (Vector3.Distance(player.transform.position, tree.transform.position) > _config.InfoRadius)
                    continue;

                // ddraw доступен админам. На один сетевой апдейт временно выставляем
                // флаг IsAdmin, отправляем только ddraw-команду и сразу возвращаем флаг.
                bool wasAdmin = player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin);

                if (!wasAdmin)
                {
                    player.playerFlags |= BasePlayer.PlayerFlags.IsAdmin;
                    player.SendNetworkUpdateImmediate();
                }

                player.SendConsoleCommand("ddraw.text", 1.1f, Color.white, position, message);

                if (!wasAdmin)
                {
                    player.playerFlags &= ~BasePlayer.PlayerFlags.IsAdmin;
                    player.SendNetworkUpdateImmediate();
                }
            }
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 0f)
                seconds = 0f;

            var span = TimeSpan.FromSeconds(Mathf.CeilToInt(seconds));

            if (span.TotalHours >= 1d)
                return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

            return $"{span.Minutes:00}:{span.Seconds:00}";
        }

        #endregion

        #region Tree Growth

        private float GetStageDuration()
        {
            if (_config.Stages.Count <= 1)
                return 1f;

            return Mathf.Max(1f, _config.TotalGrowthSeconds / (_config.Stages.Count - 1));
        }

        private void ScheduleNextStage(TreeData data)
        {
            if (data == null || data.Mature)
                return;

            float delay = Mathf.Max(0.1f, (float)(data.NextStageUnix - UnixNow()));
            ulong expectedEntityId = data.EntityId;

            timer.Once(delay, () => AdvanceStage(expectedEntityId));
        }

        private void AdvanceStage(ulong oldEntityId)
        {
            var data = FindTree(oldEntityId);
            if (data == null || data.Mature)
                return;

            var oldEntity = FindEntity(oldEntityId);
            if (oldEntity == null || oldEntity.IsDestroyed)
            {
                _data.Trees.Remove(data);
                SaveData();
                return;
            }

            int nextStage = data.StageIndex + 1;

            if (nextStage >= _config.Stages.Count)
            {
                MakeMature(data, oldEntity);
                return;
            }

            Vector3 position = oldEntity.transform.position;
            Quaternion rotation = oldEntity.transform.rotation;

            var nextEntity = SpawnStageEntity(nextStage, position, data.OwnerId, rotation);
            if (nextEntity == null)
            {
                PrintError("Не удалось создать стадию дерева #" + nextStage + ". Рост остановлен.");
                data.NextStageUnix = UnixNow() + 30d;
                ScheduleNextStage(data);
                SaveData();
                return;
            }

            _internalKills.Add(oldEntityId);

            data.EntityId = nextEntity.net.ID.Value;
            data.StageIndex = nextStage;
            data.Position = nextEntity.transform.position;
            data.NextStageUnix = UnixNow() + GetStageDuration();

            oldEntity.Kill();

            if (nextStage >= _config.Stages.Count - 1)
                MakeMature(data, nextEntity);
            else
                ScheduleNextStage(data);

            SaveData();
        }

        private BaseEntity SpawnStageEntity(int stageIndex, Vector3 position, ulong ownerId, Quaternion? rotation = null)
        {
            if (stageIndex < 0 || stageIndex >= _config.Stages.Count)
                return null;

            string prefab = _config.Stages[stageIndex];
            if (string.IsNullOrEmpty(prefab))
                return null;

            var entity = GameManager.server.CreateEntity(prefab, position, rotation ?? Quaternion.identity);
            if (entity == null)
            {
                PrintError("Не найден/не создан префаб стадии: " + prefab);
                return null;
            }

            entity.OwnerID = ownerId;
            entity.enableSaving = true;
            entity.Spawn();

            NextTick(() =>
            {
                if (entity != null && !entity.IsDestroyed)
                    RemoveNaturalBeehives(entity);
            });

            return entity;
        }

        private void MakeMature(TreeData data, BaseEntity entity)
        {
            if (data == null || entity == null || entity.IsDestroyed)
                return;

            data.Mature = true;
            data.MatureUnix = UnixNow();
            data.NextStageUnix = 0d;
            data.Position = entity.transform.position;

            SpawnBoxes(data, entity);
            ScheduleMatureExpiry(data);
            SaveData();

            var owner = BasePlayer.FindByID(data.OwnerId);
            if (owner != null && owner.IsConnected)
                SendReply(owner, "Ваше магическое дерево созрело и дало ящики! Срубите дерево, чтобы они упали.");
        }

        private void ScheduleMatureExpiry(TreeData data)
        {
            if (data == null || !data.Mature || _config.MatureLifetimeSeconds <= 0f)
                return;

            double expireAt = data.MatureUnix + _config.MatureLifetimeSeconds;
            float delay = Mathf.Max(0.1f, (float)(expireAt - UnixNow()));
            ulong expectedEntityId = data.EntityId;

            timer.Once(delay, () =>
            {
                var current = FindTree(expectedEntityId);
                if (current == null || !current.Mature)
                    return;

                var entity = FindEntity(expectedEntityId);
                if (entity != null && !entity.IsDestroyed)
                    entity.Kill();
            });
        }

        #endregion

        #region Boxes / Loot

        private void SpawnBoxes(TreeData data, BaseEntity tree)
        {
            if (data == null || tree == null || tree.IsDestroyed)
                return;

            KillBoxes(data.EntityId);

            var boxes = new List<BaseEntity>();

            for (int i = 0; i < _config.BoxCount; i++)
            {
                float x = UnityEngine.Random.Range(-_config.HorizontalRadius, _config.HorizontalRadius);
                float y = UnityEngine.Random.Range(_config.MinBoxHeight, _config.MaxBoxHeight);
                float z = UnityEngine.Random.Range(-_config.HorizontalRadius, _config.HorizontalRadius);

                Vector3 position = tree.transform.position + new Vector3(x, y, z);
                var box = GameManager.server.CreateEntity(_config.CratePrefab, position, Quaternion.identity);

                if (box == null)
                {
                    PrintWarning("Не удалось создать ящик: " + _config.CratePrefab);
                    continue;
                }

                var lootContainer = box.GetComponent<LootContainer>();
                if (lootContainer != null)
                    lootContainer.initialLootSpawn = false;

                box.OwnerID = data.OwnerId;
                box.enableSaving = false;
                box.Spawn();

                if (box.GetComponent<MagicTreeBoxMarker>() == null)
                    box.gameObject.AddComponent<MagicTreeBoxMarker>();

                if (lootContainer != null)
                    FillLoot(lootContainer);
                else
                    PrintWarning("Префаб ящика не содержит LootContainer: " + _config.CratePrefab);

                // Keep crates as normal independent world entities.
                // Parenting loot containers to TreeEntity made them ghost-like in Alkad:
                // projectiles/satchels could pass through them.
                EnsureSolidCrate(box);

                boxes.Add(box);

                if (box.net != null)
                    _lockedBoxIds.Add(box.net.ID.Value);
            }

            _boxesByTree[data.EntityId] = boxes;
        }

        private void EnsureSolidCrate(BaseEntity box)
        {
            if (box == null || box.IsDestroyed)
                return;

            var colliders = box.GetComponentsInChildren<Collider>(true);
            bool hasSolidCollider = false;

            if (colliders != null)
            {
                foreach (var collider in colliders)
                {
                    if (collider == null)
                        continue;

                    collider.enabled = true;

                    // Loot crate collision must be physical, not trigger-only,
                    // otherwise thrown explosives and projectiles pass through it.
                    if (collider is BoxCollider || collider is MeshCollider)
                    {
                        collider.isTrigger = false;
                        hasSolidCollider = true;
                    }
                }
            }

            // Safety fallback for a custom/old prefab with no usable collider.
            if (!hasSolidCollider)
            {
                var fallback = box.gameObject.GetComponent<BoxCollider>();
                if (fallback == null)
                    fallback = box.gameObject.AddComponent<BoxCollider>();

                fallback.enabled = true;
                fallback.isTrigger = false;
                fallback.center = new Vector3(0f, 0.35f, 0f);
                fallback.size = new Vector3(1.1f, 0.7f, 0.85f);
            }
        }

        private void FillLoot(LootContainer container)
        {
            if (container == null || container.inventory == null)
                return;

            foreach (var oldItem in container.inventory.itemList.ToArray())
                oldItem.Remove();

            if (_config.Loot == null || _config.Loot.Count == 0)
                return;

            var chosen = new List<LootEntry>();
            var shuffled = _config.Loot.OrderBy(x => UnityEngine.Random.value).ToList();

            foreach (var entry in shuffled)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Shortname))
                    continue;

                if (UnityEngine.Random.Range(0f, 100f) <= Mathf.Clamp(entry.Chance, 0f, 100f))
                    chosen.Add(entry);

                if (chosen.Count >= _config.ItemsPerBox)
                    break;
            }

            // If bad luck produced too few items, fill the remaining slots from valid entries.
            if (chosen.Count < _config.ItemsPerBox)
            {
                foreach (var entry in shuffled)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Shortname) || chosen.Contains(entry))
                        continue;

                    chosen.Add(entry);
                    if (chosen.Count >= _config.ItemsPerBox)
                        break;
                }
            }

            foreach (var entry in chosen.Take(_config.ItemsPerBox))
                AddLootItem(container, entry);
        }

        private void AddLootItem(LootContainer container, LootEntry entry)
        {
            int min = Mathf.Max(1, entry.MinAmount);
            int max = Mathf.Max(min, entry.MaxAmount);
            int amount = UnityEngine.Random.Range(min, max + 1);

            Item item;

            if (entry.Blueprint)
            {
                var target = ItemManager.FindItemDefinition(entry.Shortname);
                if (target == null)
                {
                    PrintWarning("Не найден предмет для чертежа: " + entry.Shortname);
                    return;
                }

                item = ItemManager.CreateByName("blueprintbase", 1);
                if (item != null)
                    item.blueprintTarget = target.itemid;
            }
            else
            {
                item = ItemManager.CreateByName(entry.Shortname, amount, entry.SkinId);
            }

            if (item == null)
            {
                PrintWarning("Не удалось создать предмет лута: " + entry.Shortname);
                return;
            }

            if (!string.IsNullOrEmpty(entry.CustomName))
                item.name = entry.CustomName;

            if (!item.MoveToContainer(container.inventory))
                item.Remove();
        }

        private bool TryReleaseMagicBox(BaseEntity box)
        {
            if (!IsHangingMagicBox(box))
                return false;

            ReleaseSingleBox(box);
            return true;
        }

        private bool IsHangingMagicBox(BaseEntity box)
        {
            if (box == null || box.IsDestroyed || box.net == null)
                return false;

            ulong boxId = box.net.ID.Value;

            if (_fallingBoxes.ContainsKey(boxId))
                return false;

            if (_lockedBoxIds.Contains(boxId))
                return true;

            // The marker means the crate is still hanging. It is removed as soon as
            // the crate is released, so a grounded crate stays lootable.
            if (box.GetComponent<MagicTreeBoxMarker>() != null)
                return true;

            // Legacy/orphaned crates from older MagicTree builds did not have
            // a marker. They were created with enableSaving=false and the same
            // configured crate prefab. This lets bullets knock those down too.
            if (box is LootContainer && !box.enableSaving && IsMagicCratePrefab(box.PrefabName))
            {
                float groundY = TerrainMeta.HeightMap.GetHeight(box.transform.position);

                // Grounded crates can have their pivot roughly 0.5-1m above terrain.
                // Only treat clearly elevated legacy crates as "still hanging".
                if (box.transform.position.y - groundY > 2.0f)
                    return true;
            }

            return false;
        }

        private bool IsMagicCratePrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;

            return prefabName.Equals(_config.CratePrefab, StringComparison.OrdinalIgnoreCase) ||
                   prefabName.Equals(
                       "assets/bundled/prefabs/radtown/crate_basic.prefab",
                       StringComparison.OrdinalIgnoreCase) ||
                   prefabName.Equals(
                       "assets/bundled/prefabs/radtown/crate_underwater_basic.prefab",
                       StringComparison.OrdinalIgnoreCase);
        }

        private void ReleaseSingleBox(BaseEntity box)
        {
            if (box == null || box.IsDestroyed || box.net == null)
                return;

            ulong boxId = box.net.ID.Value;
            _lockedBoxIds.Remove(boxId);

            // Remove it from the tree's runtime list, so chopping the tree later
            // will only release crates that are still hanging.
            foreach (var pair in _boxesByTree.ToList())
            {
                if (pair.Value == null)
                    continue;

                pair.Value.RemoveAll(x => x == null || x.IsDestroyed || x == box);
            }

            StartBoxDrop(box);
        }

        private class MagicTreeBoxMarker : MonoBehaviour
        {
        }

        private void StartBoxDrop(BaseEntity box)
        {
            if (box == null || box.IsDestroyed || box.net == null)
                return;

            ulong id = box.net.ID.Value;

            _lockedBoxIds.Remove(id);

            // Marker means "still hanging". Remove it immediately on release,
            // otherwise CanLootEntity would keep blocking the crate after landing.
            var marker = box.GetComponent<MagicTreeBoxMarker>();
            if (marker != null)
                UnityEngine.Object.Destroy(marker);

            // New v2.3+ crates are never parented, but old ones may still be.
            if (box.GetParentEntity() != null)
                box.SetParent(null, true, true);

            if (box.transform.parent != null)
                box.transform.SetParent(null, true);

            EnsureSolidCrate(box);
            box.SendNetworkUpdateImmediate();

            // Stop the old per-entity fall component if it exists.
            var oldDrop = box.GetComponent<MagicBoxDrop>();
            if (oldDrop != null)
                UnityEngine.Object.Destroy(oldDrop);

            _fallingBoxes[id] = new FallingBoxState
            {
                Entity = box,
                Velocity = 0f,
                StartedAt = Time.realtimeSinceStartup
            };
        }

        private void UpdateFallingBoxes()
        {
            if (_fallingBoxes.Count == 0)
                return;

            const float dt = 0.05f;

            foreach (var pair in _fallingBoxes.ToList())
            {
                ulong id = pair.Key;
                var state = pair.Value;
                var box = state?.Entity;

                if (box == null || box.IsDestroyed || box.net == null)
                {
                    _fallingBoxes.Remove(id);
                    continue;
                }

                Vector3 pos = box.transform.position;
                float groundY = TerrainMeta.HeightMap.GetHeight(pos) + 0.35f;

                // Absolute fallback: no crate may remain in "falling" state forever.
                bool timedOut = Time.realtimeSinceStartup - state.StartedAt > 6f;

                if (timedOut || pos.y <= groundY + 0.06f)
                {
                    pos.y = groundY;
                    box.transform.position = pos;

                    // Double-safety: a landed crate must never remain locked.
                    _lockedBoxIds.Remove(id);

                    var marker = box.GetComponent<MagicTreeBoxMarker>();
                    if (marker != null)
                        UnityEngine.Object.Destroy(marker);

                    box.SendNetworkUpdateImmediate();
                    _fallingBoxes.Remove(id);
                    continue;
                }

                state.Velocity = Mathf.Min(state.Velocity + 9.81f * dt, 22f);
                pos.y = Mathf.Max(groundY, pos.y - state.Velocity * dt);

                box.transform.position = pos;
                box.SendNetworkUpdateImmediate();
            }
        }

        private bool ForceDropRecognizedBox(BaseEntity box)
        {
            if (box == null || box.IsDestroyed || box.net == null)
                return false;

            ulong id = box.net.ID.Value;

            // If already falling, restart the fall timer from the current position.
            if (_fallingBoxes.ContainsKey(id))
            {
                _fallingBoxes.Remove(id);
                StartBoxDrop(box);
                return true;
            }

            if (IsHangingMagicBox(box))
            {
                ReleaseSingleBox(box);
                return true;
            }

            // Recovery for old/orphaned crates from previous plugin versions.
            if (box is LootContainer &&
                !box.enableSaving &&
                IsMagicCratePrefab(box.PrefabName))
            {
                float groundY = TerrainMeta.HeightMap.GetHeight(box.transform.position);
                if (box.transform.position.y - groundY > 2.0f)
                {
                    StartBoxDrop(box);
                    return true;
                }
            }

            return false;
        }

        private void DropBoxes(ulong treeEntityId)
        {
            List<BaseEntity> boxes;
            if (!_boxesByTree.TryGetValue(treeEntityId, out boxes))
                return;

            foreach (var box in boxes.ToList())
            {
                if (box == null || box.IsDestroyed)
                    continue;

                if (box.net != null)
                    _lockedBoxIds.Remove(box.net.ID.Value);

                StartBoxDrop(box);
            }

            _boxesByTree.Remove(treeEntityId);
        }

        private void KillBoxes(ulong treeEntityId)
        {
            List<BaseEntity> boxes;
            if (!_boxesByTree.TryGetValue(treeEntityId, out boxes))
                return;

            foreach (var box in boxes.ToList())
            {
                if (box == null)
                    continue;

                if (box.net != null)
                    _lockedBoxIds.Remove(box.net.ID.Value);

                if (!box.IsDestroyed)
                    box.Kill();
            }

            _boxesByTree.Remove(treeEntityId);
        }

        private class MagicBoxDrop : MonoBehaviour
        {
            private BaseEntity _entity;
            private Rigidbody _body;
            private float _velocity;
            private const float TickRate = 0.05f;

            private void Awake()
            {
                _entity = GetComponent<BaseEntity>();
                if (_entity == null)
                {
                    Destroy(this);
                    return;
                }

                // Try normal Unity physics first.
                _body = GetComponent<Rigidbody>();
                if (_body == null)
                    _body = gameObject.AddComponent<Rigidbody>();

                if (_body != null)
                {
                    _body.isKinematic = false;
                    _body.useGravity = true;
                    _body.mass = 10f;
                    _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    _body.WakeUp();
                }

                // Some Rust loot prefabs in older/Alkad builds ignore Rigidbody gravity.
                // Therefore we also move the entity server-side as a guaranteed fallback.
                InvokeRepeating(nameof(DropTick), TickRate, TickRate);
            }

            private void DropTick()
            {
                if (_entity == null || _entity.IsDestroyed)
                {
                    Destroy(this);
                    return;
                }

                Vector3 position = _entity.transform.position;
                float terrainY = TerrainMeta.HeightMap.GetHeight(position);

                // Crate pivot is around its centre; this keeps it slightly above terrain.
                float targetY = terrainY + 0.35f;

                if (position.y <= targetY + 0.05f)
                {
                    position.y = targetY;
                    _entity.transform.position = position;

                    if (_body != null)
                    {
                        _body.velocity = Vector3.zero;
                        _body.isKinematic = true;
                    }

                    _entity.SendNetworkUpdateImmediate();
                    CancelInvoke(nameof(DropTick));
                    Destroy(this);
                    return;
                }

                _velocity = Mathf.Min(_velocity + 9.81f * TickRate, 18f);
                position.y = Mathf.Max(targetY, position.y - _velocity * TickRate);

                _entity.transform.position = position;
                _entity.SendNetworkUpdateImmediate();
            }

            private void OnDestroy()
            {
                CancelInvoke(nameof(DropTick));
            }
        }

        #endregion

        #region Commands

        [ChatCommand("seed")]
        private void CommandSeedAlias(BasePlayer player, string command, string[] args)
        {
            CommandMagicSeed(player, command, args);
        }

        [ChatCommand("magicseed")]
        private void CommandMagicSeed(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, _config.Permission))
            {
                SendReply(player, "У вас нет прав на эту команду.");
                return;
            }

            BasePlayer target = player;
            int amount = 1;

            if (args != null && args.Length == 1)
            {
                int parsed;
                if (int.TryParse(args[0], out parsed))
                {
                    amount = Mathf.Clamp(parsed, 1, 10000);
                }
                else
                {
                    target = BasePlayer.Find(args[0]);
                    if (target == null)
                    {
                        SendReply(player, "Игрок не найден.");
                        return;
                    }
                }
            }
            else if (args != null && args.Length >= 2)
            {
                target = BasePlayer.Find(args[0]);
                if (target == null)
                {
                    SendReply(player, "Игрок не найден.");
                    return;
                }

                int parsed;
                if (!int.TryParse(args[1], out parsed))
                {
                    SendReply(player, "Использование: /magicseed [количество] или /magicseed игрок количество");
                    return;
                }

                amount = Mathf.Clamp(parsed, 1, 10000);
            }

            GiveSeed(target, amount, false);
            SendReply(player, "Выдано семян магического дерева: " + amount + " -> " + target.displayName);
        }

        #endregion

        [ChatCommand("magicboxesdrop")]
        private void CommandDropMagicBoxes(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
                return;

            int dropped = DropAllHangingMagicBoxes();
            SendReply(player, "Сброшено зависших ящиков: " + dropped);
        }

        [ConsoleCommand("magicboxesdrop")]
        private void ConsoleDropMagicBoxes(ConsoleSystem.Arg arg)
        {
            // Allow server console. If invoked by a player console, require admin.
            var player = arg.Player();
            if (player != null && !player.IsAdmin)
                return;

            int dropped = DropAllHangingMagicBoxes();

            if (player != null)
                SendReply(player, "Сброшено зависших ящиков: " + dropped);
            else
                Puts("Dropped hanging magic boxes: " + dropped);
        }

        private int DropAllHangingMagicBoxes()
        {
            int dropped = 0;

            foreach (var container in UnityEngine.Object.FindObjectsOfType<LootContainer>())
            {
                if (container == null || container.IsDestroyed || container.net == null)
                    continue;

                ulong id = container.net.ID.Value;

                // Recovery for crates already dropped by older builds but still carrying
                // the stale lock/marker. If they are on/near the ground, unlock them.
                if (!container.enableSaving && IsMagicCratePrefab(container.PrefabName))
                {
                    float groundY = TerrainMeta.HeightMap.GetHeight(container.transform.position);
                    if (container.transform.position.y - groundY <= 2.0f)
                    {
                        _lockedBoxIds.Remove(id);

                        var marker = container.GetComponent<MagicTreeBoxMarker>();
                        if (marker != null)
                            UnityEngine.Object.Destroy(marker);

                        continue;
                    }
                }

                if (ForceDropRecognizedBox(container))
                    dropped++;
            }

            return dropped;
        }

        #region Helpers

        private BaseEntity FindEntity(ulong id)
        {
            if (id == 0)
                return null;

            return BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
        }

        private static double UnixNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        #endregion
    }
}
