using System.Diagnostics;
using System.Threading;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Tools;
using Game.Vehicles;
using Unity.Entities;
using UnityEngine.InputSystem;

namespace ServiceCoverageFix
{
    /// <summary>
    /// Runtime-only diagnostic controls. This system deliberately uses hard-to-hit
    /// key combinations so the destructive test cannot be triggered accidentally.
    /// </summary>
    internal sealed partial class DiagnosticControlSystem : GameSystemBase
    {
        private const double DeleteHoldSeconds = 3.0;
        private static int _runtimeControlsEnabled;

        private EntityQuery _parkQuery;
        private EntityQuery _parkMaintenanceQuery;
        private EntityQuery _parkMaintenanceVehicleQuery;
        private bool _loggingComboWasDown;
        private bool _deleteTriggered;
        private long _deleteHoldStartedAt;

        protected override void OnCreate()
        {
            base.OnCreate();

            _parkQuery = CreateLiveEntityQuery(ComponentType.ReadOnly<Park>());
            _parkMaintenanceQuery =
                CreateLiveEntityQuery(ComponentType.ReadOnly<ParkMaintenance>());
            _parkMaintenanceVehicleQuery =
                CreateLiveEntityQuery(ComponentType.ReadOnly<ParkMaintenanceVehicle>());
        }

        protected override void OnUpdate()
        {
            if (Volatile.Read(ref _runtimeControlsEnabled) == 0)
            {
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            bool controlDown =
                keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            bool shiftDown =
                keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;

            bool loggingComboDown = controlDown && shiftDown && keyboard.f9Key.isPressed;
            if (loggingComboDown && !_loggingComboWasDown)
            {
                ApplyCoverageScheduleBridge.ToggleInputDiagnostics();
            }

            _loggingComboWasDown = loggingComboDown;

            bool deleteComboDown = controlDown && shiftDown && keyboard.f10Key.isPressed;
            UpdateDeleteHold(deleteComboDown);
        }

        internal static void SetRuntimeControlsEnabled(bool enabled)
        {
            Interlocked.Exchange(ref _runtimeControlsEnabled, enabled ? 1 : 0);
        }

        private EntityQuery CreateLiveEntityQuery(ComponentType requiredComponent)
        {
            return GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { requiredComponent },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
        }

        private void UpdateDeleteHold(bool comboDown)
        {
            if (!comboDown)
            {
                _deleteHoldStartedAt = 0;
                _deleteTriggered = false;
                return;
            }

            if (_deleteTriggered)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (_deleteHoldStartedAt == 0)
            {
                _deleteHoldStartedAt = now;
                Mod.Log.Warn(
                    "Park deletion hotkey armed. Continue holding Ctrl+Shift+F10 for 3 seconds " +
                    "to mark every park, park-maintenance facility, and active park-maintenance " +
                    "vehicle as deleted. Do not save over the original city.");
                return;
            }

            double heldSeconds =
                (now - _deleteHoldStartedAt) / (double)Stopwatch.Frequency;
            if (heldSeconds < DeleteHoldSeconds)
            {
                return;
            }

            _deleteTriggered = true;
            DeleteAllParksAndMaintenance();
        }

        private void DeleteAllParksAndMaintenance()
        {
            try
            {
                int parks = MarkDeleted(_parkQuery);
                int maintenanceFacilities = MarkDeleted(_parkMaintenanceQuery);
                int maintenanceVehicles = MarkDeleted(_parkMaintenanceVehicleQuery);

                Mod.Log.Warn(
                    "PARK DELETION TEST EXECUTED: " +
                    $"parks={parks}, parkMaintenanceFacilities={maintenanceFacilities}, " +
                    $"parkMaintenanceVehicles={maintenanceVehicles}. " +
                    "This change will become permanent if the city is saved.");
            }
            catch (System.Exception exception)
            {
                Mod.Log.Error(
                    $"Park deletion test failed before completion: {exception}");
            }
        }

        private int MarkDeleted(EntityQuery query)
        {
            int count = query.CalculateEntityCount();
            if (count != 0)
            {
                EntityManager.AddComponent(query, ComponentType.ReadWrite<Deleted>());
            }

            return count;
        }
    }
}
