using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SeedOre", "MrCodder", "2.1.1")]
    [Description("Magic ore seeds: plant a seed, grow a custom ore node, optionally auto-smelt its drops.")]
    public class SeedOre : RustPlugin
    {
        private static SeedOre Instance;

        private ConfigData _config;

        private const string SeedShortname = "seed.corn";

        #region Configuration

        private class ConfigData
        {
            [JsonProperty("Skin ID семечки")]
            public ulong SeedSkinId = 1923097247;

            [JsonProperty("Название семечки")]
            public string SeedName = "Семечко руды";

            [JsonProperty("Шанс выпадения семечки с обычной руды, %")]
            public float SeedDropChance = 10f;

            [JsonProperty("Время полного роста волшебной руды, секунд")]
            public float GrowthSeconds = 90f;

            [JsonProperty("Количество стадий роста")]
            public int GrowthStages = 4;

            [JsonProperty("Показывать текст над растущей рудой")]
            public bool ShowGrowthInfo = true;

            [JsonProperty("Радиус показа текста над рудой")]
            public float InfoRadius = 6f;

            [JsonProperty("Множитель добычи с выращенной руды")]
            public float GatherMultiplier = 1.5f;

            [JsonProperty("Автоматически переплавлять добычу")]
            public bool AutoSmelt = true;

            [JsonProperty("Разрешать посадку только в грядке")]
            public bool OnlyInPlanters = false;

            [JsonProperty("Skin ID растущей волшебной руды")]
            public ulong GrowingOreSkinId = 21382131;

            [JsonProperty("Skin ID созревшей волшебной руды")]
            public ulong MatureOreSkinId = 21382132;

            [JsonProperty("Префабы руды, которые могут вырасти")]
            public List<string> OrePrefabs = new List<string>
            {
                "assets/bundled/prefabs/autospawn/resource/ores/sulfur-ore.prefab",
                "assets/bundled/prefabs/autospawn/resource/ores/metal-ore.prefab"
            };
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
            catch (Exception ex)
            {
                PrintWarning($"Не удалось прочитать config, создаю новый: {ex.Message}");
                LoadDefaultConfig();
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
            if (_config.SeedDropChance < 0f) _config.SeedDropChance = 0f;
            if (_config.SeedDropChance > 100f) _config.SeedDropChance = 100f;
            if (_config.GrowthSeconds < 1f) _config.GrowthSeconds = 1f;
            if (_config.GrowthStages < 2) _config.GrowthStages = 2;
            if (_config.GrowthStages > 10) _config.GrowthStages = 10;
            if (_config.InfoRadius < 1f) _config.InfoRadius = 1f;
            if (_config.GatherMultiplier < 0.01f) _config.GatherMultiplier = 0.01f;

            if (_config.OrePrefabs == null || _config.OrePrefabs.Count == 0)
            {
                _config.OrePrefabs = new List<string>
                {
                    "assets/bundled/prefabs/autospawn/resource/ores/sulfur-ore.prefab",
                    "assets/bundled/prefabs/autospawn/resource/ores/metal-ore.prefab"
                };
            }
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            Instance = this;
        }

        private void OnServerInitialized()
        {
            // Reattach growth logic after plugin reload/server restart.
            // No NetworkableId storage is needed: the custom skin IDs identify our nodes.
            foreach (var ore in UnityEngine.Object.FindObjectsOfType<OreResourceEntity>())
            {
                if (ore == null || ore.IsDestroyed)
                    continue;

                if (ore.skinID == _config.GrowingOreSkinId)
                    AttachGrowthComponent(ore, false);
            }

            // If a magic seed plant survived a plugin reload, transform it as well.
            foreach (var growable in UnityEngine.Object.FindObjectsOfType<GrowableEntity>())
            {
                if (IsMagicSeedPlant(growable))
                {
                    var captured = growable;
                    NextTick(() =>
                    {
                        if (captured != null && !captured.IsDestroyed)
                            TransformSeedIntoOre(captured);
                    });
                }
            }

            Puts("SeedOre 2.1.0 loaded.");
        }

        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            var growable = networkable as GrowableEntity;
            if (IsMagicSeedPlant(growable))
            {
                NextTick(() =>
                {
                    if (growable != null && !growable.IsDestroyed)
                        TransformSeedIntoOre(growable);
                });
                return;
            }

            var ore = networkable as OreResourceEntity;
            if (ore != null && ore.skinID == _config.GrowingOreSkinId)
                AttachGrowthComponent(ore, true);
        }

        // Prevent players from mining the node while it is growing.
        // Internal damage/healing is allowed so Rust can switch the node's visual damage stages.
        private object OnEntityTakeDamage(ResourceEntity entity, HitInfo info)
        {
            if (entity == null)
                return null;

            var growth = entity.GetComponent<MagicOreGrowth>();
            if (growth != null && growth.InternalUpdate)
                return null;

            if (entity.skinID == _config.GrowingOreSkinId)
                return true;

            return null;
        }

        private object OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (dispenser == null || player == null || item == null)
                return null;

            var resource = dispenser.GetComponent<ResourceEntity>();

            if (resource != null && resource.skinID == _config.GrowingOreSkinId)
            {
                item.amount = 0;
                return true;
            }

            if (!IsMatureMagicOre(resource))
                return null;

            return ProcessMagicOreGather(player, item, false);
        }

        private object OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (dispenser == null || player == null || item == null)
                return null;

            var resource = dispenser.GetComponent<ResourceEntity>();

            if (resource != null && resource.skinID == _config.GrowingOreSkinId)
            {
                item.amount = 0;
                return true;
            }

            if (IsMatureMagicOre(resource))
                return ProcessMagicOreGather(player, item, true);

            // Normal ore node: chance to receive a magic seed.
            if (IsSeedDropResource(item.info?.shortname) &&
                UnityEngine.Random.Range(0f, 100f) < _config.SeedDropChance)
            {
                GiveSeed(player, 1, true);
            }

            return null;
        }

        private void Unload()
        {
            foreach (var component in UnityEngine.Object.FindObjectsOfType<MagicOreGrowth>())
                UnityEngine.Object.Destroy(component);

            Instance = null;
        }

        #endregion

        #region Seed / Ore Logic

        private bool IsMagicSeedPlant(GrowableEntity entity)
        {
            return entity != null &&
                   !entity.IsDestroyed &&
                   entity.skinID == _config.SeedSkinId &&
                   entity.ShortPrefabName == "corn.entity";
        }

        private bool IsMatureMagicOre(ResourceEntity entity)
        {
            return entity != null &&
                   !entity.IsDestroyed &&
                   entity.skinID == _config.MatureOreSkinId;
        }

        private bool IsSeedDropResource(string shortname)
        {
            return shortname == "stones" ||
                   shortname == "metal.ore" ||
                   shortname == "sulfur.ore";
        }

        private void TransformSeedIntoOre(GrowableEntity seedEntity)
        {
            if (!IsMagicSeedPlant(seedEntity))
                return;

            var position = seedEntity.transform.position;
            var rotation = seedEntity.transform.rotation;
            var ownerId = seedEntity.OwnerID;
            var planter = seedEntity.GetPlanter();

            if (_config.OnlyInPlanters && planter == null)
            {
                var owner = BasePlayer.FindByID(ownerId) ?? BasePlayer.FindSleeping(ownerId);

                if (owner != null && owner.IsConnected)
                {
                    GiveSeed(owner, 1, false);
                    SendReply(owner, "Семечко руды можно сажать только в грядке.");
                }
                else
                {
                    DropSeed(position, 1);
                }

                seedEntity.Kill();
                return;
            }

            if (_config.OrePrefabs == null || _config.OrePrefabs.Count == 0)
            {
                PrintError("В config не указаны префабы руды.");
                DropSeed(position, 1);
                seedEntity.Kill();
                return;
            }

            var prefab = _config.OrePrefabs[UnityEngine.Random.Range(0, _config.OrePrefabs.Count)];

            var created = GameManager.server.CreateEntity(prefab, position, rotation);
            var ore = created as OreResourceEntity;

            if (ore == null)
            {
                PrintError($"Не удалось создать OreResourceEntity из префаба: {prefab}");
                created?.Kill();
                DropSeed(position, 1);
                seedEntity.Kill();
                return;
            }

            ore.OwnerID = ownerId;
            ore.skinID = _config.GrowingOreSkinId;
            ore.enableSaving = true;
            ore.Spawn();

            seedEntity.Kill();

            AttachGrowthComponent(ore, true);

            var player = BasePlayer.FindByID(ownerId);
            if (player != null && player.IsConnected)
                SendReply(player, $"Семечко посажено. Руда пройдет {_config.GrowthStages} стадий и созреет примерно через {_config.GrowthSeconds:0} сек.");
        }

        private void AttachGrowthComponent(OreResourceEntity ore, bool initializeAsNew)
        {
            if (ore == null || ore.IsDestroyed || ore.skinID != _config.GrowingOreSkinId)
                return;

            var component = ore.GetComponent<MagicOreGrowth>();
            if (component == null)
                component = ore.gameObject.AddComponent<MagicOreGrowth>();

            component.Begin(_config.GrowthSeconds, _config.GrowthStages, initializeAsNew);
        }

        private void MatureOre(OreResourceEntity ore)
        {
            if (ore == null || ore.IsDestroyed)
                return;

            if (ore.skinID != _config.GrowingOreSkinId)
                return;

            ore.skinID = _config.MatureOreSkinId;
            ore.SendNetworkUpdateImmediate();

            var owner = BasePlayer.FindByID(ore.OwnerID);
            if (owner != null && owner.IsConnected)
                SendReply(owner, "Ваша волшебная руда созрела!");
        }

        private void DrawGrowthInfo(OreResourceEntity ore, int stage, int totalStages, float secondsToNext, bool mature)
        {
            if (!_config.ShowGrowthInfo || ore == null || ore.IsDestroyed)
                return;

            string text;
            if (mature)
            {
                text = "<size=20><color=#8BC34A>ВОЛШЕБНАЯ РУДА</color></size>\n<size=16>Созрела! Можно добывать.</size>";
            }
            else
            {
                text = $"<size=20><color=#FFD54F>ВОЛШЕБНАЯ РУДА</color></size>\n<size=16>Стадия: {stage}/{totalStages}\nДо следующей стадии: {FormatTime(secondsToNext)}</size>";
            }

            Vector3 position = ore.transform.position + Vector3.up * 1.8f;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected)
                    continue;

                if (Vector3.Distance(player.transform.position, ore.transform.position) > _config.InfoRadius)
                    continue;

                bool alreadyAdmin = player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin);

                if (!alreadyAdmin)
                {
                    player.playerFlags |= BasePlayer.PlayerFlags.IsAdmin;
                    player.SendNetworkUpdateImmediate();
                }

                player.SendConsoleCommand("ddraw.text", 1.1f, Color.white, position, text);

                if (!alreadyAdmin)
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

        private object ProcessMagicOreGather(BasePlayer player, Item item, bool isBonus)
        {
            var multiplier = Mathf.Max(0.01f, _config.GatherMultiplier);
            var multipliedAmount = Mathf.Max(1, Mathf.RoundToInt(item.amount * multiplier));

            if (!_config.AutoSmelt)
            {
                item.amount = multipliedAmount;

                // OnDispenserBonus accepts an Item as a replacement.
                return isBonus ? item : null;
            }

            var cookable = item.info?.GetComponent<ItemModCookable>();

            // Stone and any other non-cookable resource should not throw a NullReferenceException.
            if (cookable == null || cookable.becomeOnCooked == null)
            {
                item.amount = multipliedAmount;
                return isBonus ? item : null;
            }

            // amountOfBecome is float in this Rust/Oxide build, while ItemManager.Create expects int.
            var cookedAmount = Mathf.Max(
                1,
                Mathf.RoundToInt(multipliedAmount * cookable.amountOfBecome)
            );

            var cooked = ItemManager.Create(cookable.becomeOnCooked, cookedAmount);
            if (cooked == null)
            {
                item.amount = multipliedAmount;
                return isBonus ? item : null;
            }

            if (isBonus)
            {
                // Returning Item from OnDispenserBonus replaces the original bonus item.
                return cooked;
            }

            // OnDispenserGather: give the cooked item ourselves and cancel the original raw item.
            player.GiveItem(cooked, BaseEntity.GiveItemReason.ResourceHarvested);
            return true;
        }

        #endregion

        #region Commands / Items

        [ChatCommand("giveseedore")]
        private void CommandGiveSeed(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
                return;

            var amount = 1;

            if (args != null && args.Length > 0)
            {
                int parsed;
                if (!int.TryParse(args[0], out parsed) || parsed < 1)
                {
                    SendReply(player, "Использование: /giveseedore [количество]");
                    return;
                }

                amount = Mathf.Clamp(parsed, 1, 10000);
            }

            GiveSeed(player, amount, false);
            SendReply(player, $"Выдано семян руды: {amount}");
        }

        private void GiveSeed(BasePlayer player, int amount, bool notify)
        {
            if (player == null || amount <= 0)
                return;

            var seed = CreateSeed(amount);
            if (seed == null)
                return;

            if (!player.inventory.GiveItem(seed))
                seed.Drop(player.transform.position + Vector3.up, Vector3.zero);

            if (notify)
                SendReply(player, "Вам выпало волшебное семечко руды!");
        }

        private void DropSeed(Vector3 position, int amount)
        {
            var seed = CreateSeed(amount);
            if (seed != null)
                seed.Drop(position + Vector3.up * 0.25f, Vector3.zero);
        }

        private Item CreateSeed(int amount)
        {
            var item = ItemManager.CreateByName(SeedShortname, amount, _config.SeedSkinId);
            if (item == null)
            {
                PrintError($"Не удалось создать предмет {SeedShortname}");
                return null;
            }

            item.name = _config.SeedName;
            return item;
        }

        #endregion

        #region Growth Component

        private class MagicOreGrowth : MonoBehaviour
        {
            private OreResourceEntity _ore;
            private bool _started;
            private bool _mature;
            private int _stage = 1;
            private int _totalStages = 4;
            private float _stageDuration;
            private float _secondsToNext;

            public bool InternalUpdate { get; private set; }

            private void Awake()
            {
                _ore = GetComponent<OreResourceEntity>();
            }

            public void Begin(float totalSeconds, int stages, bool initializeAsNew)
            {
                if (_started || _ore == null || _ore.IsDestroyed || Instance == null)
                    return;

                _started = true;
                _totalStages = Mathf.Clamp(stages, 2, 10);
                _stageDuration = Mathf.Max(1f, totalSeconds / (_totalStages - 1));

                if (initializeAsNew)
                {
                    _stage = 1;
                    SetHealthForStage(_stage);
                }
                else
                {
                    _stage = GuessStageFromHealth();
                }

                _secondsToNext = _stageDuration;
                InvokeRepeating(nameof(Tick), 0.2f, 1f);
            }

            private int GuessStageFromHealth()
            {
                if (_ore == null || _ore.IsDestroyed)
                    return 1;

                float maxHealth = Mathf.Max(1f, _ore.MaxHealth());
                float ratio = Mathf.Clamp01(_ore.Health() / maxHealth);
                int guessed = Mathf.CeilToInt(ratio * _totalStages);
                return Mathf.Clamp(guessed, 1, _totalStages - 1);
            }

            private void Tick()
            {
                if (_ore == null || _ore.IsDestroyed || Instance == null)
                {
                    Destroy(this);
                    return;
                }

                if (_mature || _ore.skinID == Instance._config.MatureOreSkinId)
                {
                    _mature = true;
                    Instance.DrawGrowthInfo(_ore, _totalStages, _totalStages, 0f, true);
                    return;
                }

                _secondsToNext -= 1f;

                if (_secondsToNext <= 0f)
                {
                    _stage++;

                    if (_stage >= _totalStages)
                    {
                        _stage = _totalStages;
                        SetHealthForStage(_stage);
                        _mature = true;
                        Instance.MatureOre(_ore);
                        Instance.DrawGrowthInfo(_ore, _stage, _totalStages, 0f, true);
                        return;
                    }

                    SetHealthForStage(_stage);
                    _secondsToNext = _stageDuration;
                }

                Instance.DrawGrowthInfo(_ore, _stage, _totalStages, _secondsToNext, false);
            }

            private void SetHealthForStage(int stage)
            {
                if (_ore == null || _ore.IsDestroyed)
                    return;

                float maxHealth = Mathf.Max(1f, _ore.MaxHealth());

                // Stage 1 starts as a heavily damaged/small node.
                // Each stage restores more of the node; Rust's staged-resource visuals
                // update together with the health/damage stage.
                float targetRatio = Mathf.Clamp01((float)stage / _totalStages);
                float targetHealth = Mathf.Max(1f, maxHealth * targetRatio);
                float currentHealth = _ore.Health();
                float delta = targetHealth - currentHealth;

                if (Mathf.Abs(delta) < 0.1f)
                {
                    _ore.SendNetworkUpdateImmediate();
                    return;
                }

                InternalUpdate = true;

                try
                {
                    // Positive damage shrinks the node; negative damage heals/grows it.
                    var hit = new HitInfo(
                        new BasePlayer(),
                        _ore,
                        Rust.DamageType.Generic,
                        -delta,
                        _ore.transform.position
                    );

                    _ore.OnAttacked(hit);
                    _ore.SendNetworkUpdateImmediate();
                }
                finally
                {
                    InternalUpdate = false;
                }
            }

            private void OnDestroy()
            {
                CancelInvoke();
            }
        }

        #endregion
    }
}
