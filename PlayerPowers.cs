using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PlayerPowers", "MrCodder", "1.5.0")]
    [Description("God mode and noclip for online players by nickname or SteamID.")]
    public class PlayerPowers : RustPlugin
    {
        private readonly HashSet<ulong> godPlayers = new HashSet<ulong>();
        private readonly HashSet<ulong> flyPlayers = new HashSet<ulong>();
        private readonly HashSet<ulong> temporaryAdminFlags = new HashSet<ulong>();
        private readonly HashSet<ulong> deleteShotPlayers = new HashSet<ulong>();

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null)
                    continue;

                if (flyPlayers.Contains(player.userID))
                    player.SendConsoleCommand("noclip");

                RestoreAdminFlag(player);
            }

            godPlayers.Clear();
            flyPlayers.Clear();
            temporaryAdminFlags.Clear();
            deleteShotPlayers.Clear();
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // One-shot entity remover. The next damage hit from an armed player
            // deletes the entity they hit. If a modular-car module is hit,
            // delete the whole ModularCar instead of only that single module.
            BasePlayer attacker = info != null ? info.Initiator as BasePlayer : null;

            if (attacker != null && deleteShotPlayers.Contains(attacker.userID))
            {
                if (entity is BasePlayer)
                {
                    Puts("DELSHOT ignored a player hit from " + attacker.displayName +
                         "; remover is still armed.");
                    return null;
                }

                deleteShotPlayers.Remove(attacker.userID);

                BaseEntity target = entity;
                ModularCar parentCar = entity.GetComponentInParent<ModularCar>();
                if (parentCar != null && !parentCar.IsDestroyed)
                    target = parentCar;

                string targetName = target.ShortPrefabName;
                Vector3 targetPosition = target.transform.position;

                // Prevent the original shot from applying normal damage first.
                if (info != null && info.damageTypes != null)
                    info.damageTypes.ScaleAll(0f);

                timer.Once(0f, () =>
                {
                    if (target == null || target.IsDestroyed)
                        return;

                    target.Kill();

                    Puts("DELSHOT " + attacker.displayName +
                         " removed " + targetName +
                         " at " + targetPosition.ToString());
                });

                return true;
            }

            BasePlayer player = entity as BasePlayer;
            if (player == null || !godPlayers.Contains(player.userID))
                return null;

            if (info != null && info.damageTypes != null)
                info.damageTypes.ScaleAll(0f);

            return true;
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;

            godPlayers.Remove(player.userID);
            flyPlayers.Remove(player.userID);
            deleteShotPlayers.Remove(player.userID);
            RestoreAdminFlag(player);
        }

        [ConsoleCommand("pgod")]
        private void CommandGod(ConsoleSystem.Arg arg)
        {
            string[] args = ReadArgs(arg);
            if (args.Length == 0)
            {
                Puts("Usage: pgod <nickname|steamid> [on|off]");
                return;
            }

            string query;
            bool? wanted;
            ParseTargetAndState(args, out query, out wanted);

            string error;
            BasePlayer target = FindOnlinePlayer(query, out error);
            if (target == null)
            {
                Puts(error);
                return;
            }

            bool current = godPlayers.Contains(target.userID);
            bool enabled = wanted.HasValue ? wanted.Value : !current;

            if (enabled)
                godPlayers.Add(target.userID);
            else
                godPlayers.Remove(target.userID);

            Puts("GOD " + (enabled ? "ON" : "OFF") + ": " +
                 target.displayName + " (" + target.userID + ")");
        }

        [ConsoleCommand("pfly")]
        private void CommandFly(ConsoleSystem.Arg arg)
        {
            string[] args = ReadArgs(arg);
            if (args.Length == 0)
            {
                Puts("Usage: pfly <nickname|steamid> [on|off]");
                return;
            }

            string query;
            bool? wanted;
            ParseTargetAndState(args, out query, out wanted);

            string error;
            BasePlayer target = FindOnlinePlayer(query, out error);
            if (target == null)
            {
                Puts(error);
                return;
            }

            bool current = flyPlayers.Contains(target.userID);
            bool enabled = wanted.HasValue ? wanted.Value : !current;

            if (enabled == current)
            {
                Puts("FLY already " + (enabled ? "ON" : "OFF") + ": " +
                     target.displayName + " (" + target.userID + ")");
                return;
            }

            if (enabled)
            {
                bool wasAdmin = target.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin);

                if (!wasAdmin)
                {
                    target.playerFlags |= BasePlayer.PlayerFlags.IsAdmin;
                    temporaryAdminFlags.Add(target.userID);
                    target.SendNetworkUpdateImmediate();
                }

                flyPlayers.Add(target.userID);
                target.SendConsoleCommand("noclip");
            }
            else
            {
                target.SendConsoleCommand("noclip");
                flyPlayers.Remove(target.userID);
                RestoreAdminFlag(target);
            }

            Puts("FLY " + (enabled ? "ON" : "OFF") + ": " +
                 target.displayName + " (" + target.userID + ")");
        }

        [ConsoleCommand("delshot")]
        private void CommandDeleteShot(ConsoleSystem.Arg arg)
        {
            string[] args = ReadArgs(arg);
            if (args.Length == 0)
            {
                Puts("Usage: delshot <nickname|steamid> [on|off]");
                return;
            }

            string query;
            bool? wanted;
            ParseTargetAndState(args, out query, out wanted);

            string error;
            BasePlayer target = FindOnlinePlayer(query, out error);
            if (target == null)
            {
                Puts(error);
                return;
            }

            bool current = deleteShotPlayers.Contains(target.userID);
            bool enabled = wanted.HasValue ? wanted.Value : !current;

            if (enabled)
                deleteShotPlayers.Add(target.userID);
            else
                deleteShotPlayers.Remove(target.userID);

            Puts("DELSHOT " + (enabled ? "ARMED" : "OFF") + ": " +
                 target.displayName + " (" + target.userID + ")" +
                 (enabled ? " | next shot deletes the hit entity" : ""));
        }

        [ConsoleCommand("spawntanker")]
        private void CommandSpawnTanker(ConsoleSystem.Arg arg)
        {
            string[] args = ReadArgs(arg);
            if (args.Length == 0)
            {
                Puts("Usage: spawntanker <nickname|steamid>");
                return;
            }

            string error;
            BasePlayer target = FindOnlinePlayer(string.Join(" ", args), out error);
            if (target == null)
            {
                Puts(error);
                return;
            }

            Vector3 forward = target.transform.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;
            else
                forward.Normalize();

            Vector3 spawnPosition = target.transform.position + forward * 6f + Vector3.up;
            Quaternion spawnRotation = Quaternion.Euler(
                0f,
                target.transform.eulerAngles.y + 180f,
                0f);

            BaseEntity baseEntity = GameManager.server.CreateEntity(
                "assets/content/vehicles/modularcar/car_chassis_3module.entity.prefab",
                spawnPosition,
                spawnRotation);

            ModularCar car = baseEntity as ModularCar;
            if (car == null)
            {
                if (baseEntity != null)
                    baseEntity.Kill();

                Puts("Failed to create 3-module car chassis.");
                return;
            }

            car.OwnerID = target.userID;
            car.enableSaving = true;
            car.Spawn();

            ItemDefinition cockpitDef = ItemManager.FindItemDefinition("vehicle.1mod.cockpit.with.engine");
            ItemDefinition tankerDef = ItemManager.FindItemDefinition("vehicle.2mod.fuel.tank");

            if (cockpitDef == null || tankerDef == null)
            {
                car.Kill();
                Puts("Required car module item definitions were not found.");
                return;
            }

            Item cockpit = ItemManager.Create(cockpitDef, 1);
            Item tanker = ItemManager.Create(tankerDef, 1);

            bool cockpitOk = cockpit != null && car.TryAddModule(cockpit, 0);
            if (!cockpitOk && cockpit != null)
                cockpit.Remove();

            bool tankerOk = tanker != null && car.TryAddModule(tanker, 1);
            if (!tankerOk && tanker != null)
                tanker.Remove();

            if (!cockpitOk || !tankerOk)
            {
                car.Kill();
                Puts("Car chassis spawned, but attaching cockpit/tanker failed.");
                return;
            }

            // Vehicle module entities are created a little after TryAddModule.
            // Wait briefly, then fill their real StorageContainer inventories.
            timer.Once(0.75f, () =>
            {
                if (car == null || car.IsDestroyed)
                    return;

                int installedParts = 0;

                if (TryInsertIntoCarStorage(car, "carburetor3", 1)) installedParts++;
                if (TryInsertIntoCarStorage(car, "crankshaft3", 1)) installedParts++;
                if (TryInsertIntoCarStorage(car, "piston3", 1)) installedParts++;
                if (TryInsertIntoCarStorage(car, "sparkplug3", 1)) installedParts++;
                if (TryInsertIntoCarStorage(car, "valve3", 1)) installedParts++;

                bool fuelInserted = TryInsertIntoCarStorage(car, "lowgradefuel", 500);

                // Fallback only if this Alkad build exposes vehicle storage differently.
                if (installedParts < 5)
                {
                    GiveMissingEngineParts(target, car);
                    Puts("Could not auto-fill every engine slot; missing T3 parts were given to " +
                         target.displayName + ".");
                }

                if (!fuelInserted)
                {
                    GiveItem(target, "lowgradefuel", 500);
                    Puts("Could not auto-fill the car fuel tank; 500 low grade fuel was given to " +
                         target.displayName + ".");
                }

                car.SendNetworkUpdateImmediate();

                Puts("Spawned tanker car for " + target.displayName +
                     " | engine parts installed: " + installedParts + "/5" +
                     " | fuel: " + (fuelInserted ? "500 inserted" : "given to player"));
            });
        }

        private bool TryInsertIntoCarStorage(ModularCar car, string shortName, int amount)
        {
            if (car == null || car.IsDestroyed || amount <= 0)
                return false;

            ItemDefinition definition = ItemManager.FindItemDefinition(shortName);
            if (definition == null)
                return false;

            Item item = ItemManager.Create(definition, amount);
            if (item == null)
                return false;

            StorageContainer[] containers = car.GetComponentsInChildren<StorageContainer>(true);

            if (containers != null)
            {
                foreach (StorageContainer container in containers)
                {
                    if (container == null || container.inventory == null)
                        continue;

                    // Let Rust's own container/slot rules decide whether this item
                    // belongs here. Engine parts will only enter the engine storage,
                    // and low grade will only enter the vehicle fuel storage.
                    if (item.MoveToContainer(container.inventory))
                    {
                        item.MarkDirty();
                        return true;
                    }
                }
            }

            item.Remove();
            return false;
        }

        private void GiveMissingEngineParts(BasePlayer player, ModularCar car)
        {
            if (player == null)
                return;

            string[] parts =
            {
                "carburetor3",
                "crankshaft3",
                "piston3",
                "sparkplug3",
                "valve3"
            };

            // Only give a fallback set. This is deliberately simple and avoids
            // depending on EngineStorage APIs that vary between Rust/Oxide builds.
            foreach (string shortName in parts)
                GiveItem(player, shortName, 1);
        }

        private void GiveItem(BasePlayer player, string shortName, int amount)
        {
            if (player == null || amount <= 0)
                return;

            ItemDefinition definition = ItemManager.FindItemDefinition(shortName);
            if (definition == null)
            {
                Puts("Item not found: " + shortName);
                return;
            }

            Item item = ItemManager.Create(definition, amount);
            if (item != null)
                player.GiveItem(item);
        }

        [ConsoleCommand("fixcar")]
        private void CommandFixCar(ConsoleSystem.Arg arg)
        {
            string[] args = ReadArgs(arg);
            if (args.Length == 0)
            {
                Puts("Usage: fixcar <nickname|steamid>");
                return;
            }

            string error;
            BasePlayer target = FindOnlinePlayer(string.Join(" ", args), out error);
            if (target == null)
            {
                Puts(error);
                return;
            }

            ModularCar nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (ModularCar car in UnityEngine.Object.FindObjectsOfType<ModularCar>())
            {
                if (car == null || car.IsDestroyed)
                    continue;

                float distance = Vector3.Distance(target.transform.position, car.transform.position);
                if (distance < nearestDistance)
                {
                    nearest = car;
                    nearestDistance = distance;
                }
            }

            if (nearest == null || nearestDistance > 20f)
            {
                Puts("No modular car found within 20m of " + target.displayName + ".");
                return;
            }

            string[] engineParts =
            {
                "carburetor3",
                "crankshaft3",
                "piston3",
                "sparkplug3",
                "valve3"
            };

            int alreadyInstalled = 0;
            int newlyInstalled = 0;
            int failedParts = 0;

            foreach (string shortName in engineParts)
            {
                if (CarHasItem(nearest, shortName, 1))
                {
                    alreadyInstalled++;
                    continue;
                }

                if (TryInsertIntoCarStorage(nearest, shortName, 1))
                    newlyInstalled++;
                else
                    failedParts++;
            }

            int currentFuel = CountCarItem(nearest, "lowgradefuel");
            int fuelNeeded = Mathf.Max(0, 500 - currentFuel);
            bool fuelOk = fuelNeeded == 0 || TryInsertIntoCarStorage(nearest, "lowgradefuel", fuelNeeded);

            // Repair chassis and every damageable child entity of the modular car.
            // On Rust modular cars the installed modules are child BaseCombatEntity
            // objects, so this repairs cockpit, tanker and any other installed module.
            int repairedEntities = 0;
            BaseCombatEntity[] damageables = nearest.GetComponentsInChildren<BaseCombatEntity>(true);

            if (damageables != null)
            {
                foreach (BaseCombatEntity damageable in damageables)
                {
                    if (damageable == null || damageable.IsDestroyed)
                        continue;

                    damageable.health = damageable.MaxHealth();
                    damageable.SendNetworkUpdateImmediate();
                    repairedEntities++;
                }
            }

            nearest.health = nearest.MaxHealth();
            nearest.SendNetworkUpdateImmediate();

            if (failedParts > 0)
            {
                Puts("fixcar: some T3 engine parts could not be inserted automatically (" +
                     failedParts + ").");
            }

            Puts(
                "FIXCAR " + target.displayName +
                " | distance: " + nearestDistance.ToString("0.0") + "m" +
                " | T3 already: " + alreadyInstalled +
                " | T3 inserted: " + newlyInstalled +
                " | fuel before: " + currentFuel +
                " | fuel: " + (fuelOk ? "OK (target 500)" : "FAILED") +
                " | repaired entities/modules: " + repairedEntities +
                " | health: 100%");
        }

        private bool CarHasItem(ModularCar car, string shortName, int minimumAmount)
        {
            return CountCarItem(car, shortName) >= minimumAmount;
        }

        private int CountCarItem(ModularCar car, string shortName)
        {
            if (car == null || car.IsDestroyed || string.IsNullOrEmpty(shortName))
                return 0;

            int total = 0;
            StorageContainer[] containers = car.GetComponentsInChildren<StorageContainer>(true);

            if (containers == null)
                return 0;

            foreach (StorageContainer container in containers)
            {
                if (container == null || container.inventory == null || container.inventory.itemList == null)
                    continue;

                foreach (Item item in container.inventory.itemList)
                {
                    if (item == null || item.info == null)
                        continue;

                    if (string.Equals(item.info.shortname, shortName, StringComparison.OrdinalIgnoreCase))
                        total += item.amount;
                }
            }

            return total;
        }

        [ConsoleCommand("ppowers")]
        private void CommandStatus(ConsoleSystem.Arg arg)
        {
            string[] args = ReadArgs(arg);
            if (args.Length == 0)
            {
                Puts("Usage: ppowers <nickname|steamid>");
                return;
            }

            string query = string.Join(" ", args);

            string error;
            BasePlayer target = FindOnlinePlayer(query, out error);
            if (target == null)
            {
                Puts(error);
                return;
            }

            Puts(target.displayName + " (" + target.userID + ")" +
                 " | GOD: " + (godPlayers.Contains(target.userID) ? "ON" : "OFF") +
                 " | FLY: " + (flyPlayers.Contains(target.userID) ? "ON" : "OFF") +
                 " | DELSHOT: " + (deleteShotPlayers.Contains(target.userID) ? "ARMED" : "OFF"));
        }

        private string[] ReadArgs(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Args == null || arg.Args.Length == 0)
                return new string[0];

            string[] result = new string[arg.Args.Length];

            for (int i = 0; i < arg.Args.Length; i++)
                result[i] = arg.Args[i].ToString();

            return result;
        }

        private void ParseTargetAndState(string[] args, out string query, out bool? wanted)
        {
            wanted = null;
            int count = args.Length;

            if (count > 1)
            {
                string last = args[count - 1].ToLowerInvariant();

                if (last == "on" || last == "true" || last == "1")
                {
                    wanted = true;
                    count--;
                }
                else if (last == "off" || last == "false" || last == "0")
                {
                    wanted = false;
                    count--;
                }
            }

            query = string.Join(" ", args.Take(count).ToArray()).Trim();
        }

        private BasePlayer FindOnlinePlayer(string query, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(query))
            {
                error = "Player name/SteamID is empty.";
                return null;
            }

            BasePlayer exact = null;
            List<BasePlayer> partial = new List<BasePlayer>();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null)
                    continue;

                if (player.UserIDString == query ||
                    string.Equals(player.displayName, query, StringComparison.OrdinalIgnoreCase))
                {
                    exact = player;
                    break;
                }

                if (!string.IsNullOrEmpty(player.displayName) &&
                    player.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial.Add(player);
                }
            }

            if (exact != null)
                return exact;

            if (partial.Count == 1)
                return partial[0];

            if (partial.Count == 0)
            {
                error = "Online player not found: " + query;
                return null;
            }

            error = "Several players matched: " +
                    string.Join(", ", partial.Select(x =>
                        x.displayName + " (" + x.userID + ")").ToArray());

            return null;
        }

        private void RestoreAdminFlag(BasePlayer player)
        {
            if (player == null || !temporaryAdminFlags.Contains(player.userID))
                return;

            player.playerFlags &= ~BasePlayer.PlayerFlags.IsAdmin;
            temporaryAdminFlags.Remove(player.userID);
            player.SendNetworkUpdateImmediate();
        }
    }
}
