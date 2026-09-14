using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("FancyDrop", "MrCodder", "1.0.0")]
    [Description("Minimal Alkad-compatible accelerator for Supply Signal cargo planes and crates. Does not touch loot.")]
    public class FancyDrop : RustPlugin
    {
        private PluginConfig config;

        private readonly HashSet<CargoPlane> signalPlanes = new HashSet<CargoPlane>();

        private FieldInfo planeStartPosField;
        private FieldInfo planeEndPosField;
        private FieldInfo planeSecondsToTakeField;
        private FieldInfo planeSecondsTakenField;

        private PropertyInfo rigidbodyDragProperty;
        private PropertyInfo rigidbodyLinearDampingProperty;
        private FieldInfo rigidbodyDragField;
        private FieldInfo rigidbodyLinearDampingField;

        private class PluginConfig
        {
            public float PlaneSpeed = 110f;
            public float CrateAirResistance = 0.70f;
            public bool Debug = false;
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            SaveConfig();
        }

        private void LoadConfigValues()
        {
            try
            {
                config = Config.ReadObject<PluginConfig>();
            }
            catch
            {
                PrintWarning("Config is invalid. Creating a fresh FancyDrop.json.");
                config = new PluginConfig();
            }

            if (config == null)
                config = new PluginConfig();

            if (config.PlaneSpeed < 20f)
                config.PlaneSpeed = 20f;

            if (config.PlaneSpeed > 200f)
                config.PlaneSpeed = 200f;

            if (config.CrateAirResistance < 0.05f)
                config.CrateAirResistance = 0.05f;

            if (config.CrateAirResistance > 10f)
                config.CrateAirResistance = 10f;

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        private void Init()
        {
            LoadConfigValues();
            CacheReflection();

            Puts("Supply Signal acceleration enabled: planeSpeed=" +
                 config.PlaneSpeed.ToString("0.##") +
                 ", crateAirResistance=" +
                 config.CrateAirResistance.ToString("0.##") +
                 ". Loot is untouched.");
        }

        private void Unload()
        {
            signalPlanes.Clear();
        }

        private void CacheReflection()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            planeStartPosField = typeof(CargoPlane).GetField("startPos", flags);
            planeEndPosField = typeof(CargoPlane).GetField("endPos", flags);
            planeSecondsToTakeField = typeof(CargoPlane).GetField("secondsToTake", flags);
            planeSecondsTakenField = typeof(CargoPlane).GetField("secondsTaken", flags);

            rigidbodyDragProperty = typeof(Rigidbody).GetProperty("drag", flags);
            rigidbodyLinearDampingProperty = typeof(Rigidbody).GetProperty("linearDamping", flags);
            rigidbodyDragField = typeof(Rigidbody).GetField("drag", flags);
            rigidbodyLinearDampingField = typeof(Rigidbody).GetField("linearDamping", flags);

            if (planeStartPosField == null ||
                planeEndPosField == null ||
                planeSecondsToTakeField == null)
            {
                PrintWarning("CargoPlane timing fields were not all found. Plane acceleration may not work on this build.");
            }

            if (rigidbodyDragProperty == null &&
                rigidbodyLinearDampingProperty == null &&
                rigidbodyDragField == null &&
                rigidbodyLinearDampingField == null)
            {
                PrintWarning("Rigidbody drag/damping member was not found. Crate fall acceleration may not work on this build.");
            }
        }

        // Official Oxide/uMod Rust hook: called when a Supply Signal has called a CargoPlane.
        private void OnCargoPlaneSignaled(CargoPlane cargoPlane, SupplySignal supplySignal)
        {
            if (cargoPlane == null)
                return;

            signalPlanes.Add(cargoPlane);

            // CargoPlane timing can finish initialization just after this hook.
            timer.Once(0.10f, delegate
            {
                if (cargoPlane == null || cargoPlane.IsDestroyed)
                    return;

                ApplyPlaneSpeed(cargoPlane);
            });
        }

        // Official Oxide/uMod Rust hook: called right after the plane drops the crate.
        private void OnSupplyDropDropped(SupplyDrop supplyDrop, CargoPlane cargoPlane)
        {
            if (supplyDrop == null || cargoPlane == null)
                return;

            if (!signalPlanes.Contains(cargoPlane))
                return;

            timer.Once(0.05f, delegate
            {
                if (supplyDrop == null || supplyDrop.IsDestroyed)
                    return;

                ApplyCrateResistance(supplyDrop);
            });
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            CargoPlane plane = entity as CargoPlane;
            if (plane != null)
                signalPlanes.Remove(plane);
        }

        private void ApplyPlaneSpeed(CargoPlane plane)
        {
            if (planeStartPosField == null ||
                planeEndPosField == null ||
                planeSecondsToTakeField == null)
                return;

            try
            {
                Vector3 start = (Vector3)planeStartPosField.GetValue(plane);
                Vector3 end = (Vector3)planeEndPosField.GetValue(plane);

                float distance = Vector3.Distance(start, end);
                if (distance < 1f)
                {
                    if (config.Debug)
                        Puts("Signal plane distance was not initialized yet; retrying once.");

                    timer.Once(0.25f, delegate
                    {
                        if (plane != null && !plane.IsDestroyed)
                            ApplyPlaneSpeed(plane);
                    });

                    return;
                }

                float newSecondsToTake = distance / config.PlaneSpeed;
                if (newSecondsToTake < 1f)
                    newSecondsToTake = 1f;

                float oldSecondsToTake = Convert.ToSingle(planeSecondsToTakeField.GetValue(plane));
                float oldSecondsTaken = 0f;

                if (planeSecondsTakenField != null)
                    oldSecondsTaken = Convert.ToSingle(planeSecondsTakenField.GetValue(plane));

                float progress = 0f;
                if (oldSecondsToTake > 0.01f)
                    progress = Mathf.Clamp01(oldSecondsTaken / oldSecondsToTake);

                planeSecondsToTakeField.SetValue(plane, newSecondsToTake);

                if (planeSecondsTakenField != null)
                    planeSecondsTakenField.SetValue(plane, newSecondsToTake * progress);

                if (config.Debug)
                {
                    Puts("Signal plane accelerated: distance=" +
                         distance.ToString("0") +
                         "m, oldTime=" +
                         oldSecondsToTake.ToString("0.0") +
                         "s, newTime=" +
                         newSecondsToTake.ToString("0.0") +
                         "s.");
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Failed to accelerate signal plane: " + ex.Message);
            }
        }

        private void ApplyCrateResistance(SupplyDrop supplyDrop)
        {
            Rigidbody body = supplyDrop.GetComponent<Rigidbody>();

            if (body == null)
            {
                PrintWarning("SupplyDrop Rigidbody was not found.");
                return;
            }

            if (!SetRigidbodyDamping(body, config.CrateAirResistance))
            {
                PrintWarning("Could not set SupplyDrop air resistance on this Unity build.");
                return;
            }

            if (config.Debug)
            {
                Puts("Signal crate air resistance set to " +
                     config.CrateAirResistance.ToString("0.##") + ".");
            }
        }

        private bool SetRigidbodyDamping(Rigidbody body, float value)
        {
            try
            {
                if (rigidbodyDragProperty != null && rigidbodyDragProperty.CanWrite)
                {
                    rigidbodyDragProperty.SetValue(body, value, null);
                    return true;
                }

                if (rigidbodyLinearDampingProperty != null && rigidbodyLinearDampingProperty.CanWrite)
                {
                    rigidbodyLinearDampingProperty.SetValue(body, value, null);
                    return true;
                }

                if (rigidbodyDragField != null)
                {
                    rigidbodyDragField.SetValue(body, value);
                    return true;
                }

                if (rigidbodyLinearDampingField != null)
                {
                    rigidbodyLinearDampingField.SetValue(body, value);
                    return true;
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Rigidbody damping error: " + ex.Message);
            }

            return false;
        }
    }
}
