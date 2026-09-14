//todo: lessen lambda use a bit to optimize performance
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;
// ReSharper disable UnusedMember.Local
// ReSharper disable SuggestBaseTypeForParameter
// ReSharper disable CheckNamespace
// ReSharper disable UnusedParameter.Local

namespace Oxide.Plugins
{
    [Info("WorldShops", "MrCodder", "1.2.3", ResourceId = 2820)]
    [Description("Allows for automated vending machines.")]
    public class WorldShops : RustPlugin
    {
        private static PluginTimers _timer;

        private Dictionary<BasePlayer, WorldShopsSettings.Shop> queuedShops;
        private List<BasePlayer> queuedWipes;
        private Dictionary<BasePlayer, WorldShopsSettings.Shop> queuedSaves;
        private List<BasePlayer> queuedDisables;
        private List<BasePlayer> queuedSpawns;
        private List<BasePlayer> queuedRemoves;

        private static Dictionary<VendingMachine, WorldShopsSettings.Shop> _activeShops;

        private void Init()
        {
            _timer = this.timer;

            WorldShopsSettings.Loaded = this.Config.ReadObject<WorldShopsSettings.General>();
            if (WorldShopsSettings.Loaded == null)
                WorldShopsSettings.Loaded = new WorldShopsSettings.General();

            if (WorldShopsSettings.Loaded.Shops == null)
                WorldShopsSettings.Loaded.Shops = new List<WorldShopsSettings.Shop>();

            if (WorldShopsSettings.Loaded.Notification == null)
            {
                WorldShopsSettings.Loaded.Notification = new WorldShopsSettings.ShopNotification
                {
                    Enabled = false,
                    FadeIn = 0.5f,
                    WaitTime = 2f,
                    FadeOut = 0.5f
                };
            }

            WorldShopsData.Loaded = Interface.Oxide.DataFileSystem.ReadObject<WorldShopsData.General>(nameof(WorldShops));
            if (WorldShopsData.Loaded == null)
                WorldShopsData.Loaded = new WorldShopsData.General();

            if (WorldShopsData.Loaded.Shops == null)
                WorldShopsData.Loaded.Shops = new Dictionary<string, string>();

            for (int i = 0; i < WorldShopsSettings.Loaded.Shops.Count; i++)
            {
                WorldShopsSettings.Shop shop = WorldShopsSettings.Loaded.Shops[i];

                if (shop.CommandName == null)
                {
                    this.PrintError($"Unable to load WorldShops. The {nameof(WorldShopsSettings.Shop.CommandName)} of {nameof(WorldShopsSettings.Shop)} #{i + 1} is null. Please set a value.");
                    this.Manager.RemovePlugin(this);
                    return;
                }

                if (shop.SellOrders == null)
                {
                    shop.SellOrders = new WorldShopsSettings.SellOrder[0];
                    this.PrintWarning($"The {nameof(WorldShopsSettings.Shop.SellOrders)} of {nameof(WorldShopsSettings.Shop)} \"{shop.CommandName}\" is null. The value has been set to an empty array.");
                }

                if (shop.WorldName == null)
                {
                    shop.WorldName = "A Shop";
                    this.PrintWarning($"The {nameof(WorldShopsSettings.Shop.WorldName)} of {nameof(WorldShopsSettings.Shop)} \"{shop.CommandName}\" is null. The value has been set \"A Shop\".");
                }

                for (int j = 0; j < shop.SellOrders.Length; j++)
                {
                    if (shop.SellOrders[j].BuyItem.Definition == null)
                    {
                        this.RaiseError($"Unable to load WorldShops. The {nameof(WorldShopsSettings.SellOrder.BuyItem)} of {nameof(WorldShopsSettings.SellOrder)} #{j + 1} in {nameof(WorldShopsSettings.Shop)} \"{shop.CommandName}\" has an invalid item shortname.");
                        this.Manager.RemovePlugin(this);
                        return;
                    }
                    if (shop.SellOrders[j].SellItem.Definition == null)
                    {
                        this.RaiseError($"Unable to load WorldShops. The {nameof(WorldShopsSettings.SellOrder.SellItem)} of {nameof(WorldShopsSettings.SellOrder)} #{j + 1} in {nameof(WorldShopsSettings.Shop)} \"{shop.CommandName}\" has an invalid item shortname.");
                        this.Manager.RemovePlugin(this);
                        return;
                    }
                }
            }

            string[] names = WorldShopsSettings.Loaded.Shops.Select(x => x.CommandName).ToArray();
            string conflictingName = names.FirstOrDefault(x => names.Length - names.Except(new string[] {x}).Count() > 1);
            if (conflictingName != null)
            {
                this.RaiseError($"Unable to load WorldShops. Two or more shops have a conflicting command name: {conflictingName}");
                this.Manager.RemovePlugin(this);
                return;
            }

            this.queuedShops = new Dictionary<BasePlayer, WorldShopsSettings.Shop>();
            this.queuedWipes = new List<BasePlayer>();
            this.queuedSaves = new Dictionary<BasePlayer, WorldShopsSettings.Shop>();
            this.queuedDisables = new List<BasePlayer>();
            this.queuedSpawns = new List<BasePlayer>();
            this.queuedRemoves = new List<BasePlayer>();

            _activeShops = new Dictionary<VendingMachine, WorldShopsSettings.Shop>();
            
            this.permission.RegisterPermission("worldshops.build", this);
            this.permission.RegisterPermission("worldshops.spawn", this);
            this.permission.RegisterPermission("worldshops.apply", this);
            this.permission.RegisterPermission("worldshops.disable", this);
            this.permission.RegisterPermission("worldshops.wipe", this);
            this.permission.RegisterPermission("worldshops.save", this);
            this.permission.RegisterPermission("worldshops.delete", this);
            this.permission.RegisterPermission("worldshops.remove", this);

            this.Config.WriteObject(WorldShopsSettings.Loaded);

            if (WorldShopsSettings.Loaded.Notification.Enabled)
            {
                foreach (BasePlayer activePlayer in BasePlayer.activePlayerList)
                    activePlayer.gameObject.AddComponent<ShopBlock>();
            }
        }

        private void OnPlayerSleep(BasePlayer player)
        {
            ShopBlock shopBlock = player.gameObject.GetComponent<ShopBlock>();
            if (shopBlock == null)
                return;

            shopBlock.Dispose();
            UnityEngine.Object.Destroy(shopBlock);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (!WorldShopsSettings.Loaded.Notification.Enabled)
                return;

            if (player.gameObject.GetComponent<ShopBlock>() == null)
                player.gameObject.AddComponent<ShopBlock>();
        }

        private void OnServerInitialized()
        {
            foreach (VendingMachine machine in UnityEngine.Object.FindObjectsOfType<VendingMachine>())
            {
                if (machine == null || machine.IsDestroyed)
                    continue;

                string machineId = machine.transform.position.ToString("f2");

                string shopName;
                if (!WorldShopsData.Loaded.Shops.TryGetValue(machineId, out shopName) || string.IsNullOrEmpty(shopName))
                    continue;

                WorldShopsSettings.Shop shop = WorldShopsSettings.Loaded.Shops.FirstOrDefault(x => x.CommandName == shopName);
                if (shop == null)
                    continue;

                ApplyMachine(machine, shop);
                _activeShops[machine] = shop;
            }
        }

        protected override void LoadDefaultMessages()
        {
            this.lang.RegisterMessages(new Dictionary<string, string>
            {
                ["InsufficientPermission"] = "You do not have permission to use that command.",
                ["Help"] = "Use of /wshop:\n" +
                           "/wshop spawn - Spawns a vending machine, anywhere*\n" +
                           "/wshop apply [name] - Applies a specified shop to a vending machine*\n" +
                           "/wshop disable - No longer treats the vending machine as a shop*\n" +
                           "/wshop wipe - Resets a vending machine*\n" +
                           "/wshop save [name] - Creates a shop out of a customized vending machine*\n" +
                           "/wshop delete [name] - Deletes a shop*\n" +
                           "/wshop remove - Removes a placed vending machine*\n" +
                           "/wshop list - Lists all shops\n" +
                           "/wshop help - Shows this\n" +
                           "* Special permissions needed to execute",
                ["InvalidShop"] = "No shop with the name \"{0}\" exists.",
                ["ShopNotPlaced"] = "The shop \"{0}\" is not on the map.",
                ["ShopExists"] = "The shop name \"{0}\" is already taken.",
                ["ApplyReady"] = "Selected shop \"{0}\"",
                ["ApplyTimeout"] = "Deselected shop \"{0}\"",
                ["ApplySuccess"] = "Applied shop \"{0}\"",
                ["SaveReady"] = "Ready to save shop \"{0}\"",
                ["SaveTimeout"] = "Saving shop \"{0}\" timed out",
                ["SaveSuccess"] = "Saved shop \"{0}\"",
                ["DeletedShop"] = "Deleted shop \"{0}\"",
                ["DeletedShopPlaced"] = "Warning: This shop was placed and all instances of this shop in the world were deleted.",
                ["WipeReady"] = "Ready to wipe shop",
                ["WipeTimeout"] = "Shop wipe has timed out",
                ["WipeSuccess"] = "Wiped shop",
                ["DisableReady"] = "Ready to disable shop",
                ["DisableTimeout"] = "Shop disable has timed out",
                ["DisableSuccess"] = "Disabled shop",
                ["SpawnReady"] = "Ready to spawn vending machine",
                ["SpawnTimeout"] = "Spawn has timed out",
                ["SpawnSuccess"] = "Spawned vending machine",
                ["RemoveReady"] = "Hit the vending machine you want to remove within 5 seconds",
                ["RemoveTimeout"] = "Remove vending machine timed out",
                ["RemoveSuccess"] = "Removed vending machine",
                ["ShopTooClose"] = "Too close to shop ({0})",
                ["ShopListElement"] = "{0} ({1}) - {2} instances on map"
            }, this);
        }

        [ChatCommand("wshop")]
        private void ShopCommand(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                player.ChatMessage(this.Lang("Help", player));
                return;
            }

            bool commandPlaced;
            string fullName;
            bool fullPlaced;
            WorldShopsSettings.Shop foundShop;
            switch (args[0])
            {
                case "spawn":
                    if (!HasPermission(player, "worldshops.spawn"))
                    {
                        player.ChatMessage(this.Lang("InsufficientPermission", player));
                        return;
                    }

                    this.queuedSpawns.Add(player);
                    player.ChatMessage(this.Lang("SpawnReady", player));

                    _timer.Once(5f, () =>
                    {
                        if (!this.queuedSpawns.Contains(player))
                            return;

                        this.queuedSpawns.Remove(player);
                        player.ChatMessage(this.Lang("SpawnTimeout", player));
                    });
                    break;

                case "apply":
                    if (args.Length != 2)
                    {
                        player.ChatMessage(this.Lang("Help", player));
                        return;
                    }

                    if (!HasPermission(player, "worldshops.apply"))
                    {
                        player.ChatMessage(this.Lang("InsufficientPermission", player));
                        return;
                    }

                    if (!WorldShopsSettings.Loaded.Shops.Select(x => x.CommandName).Contains(args[1]))
                    {
                        player.ChatMessage(this.Lang("InvalidShop", player, args[1]));
                        return;
                    }
                    WorldShopsSettings.Shop applyShop = WorldShopsSettings.Loaded.Shops.First(x => x.CommandName == args[1]);

                    this.queuedShops.Add(player, applyShop);
                    player.ChatMessage(this.Lang("ApplyReady", player, args[1]));

                    _timer.Once(5f, () =>
                    {
                        if (!this.queuedShops.ContainsKey(player))
                            return;

                        this.queuedShops.Remove(player);
                        player.ChatMessage(this.Lang("ApplyTimeout", player, args[1]));
                    });
                    break;

                case "save":
                    if (args.Length != 2)
                    {
                        player.ChatMessage(this.Lang("Help", player));
                        return;
                    }

                    if (!HasPermission(player, "worldshops.save"))
                    {
                        player.ChatMessage(this.Lang("InsufficientPermission", player));
                        return;
                    }

                    if (WorldShopsSettings.Loaded.Shops.Select(x => x.CommandName).Contains(args[1]))
                    {
                        player.ChatMessage(this.Lang("ShopExists", player, args[1]));
                        return;
                    }

                    WorldShopsSettings.Shop newShop = new WorldShopsSettings.Shop
                    {
                        CommandName = args[1]
                    };

                    WorldShopsSettings.Loaded.Shops.Add(newShop);
                    this.queuedSaves.Add(player, newShop);
                    player.ChatMessage(this.Lang("SaveReady", player, args[1]));

                    _timer.Once(5f, () =>
                    {
                        if (!this.queuedSaves.ContainsKey(player))
                            return;

                        this.queuedSaves.Remove(player);
                        player.ChatMessage(this.Lang("SaveTimeout", player, args[1]));
                    });
                    break;

                case "delete":
                    if (args.Length < 2)
                    {
                        player.ChatMessage(this.Lang("Help", player));
                        return;
                    }

                    if (!HasPermission(player, "worldshops.delete"))
                    {
                        player.ChatMessage(this.Lang("InsufficientPermission", player));
                        return;
                    }

                    if (!WorldShopsSettings.Loaded.Shops.Any(x => x.CommandName == args[1] || x.WorldName == string.Join(" ", args.Skip(1).ToArray())))
                    {
                        player.ChatMessage(this.Lang("InvalidShop", player, args[1]));
                        return;
                    }

                    fullName = string.Join(" ", args.Skip(1).ToArray());
                    foundShop = WorldShopsSettings.Loaded.Shops.First(x => x.CommandName == args[1] || x.WorldName == fullName);

                    commandPlaced = _activeShops.Any(x => x.Value.CommandName == args[1]);
                    fullPlaced = _activeShops.Any(x => x.Value.WorldName == fullName);

                    WorldShopsSettings.Loaded.Shops.RemoveAll(x => x.CommandName == args[1] || x.WorldName == string.Join(" ", args.Skip(1).ToArray()));
                    this.Config.WriteObject(WorldShopsSettings.Loaded);

                    player.ChatMessage(this.Lang("DeletedShop", player, args[1]));

                    if (commandPlaced || fullPlaced)
                    {
                        player.ChatMessage(this.Lang("DeletedShopPlaced", player, foundShop.WorldName));
                        foreach (VendingMachine vendingMachine in _activeShops.Where(x => x.Value == foundShop).Select(x => x.Key).ToArray())
                        {
                            _activeShops.Remove(vendingMachine);
                            if (vendingMachine != null && !vendingMachine.IsDestroyed)
                                vendingMachine.Kill();
                        }
                    }
                    break;

                case "wipe":
                    if (!HasPermission(player, "worldshops.wipe"))
                    {
                        player.ChatMessage(this.Lang("InsufficientPermission", player));
                        return;
                    }

                    this.queuedWipes.Add(player);
                    player.ChatMessage(this.Lang("WipeReady", player));

                    _timer.Once(5f, () =>
                    {
                        if (!this.queuedWipes.Contains(player))
                            return;

                        this.queuedWipes.Remove(player);
                        player.ChatMessage(this.Lang("WipeTimeout", player));
                    });
                    break;

                case "disable":
                    if (!HasPermission(player, "worldshops.disable"))
                    {
                        player.ChatMessage(this.Lang("InsufficientPermission", player));
                        return;
                    }

                    this.queuedDisables.Add(player);
                    player.ChatMessage(this.Lang("DisableReady", player));

                    _timer.Once(5f, () =>
                    {
                        if (!this.queuedDisables.Contains(player))
                            return;

                        this.queuedDisables.Remove(player);
                        player.ChatMessage(this.Lang("DisableTimeout", player));
                    });
                    break;

                case "list":
                    player.ChatMessage(string.Join("\n", WorldShopsSettings.Loaded.Shops.Select(x => this.Lang("ShopListElement", player, x.WorldName, x.CommandName, _activeShops.Count(y => y.Value == x))).ToArray()));
                    break;

                case "remove":
                    if (!HasPermission(player, "worldshops.remove"))
                    {
                        player.ChatMessage(this.Lang("InsufficientPermission", player));
                        return;
                    }

                    if (!queuedRemoves.Contains(player))
                        queuedRemoves.Add(player);

                    player.ChatMessage(this.Lang("RemoveReady", player));

                    _timer.Once(5f, () =>
                    {
                        if (!queuedRemoves.Contains(player))
                            return;

                        queuedRemoves.Remove(player);
                        player.ChatMessage(this.Lang("RemoveTimeout", player));
                    });
                    break;

                default:
                    player.ChatMessage(this.Lang("Help", player));
                    break;
            }
        }


        protected override void LoadDefaultConfig()
        {
            this.Config.WriteObject(new WorldShopsSettings.General
            {
                Notification = new WorldShopsSettings.ShopNotification
                {
                    Enabled = true,
                    FadeIn = 0.5f,
                    WaitTime = 2f,
                    FadeOut = 0.5f
                },
                Shops = new List<WorldShopsSettings.Shop>
                {
                    new WorldShopsSettings.Shop
                    {
                        WorldName = "Test Shop",
                        CommandName = "test",
                        SkinId = 0,
                        SellOrders = new WorldShopsSettings.SellOrder[]
                        {
                            new WorldShopsSettings.SellOrder
                            {
                                SellItem = new WorldShopsSettings.Item
                                {
                                    ShortName = "rifle.ak",
                                    Quantity = 1,
                                    Blueprint = true
                                },
                                BuyItem = new WorldShopsSettings.Item
                                {
                                    ShortName = "scrap",
                                    Quantity = 1500
                                }
                            }
                        },
                        BuildingBlockedDistance = 50f
                    }
                }
            }, true);
        }

        private void Unload()
        {
            if (WorldShopsData.Loaded != null)
            {
                WorldShopsData.Loaded.Shops.Clear();
                foreach (VendingMachine machine in _activeShops.Keys.ToArray())
                {
                    if (machine == null || machine.IsDestroyed)
                        continue;

                    WorldShopsData.Loaded.Shops[machine.transform.position.ToString("f2")] = _activeShops[machine].CommandName;
                }

                Interface.Oxide.DataFileSystem.WriteObject(nameof(WorldShops), WorldShopsData.Loaded);
            }

            foreach (ShopBlock shopBlock in UnityEngine.Object.FindObjectsOfType<ShopBlock>())
            {
                shopBlock.Dispose();
                UnityEngine.Object.Destroy(shopBlock);
            }
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || _activeShops == null)
                return;

            if (info.InitiatorPlayer != null && queuedSpawns.Contains(info.InitiatorPlayer))
            {
                info.damageTypes.ScaleAll(0f);
                return;
            }

            if (_activeShops.Any(x =>
                x.Key != null &&
                !x.Key.IsDestroyed &&
                Vector3.Distance(x.Key.CenterPoint(), entity.CenterPoint()) < x.Value.BuildingBlockedDistance))
            {
                info.damageTypes.ScaleAll(0f);
            }
        }

        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            if (planner == null || _activeShops == null || _activeShops.Count == 0)
                return null;

            BasePlayer player = planner.GetOwnerPlayer();
            if (player == null || HasPermission(player, "worldshops.build"))
                return null;

            Vector3 position = target.position;

            WorldShopsSettings.Shop[] closeShops = _activeShops
                .Where(x => x.Key != null && !x.Key.IsDestroyed)
                .Select(x => new KeyValuePair<WorldShopsSettings.Shop, float>(
                    x.Value, Vector3.Distance(x.Key.CenterPoint(), position)))
                .Where(x => x.Value < x.Key.BuildingBlockedDistance)
                .OrderBy(x => x.Value)
                .Select(x => x.Key)
                .ToArray();

            if (closeShops.Length > 0)
            {
                player.ChatMessage(Lang("ShopTooClose", player,
                    string.Join(", ", closeShops.Select(x => x.WorldName).ToArray())));
                return false;
            }

            return null;
        }

        /*
        This area may come of use later once I do more advanced damage detection on shop buildings

        private BasePlayer Owner(BaseEntity entity) =>
            this.Player.Players.FirstOrDefault(x => x.userID == entity.OwnerID);
        */

        //this would normally be hammer only but since spawning on ground is a thing i made it for all weapons
        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            VendingMachine machine = info.HitEntity as VendingMachine;
            if (machine == null)
            {
                if (this.queuedSpawns.Contains(attacker))
                {
                    Vector3 spawn = info.HitPositionWorld;

                    Quaternion rotation = Quaternion.LookRotation(attacker.transform.position - spawn); //flipped so front faces player
                    rotation = Quaternion.Euler(0f, rotation.eulerAngles.y, rotation.eulerAngles.z); //lock X so it doesnt rotate up or down

                    VendingMachine entity = GameManager.server.CreateEntity(
                        "assets/prefabs/deployable/vendingmachine/vendingmachine.deployed.prefab",
                        spawn, rotation) as VendingMachine;

                    if (entity == null)
                    {
                        queuedSpawns.Remove(attacker);
                        attacker.ChatMessage("Failed to spawn vending machine.");
                        return;
                    }

                    // Make the player who spawned the machine its owner so a non-admin
                    // with WorldShops permissions can open the vanilla administration UI
                    // and configure sell orders before /wshop save and /wshop apply.
                    entity.OwnerID = attacker.userID;
                    entity.Spawn();

                    attacker.ChatMessage(Lang("SpawnSuccess", attacker));
                    NextFrame(() => queuedSpawns.Remove(attacker)); //remove next frame so the hit entity doesnt take damage
                }

                return;
            }

            if (queuedRemoves.Contains(attacker))
            {
                queuedRemoves.Remove(attacker);

                if (_activeShops.ContainsKey(machine))
                    _activeShops.Remove(machine);

                machine.Kill();
                attacker.ChatMessage(this.Lang("RemoveSuccess", attacker));
                return;
            }

            if (this.queuedShops.ContainsKey(attacker))
            {
                this.ApplyMachine(machine, this.queuedShops[attacker]);

                if (_activeShops.ContainsKey(machine))
                    _activeShops[machine] = this.queuedShops[attacker];
                else
                    _activeShops.Add(machine, this.queuedShops[attacker]);

                attacker.ChatMessage(this.Lang("ApplySuccess", attacker, this.queuedShops[attacker].CommandName));
                this.queuedShops.Remove(attacker);

            }
            else if (this.queuedWipes.Contains(attacker))
            {
                if (_activeShops.ContainsKey(machine))
                {
                    this.WipeMachine(machine);
                    this.queuedWipes.Remove(attacker);
                    attacker.ChatMessage(this.Lang("WipeSuccess", attacker));
                }
            }
            else if (this.queuedDisables.Contains(attacker))
            {
                if (_activeShops.ContainsKey(machine))
                {
                    _activeShops.Remove(machine);
                    this.queuedDisables.Remove(attacker);
                    attacker.ChatMessage(this.Lang("DisableSuccess", attacker));
                }
            }
            else if (this.queuedSaves.ContainsKey(attacker))
            {
                this.SaveMachine(machine, this.queuedSaves[attacker]);
                this.Config.WriteObject(WorldShopsSettings.Loaded);

                attacker.ChatMessage(this.Lang("SaveSuccess", attacker, this.queuedSaves[attacker].CommandName));
                this.queuedSaves.Remove(attacker);
            }
        }


        private void OnEntityKill(BaseNetworkable entity)
        {
            VendingMachine machine = entity as VendingMachine;
            if (machine == null)
                return;

            if (_activeShops.ContainsKey(machine))
                _activeShops.Remove(machine);
        }

        private object OnVendingTransaction(VendingMachine machine, BasePlayer buyer, int sellOrderId, int numberOfTransactions, ItemContainer targetContainer)
        {
            if (machine == null || _activeShops == null || !_activeShops.ContainsKey(machine))
                return null;

            NextFrame(() =>
            {
                if (machine == null || machine.IsDestroyed || !_activeShops.ContainsKey(machine))
                    return;

                WorldShopsSettings.Shop shop = _activeShops[machine];
                if (shop.SellOrders == null || sellOrderId < 0 || sellOrderId >= shop.SellOrders.Length)
                    return;

                if (machine.sellOrders == null || machine.sellOrders.sellOrders == null ||
                    sellOrderId >= machine.sellOrders.sellOrders.Count)
                    return;

                int transactions = Mathf.Max(1, numberOfTransactions);
                WorldShopsSettings.SellOrder order = shop.SellOrders[sellOrderId];
                ProtoBuf.VendingMachine.SellOrder protoOrder = machine.sellOrders.sellOrders[sellOrderId];

                int refillAmount = Mathf.Max(1, order.SellItem.Quantity) * transactions;

                Item stack = machine.inventory.itemList.FirstOrDefault(x =>
                    x.info.itemid == protoOrder.itemToSellID &&
                    (!order.SellItem.Blueprint || x.IsBlueprint()));

                if (stack == null)
                {
                    Item refill = order.SellItem.Blueprint
                        ? GetBlueprint(order.SellItem.Definition, refillAmount)
                        : ItemManager.Create(order.SellItem.Definition, refillAmount);

                    if (refill != null)
                        refill.MoveToContainer(machine.inventory);
                }
                else
                {
                    stack.amount += refillAmount;
                    stack.MarkDirty();
                }

                int currencyToRemove = Mathf.Max(1, protoOrder.currencyAmountPerItem) * transactions;
                foreach (Item currencyStack in machine.inventory.itemList
                    .Where(x => x.info.itemid == protoOrder.currencyID)
                    .ToArray())
                {
                    if (currencyToRemove <= 0)
                        break;

                    int take = Mathf.Min(currencyStack.amount, currencyToRemove);
                    currencyStack.amount -= take;
                    currencyToRemove -= take;

                    if (currencyStack.amount <= 0)
                        currencyStack.Remove();
                    else
                        currencyStack.MarkDirty();
                }
            });

            return null;
        }

        private void ApplyMachine(VendingMachine machine, WorldShopsSettings.Shop shop)
        {
            if (machine == null || shop == null)
                return;

            if (shop.SellOrders == null)
                shop.SellOrders = new WorldShopsSettings.SellOrder[0];

            machine.sellOrders.sellOrders = shop.SellOrders.Select(x => x.ProtoBuf).ToList();
            machine.shopName = shop.WorldName;
            machine.skinID = shop.SkinId;
            machine.health = machine.MaxHealth();

            machine.inventory.Clear();
            foreach (WorldShopsSettings.SellOrder order in shop.SellOrders)
            {
                if (machine.inventory.itemList.Select(x => x.info).Contains(order.SellItem.Definition))
                    continue;

                if (order.SellItem.Blueprint)
                {
                    Item blueprint = GetBlueprint(order.SellItem.Definition, order.SellItem.Quantity);
                    if (blueprint != null)
                        blueprint.MoveToContainer(machine.inventory);
                }
                else
                {
                    Item item = ItemManager.Create(order.SellItem.Definition, order.SellItem.Quantity);
                    if (item != null)
                        item.MoveToContainer(machine.inventory);
                }
            }

            machine.SendNetworkUpdate();
        }

        private void WipeMachine(VendingMachine machine)
        {
            machine.sellOrders.sellOrders = new List<ProtoBuf.VendingMachine.SellOrder>();
            machine.inventory.Clear();
            machine.shopName = "A Shop";
            machine.skinID = 0UL;
            machine.health = machine.MaxHealth();

            _activeShops.Remove(machine);
            machine.SendNetworkUpdate();
        }

        private void SaveMachine(VendingMachine machine, WorldShopsSettings.Shop shop)
        {
            shop.SellOrders = machine.sellOrders.sellOrders.Select(x =>
                new WorldShopsSettings.SellOrder
                {
                    BuyItem = new WorldShopsSettings.Item
                    {
                        Definition = ItemManager.FindItemDefinition(x.currencyID),
                        Quantity = x.currencyAmountPerItem,
                        Blueprint = x.currencyIsBP
                    },
                    SellItem = new WorldShopsSettings.Item
                    {
                        Definition = ItemManager.FindItemDefinition(x.itemToSellID),
                        Quantity = x.itemToSellAmount,
                        Blueprint = x.itemToSellIsBP
                    }
                }).ToArray();

            shop.SkinId = machine.skinID;
            shop.WorldName = machine.shopName;
        }

        private Item GetBlueprint(ItemDefinition learnableItem, int amount = 1)
        {
            if (learnableItem == null)
                return null;

            ItemDefinition blueprintBase = ItemManager.FindItemDefinition("blueprintbase");
            if (blueprintBase == null)
                return null;

            Item item = ItemManager.Create(blueprintBase, Mathf.Max(1, amount));
            if (item == null)
                return null;

            item.blueprintTarget = learnableItem.itemid;
            return item;
        }

        private object CanAdministerVending(BasePlayer player, VendingMachine machine)
        {
            if (_activeShops.ContainsKey(machine))
                return false;
            
            return null;
        }
        
        private object OnRotateVendingMachine(VendingMachine machine, BasePlayer player)
        {
            if (_activeShops.ContainsKey(machine))
            {
                if (HasPermission(player, "worldshops.build"))
                    return null;

                return false;
            }

            return null;
        }

        private bool HasPermission(BasePlayer player, string permissionName)
        {
            if (player == null)
                return false;

            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, permissionName);
        }

        private string Lang(string key, BasePlayer player, params object[] args) =>
            string.Format(lang.GetMessage(key, this, player.UserIDString), args);

        private class ShopBlock : MonoBehaviour
        {
            private BasePlayer player;
            private bool notificaitonActive;
            private bool notificationShown;

            public void Start()
            {
                player = GetComponent<BasePlayer>();
                if (player == null || _activeShops == null)
                    return;

                notificationShown = _activeShops.Any(x =>
                    x.Key != null && !x.Key.IsDestroyed &&
                    Vector3.Distance(x.Key.CenterPoint(), player.CenterPoint()) < x.Value.BuildingBlockedDistance);
            }

            public void Update()
            {
                if (player == null || _activeShops == null)
                    return;

                if (_activeShops.Any(x =>
                    x.Key != null && !x.Key.IsDestroyed &&
                    Vector3.Distance(x.Key.CenterPoint(), player.CenterPoint()) < x.Value.BuildingBlockedDistance))
                {
                    if (!this.notificationShown && !this.notificaitonActive)
                    {
                        this.ShowGui(false);
                        this.notificationShown = true;
                    }
                }
                else if (this.notificationShown && !this.notificaitonActive)
                {
                    this.ShowGui(true);
                    this.notificationShown = false;
                }
            }

            private void ShowGui(bool exiting)
            {
                this.notificaitonActive = true;

                float[] position = { 0.3945f, 0.11f };
                float[] maxPosition = { position[0] + 0.1953125f, position[1] + 0.104166667f };
                CuiHelper.AddUi(this.player, new List<CuiElement>
                {
                    new CuiElement
                    {
                       Name = "ShopBlockedIcon",
                        Components =
                        {
                            new CuiRawImageComponent
                            {
                                Url = exiting ? "https://i.imgur.com/0BH83NH.png" : "https://i.imgur.com/MWY2a6x.png",
                                FadeIn = WorldShopsSettings.Loaded.Notification.FadeIn
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = $"{position[0]} {position[1]}",
                                AnchorMax = $"{maxPosition[0]} {maxPosition[1]}"
                            }
                        },
                        FadeOut = WorldShopsSettings.Loaded.Notification.FadeOut
                    }
                });

                _timer.Once(WorldShopsSettings.Loaded.Notification.WaitTime + WorldShopsSettings.Loaded.Notification.FadeIn, () => CuiHelper.DestroyUi(this.player, "ShopBlockedIcon"));
                _timer.Once(WorldShopsSettings.Loaded.Notification.WaitTime + WorldShopsSettings.Loaded.Notification.FadeIn + WorldShopsSettings.Loaded.Notification.FadeOut, () => this.notificaitonActive = false);
            }

            public void Dispose()
            {
                CuiHelper.DestroyUi(this.player, "ShopBlockedIcon");

                this.player = null;
                this.notificaitonActive = false;
                this.notificationShown = false;
            }
        }

        private class WorldShopsSettings
        {
            public class Item
            {
                [JsonIgnore]
                public ItemDefinition Definition { get; set; }
                
                public string ShortName
                {
                    get
                    {
                        return this.Definition.shortname;
                    }
                    set
                    {
                        this.Definition = ItemManager.FindItemDefinition(value);
                    }
                }
                
                public int Quantity { get; set; }
                public bool Blueprint { get; set; }
            }

            public class SellOrder
            {
                [JsonIgnore]
                public ProtoBuf.VendingMachine.SellOrder ProtoBuf
                {
                    get
                    {
                        return new ProtoBuf.VendingMachine.SellOrder
                        {
                            currencyID = this.BuyItem.Definition.itemid,
                            currencyAmountPerItem = this.BuyItem.Quantity,
                            currencyIsBP = this.BuyItem.Blueprint,
                            itemToSellID = this.SellItem.Definition.itemid,
                            itemToSellAmount = this.SellItem.Quantity,
                            itemToSellIsBP = this.SellItem.Blueprint
                        };
                    }
                }
                
                public Item SellItem { get; set; }
                public Item BuyItem { get; set; }
            }

            public class Shop
            {
                public SellOrder[] SellOrders { get; set; }
                public string WorldName { get; set; }
                public string CommandName { get; set; }
                public ulong SkinId { get; set; }
                public float BuildingBlockedDistance { get; set; }
            }

            public class ShopNotification
            {
                public bool Enabled { get; set; }
                public float FadeIn { get; set; }
                public float WaitTime { get; set; }
                public float FadeOut { get; set; }
            }

            public class General
            {
                public List<Shop> Shops { get; set; }
                public ShopNotification Notification { get; set; }
            }

            public static General Loaded;
        }

        private class WorldShopsData
        {
            public class General
            {
                public Dictionary<string, string> Shops { get; set; }
            }

            public static General Loaded;
        }
    }
}