using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RevivalMod.Constants;
using EFT.InventoryLogic;
using UnityEngine;
using EFT.Communications;
using Comfort.Common;
using RevivalMod.Helpers;
using RevivalMod.Fika;
using RevivalMod.Components;
using EFT.UI;
using EFT.UI.BattleTimer;

namespace RevivalMod.Features
{
    /// <summary>
    /// Implements a second-chance mechanic for players, allowing them to enter a critical state
    /// instead of dying, and use a defibrillator to revive.
    /// </summary>
    internal class RevivalFeatures : ModulePatch
    {
        #region Constants

        // Player movement and behavior constants
        private const float MOVEMENT_SPEED_MULTIPLIER = 0.1f;
        private const bool FORCE_CROUCH_DURING_INVULNERABILITY = false;
        private const bool DISABLE_SHOOTING_DURING_INVULNERABILITY = false;

        // Notification timing constants
        private const float NOTIFICATION_INTERVAL = 15f;
        private const float CRITICAL_NOTIFICATION_INTERVAL = 30f;
        private const float URGENT_NOTIFICATION_INTERVAL = 10f;
        private const float CRITICAL_URGENT_THRESHOLD = 30f;
        private const float PROGRESS_NOTIFICATION_INTERVAL = 0.25f;

        // Visual effect constants
        private const float FLASH_INTERVAL = 0.5f;

        #endregion

        #region State Tracking

        // Timers and state for notifications
        private static float _notificationTimer = 0f;
        private static float _progressNotificationTimer = 0f;

        // Key holding tracking
        private static readonly Dictionary<KeyCode, float> _selfRevivalKeyHoldDuration = new Dictionary<KeyCode, float>();
        private static readonly Dictionary<KeyCode, float> _teamRevivalKeyHoldDuration = new Dictionary<KeyCode, float>();
        private static readonly Dictionary<string, string> _currentRevivalTargets = new Dictionary<string, string>();

        // Player state dictionaries
        private static readonly Dictionary<string, long> _lastRevivalTimesByPlayer = new Dictionary<string, long>();
        private static readonly Dictionary<string, bool> _playerInCriticalState = new Dictionary<string, bool>();
        private static readonly Dictionary<string, EDamageType> _playerDamageTypes = new Dictionary<string, EDamageType>();
        private static readonly Dictionary<string, bool> _playerIsInvulnerable = new Dictionary<string, bool>();
        private static readonly Dictionary<string, float> _playerInvulnerabilityTimers = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _playerCriticalStateTimers = new Dictionary<string, float>();

        // Player original state storage for restoration
        private static readonly Dictionary<string, float> _originalAwareness = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _originalMovementSpeed = new Dictionary<string, float>();

        // Multiplayer revival tracking
        private static readonly Dictionary<string, bool> _revivablePlayers = new Dictionary<string, bool>();

        // Reference to local player
        private static Player PlayerClient { get; set; }

        // Track overriding kill behavior
        public static readonly Dictionary<string, bool> KillOverridePlayers = new Dictionary<string, bool>();

        #endregion
        public static CustomTimer criticalStateMainTimer;

        private static CustomTimer selfRevivalTimer;
        private static CustomTimer criticalStateTimer;

        #region Core Patch Implementation

        protected override MethodBase GetTargetMethod()
        {
            // Patch the Update method of Player to check for revival and manage states
            return AccessTools.Method(typeof(Player), nameof(Player.UpdateTick));
        }

        [PatchPostfix]
        static void Postfix(Player __instance)
        {
            try
            {
                string playerId = __instance.ProfileId;
                PlayerClient = __instance;

                // Only process for the local player
                if (!__instance.IsYourPlayer)
                    return;

                // Process player states
                ProcessInvulnerabilityState(__instance, playerId);
                ProcessCriticalState(__instance, playerId);
                CheckForTeammateRevival(__instance);

                // Send position updates if in critical state (regardless of local player status)
                if (IsPlayerInCriticalState(playerId))
                {
                    // Send position update on every tick for critical players
                    FikaBridge.SendPlayerPositionPacket(playerId, new DateTime(), __instance.Position);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in RevivalFeatures patch: {ex.Message}");
            }
        }

        #endregion

        #region State Processing Methods

        /// <summary>
        /// Processes invulnerability state and timer for a player
        /// </summary>
        private static void ProcessInvulnerabilityState(Player player, string playerId)
        {
            if (!_playerIsInvulnerable.TryGetValue(playerId, out bool isInvulnerable) || !isInvulnerable)
                return;

            if (_playerInvulnerabilityTimers.TryGetValue(playerId, out float timer))
            {
                timer -= Time.deltaTime;
                _playerInvulnerabilityTimers[playerId] = timer;

                // Apply invulnerability restrictions
                ApplyInvulnerabilityRestrictions(player);

                // End invulnerability if timer is up
                if (timer <= 0)
                {
                    EndInvulnerability(player);
                }
            }
        }

        /// <summary>
        /// Applies movement and action restrictions during invulnerability period
        /// </summary>
        private static void ApplyInvulnerabilityRestrictions(Player player)
        {
            // Force player to crouch during invulnerability if enabled
            if (FORCE_CROUCH_DURING_INVULNERABILITY && player.MovementContext.PoseLevel > 0)
            {
                player.MovementContext.SetPoseLevel(0);
            }

            // Disable shooting during invulnerability if enabled
            if (DISABLE_SHOOTING_DURING_INVULNERABILITY && player.HandsController.IsAiming)
            {
                player.HandsController.IsAiming = false;
            }
        }

        /// <summary>
        /// Processes critical state and checks for revival inputs
        /// </summary>
        private static void ProcessCriticalState(Player player, string playerId)
        {
            if (!_playerInCriticalState.TryGetValue(playerId, out bool inCritical) || !inCritical)
                return;

            if (!_playerCriticalStateTimers.TryGetValue(playerId, out float criticalTimer))
                return;

            criticalTimer -= Time.deltaTime;
            _playerCriticalStateTimers[playerId] = criticalTimer;

            // Update the main critical state timer
            if (criticalStateMainTimer != null)
            {
                criticalStateMainTimer.Update();
            }

            // If time runs out, player dies
            if (criticalTimer <= 0)
            {
                if (criticalStateMainTimer != null)
                {
                    criticalStateMainTimer.StopTimer();
                    criticalStateMainTimer = null;
                }

                ForcePlayerDeath(player);
                return;
            }

            // Check for self-revival if player has the item
            CheckForSelfRevival(player, criticalTimer);

            // Display countdown notifications - now less frequent since we have UI timer
            DisplayCriticalStateCountdown(criticalTimer);

            // Check for "give up" key press
            if (Input.GetKeyDown(Settings.GIVE_UP_KEY.Value))
            {
                if (criticalStateMainTimer != null)
                {
                    criticalStateMainTimer.StopTimer();
                    criticalStateMainTimer = null;
                }

                ForcePlayerDeath(player);
            }
        }

        /// <summary>
        /// Checks if self-revival is possible and processes key input with hold-to-revive behavior
        /// </summary>
        /// 

        private static void CheckForSelfRevival(Player player, float remainingTime)
        {
            var revivalItemCheck = CheckRevivalItemInRaidInventory();
            if (revivalItemCheck.Value && Settings.SELF_REVIVAL_ENABLED.Value)
            {
                KeyCode revivalKey = Settings.SELF_REVIVAL_KEY.Value;

                // Start key hold tracking when key is first pressed
                if (Input.GetKeyDown(revivalKey))
                {
                    _selfRevivalKeyHoldDuration[revivalKey] = 0f;

                    // Initialize timers
                    selfRevivalTimer = new CustomTimer();
                    selfRevivalTimer.StartCountdown(Settings.REVIVAL_HOLD_DURATION.Value, "Self Revival Timer");

                }

                // Update hold duration while key is held
                if (Input.GetKey(revivalKey) && _selfRevivalKeyHoldDuration.ContainsKey(revivalKey))
                {

                    _selfRevivalKeyHoldDuration[revivalKey] += Time.deltaTime;
                    float holdDuration = _selfRevivalKeyHoldDuration[revivalKey];
                    float requiredDuration = Settings.REVIVAL_HOLD_DURATION.Value;           

                    // Trigger revival when key is held long enough
                    if (holdDuration >= requiredDuration)
                    {
                        _selfRevivalKeyHoldDuration.Remove(revivalKey);

                        // Stop timers
                        if (selfRevivalTimer != null) selfRevivalTimer.StopTimer();

                        TryPerformManualRevival(player);
                    }
                }

                // Reset when key is released
                if (Input.GetKeyUp(revivalKey))
                {
                    if (_selfRevivalKeyHoldDuration.ContainsKey(revivalKey))
                    {
                        // Stop timers
                        if (selfRevivalTimer != null) selfRevivalTimer.StopTimer();

                        NotificationManagerClass.DisplayMessageNotification(
                            "Defibrillator use canceled",
                            ENotificationDurationType.Default,
                            ENotificationIconType.Default,
                            Color.red);

                        _selfRevivalKeyHoldDuration.Remove(revivalKey);
                    }
                }
            }
        }

        /// <summary>
        /// Displays countdown notifications for critical state
        /// </summary>
        private static void DisplayCriticalStateCountdown(float criticalTimer)
        {
            return;
            // Regular interval notifications
            if (criticalTimer % CRITICAL_NOTIFICATION_INTERVAL < 0.1f && criticalTimer > 0.5f)
            {
                NotificationManagerClass.DisplayMessageNotification(
                    $"Critical state: {(int)criticalTimer} seconds remaining to be revived",
                    ENotificationDurationType.Default,
                    ENotificationIconType.Alert,
                    Color.yellow);
            }
            // More frequent notifications in the last 30 seconds
            else if (criticalTimer <= CRITICAL_URGENT_THRESHOLD && criticalTimer % URGENT_NOTIFICATION_INTERVAL < 0.1f)
            {
                NotificationManagerClass.DisplayMessageNotification(
                    $"URGENT: {(int)criticalTimer} seconds remaining before death",
                    ENotificationDurationType.Default,
                    ENotificationIconType.Alert,
                    Color.red);
            }
        }

        /// <summary>
        /// Checks for nearby critical teammates and processes revival actions with hold-to-revive behavior
        /// </summary>

        private static void CheckForTeammateRevival(Player player)
        {
            var revivalItemCheck = CheckRevivalItemInRaidInventory();
            if (!revivalItemCheck.Value)
                return;

            // Get player position
            string playerId = player.ProfileId;
            Vector3 currentPos = player.Position;

            // Process each critical player
            foreach (KeyValuePair<string, Vector3> critPlayer in RMSession.GetCriticalPlayers())
            {
                // Using squared magnitude for performance (avoids square root calculation)
                if ((currentPos - critPlayer.Value).sqrMagnitude <= 4f &&
                    (!_revivablePlayers.ContainsKey(critPlayer.Key) || !_revivablePlayers[critPlayer.Key]))
                {
                    // Show notification about nearby critical player
                    _notificationTimer -= Time.deltaTime;
                    if (_notificationTimer <= 0)
                    {
                        _notificationTimer = NOTIFICATION_INTERVAL;
                        NotificationManagerClass.DisplayMessageNotification(
                            $"Press and hold {Settings.TEAM_REVIVAL_KEY.Value} to use your defibrillator to revive your teammate!",
                            ENotificationDurationType.Long,
                            ENotificationIconType.Friend,
                            Color.green);
                        Plugin.LogSource.LogDebug($"Player with id {player.ProfileId} is within 2m of critplayer with id {critPlayer.Key}");
                    }

                    KeyCode teamRevivalKey = Settings.TEAM_REVIVAL_KEY.Value;

                    // Start key hold tracking when key is first pressed
                    if (Input.GetKeyDown(teamRevivalKey))
                    {
                        _teamRevivalKeyHoldDuration[teamRevivalKey] = 0f;
                        _currentRevivalTargets[playerId] = critPlayer.Key;

                        // Initialize timers
                        criticalStateTimer = new CustomTimer();
                        criticalStateTimer.StartCountdown(Settings.TEAM_REVIVAL_HOLD_DURATION.Value, "Reviving Teammate");
                    }

                    // Update hold duration while key is held
                    if (Input.GetKey(teamRevivalKey) && _teamRevivalKeyHoldDuration.ContainsKey(teamRevivalKey) &&
                        _currentRevivalTargets.ContainsKey(playerId) && _currentRevivalTargets[playerId] == critPlayer.Key)
                    {
                        // Update both timers
                        criticalStateTimer.Update();

                        _teamRevivalKeyHoldDuration[teamRevivalKey] += Time.deltaTime;
                        float holdDuration = _teamRevivalKeyHoldDuration[teamRevivalKey];
                        float requiredDuration = Settings.TEAM_REVIVAL_HOLD_DURATION.Value;

                        // Show progress notifications
                        _progressNotificationTimer -= Time.deltaTime;
                        if (_progressNotificationTimer <= 0)
                        {
                            _progressNotificationTimer = PROGRESS_NOTIFICATION_INTERVAL;
                            int progressPercent = Mathf.RoundToInt((holdDuration / requiredDuration) * 100f);
                            progressPercent = Mathf.Clamp(progressPercent, 0, 100);

                           
                        }

                        // Trigger revival when key is held long enough
                        if (holdDuration >= requiredDuration)
                        {
                            string targetId = _currentRevivalTargets[playerId];
                            _teamRevivalKeyHoldDuration.Remove(teamRevivalKey);
                            _currentRevivalTargets.Remove(playerId);

                            // Stop timers
                            criticalStateTimer.StopTimer();

                            PerformTeammateRevival(targetId, player);
                        }
                    }

                    // Reset when key is released
                    if (Input.GetKeyUp(teamRevivalKey))
                    {
                        if (_teamRevivalKeyHoldDuration.ContainsKey(teamRevivalKey))
                        {
                            // Stop timers
                            if (criticalStateTimer != null) criticalStateTimer.StopTimer();

                            NotificationManagerClass.DisplayMessageNotification(
                                "Revival canceled",
                                ENotificationDurationType.Default,
                                ENotificationIconType.Friend,
                                Color.red);

                            _teamRevivalKeyHoldDuration.Remove(teamRevivalKey);
                            _currentRevivalTargets.Remove(playerId);
                        }
                    }
                }
            }
        }

        #endregion

        #region Public API Methods

        /// <summary>
        /// Checks if a player is currently in critical state
        /// </summary>
        public static bool IsPlayerInCriticalState(string playerId)
        {
            return _playerInCriticalState.TryGetValue(playerId, out bool inCritical) && inCritical;
        }

        /// <summary>
        /// Checks if a player is currently invulnerable
        /// </summary>
        public static bool IsPlayerInvulnerable(string playerId)
        {
            return _playerIsInvulnerable.TryGetValue(playerId, out bool invulnerable) && invulnerable;
        }

        /// <summary>
        /// Sets a player's critical state status
        /// </summary>
        public static void SetPlayerCriticalState(Player player, bool criticalState, EDamageType damageType)
        {
            if (player == null)
                return;

            string playerId = player.ProfileId;

            // Update critical state
            _playerInCriticalState[playerId] = criticalState;
            _playerDamageTypes[playerId] = damageType;

            if (criticalState)
            {
                InitializeCriticalState(player, playerId);
            }
            else
            {
                CleanupCriticalState(player, playerId);
            }
        }

        /// <summary>
        /// Attempts to perform revival of player by a teammate
        /// </summary>
        public static bool TryPerformRevivalByTeammate(string playerId)
        {
            if (playerId != Singleton<GameWorld>.Instance.MainPlayer.ProfileId)
                return false;

            Player player = Singleton<GameWorld>.Instance.MainPlayer;

            try
            {
                // Apply revival effects
                ApplyRevivalEffects(player);

                // Apply invulnerability
                StartInvulnerability(player);

                // Reset critical state
                _playerInCriticalState[playerId] = false;

                // Set last revival time
                _lastRevivalTimesByPlayer[playerId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Show successful revival notification
                NotificationManagerClass.DisplayMessageNotification(
                    "Defibrillator used successfully! You are temporarily invulnerable but limited in movement.",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Default,
                    Color.green);
                criticalStateMainTimer.StopTimer();
                criticalStateMainTimer = null;

                Plugin.LogSource.LogInfo($"Team revival performed for player {playerId}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in teammate revival: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Checks if the player has a revival item in their inventory
        /// </summary>
        public static KeyValuePair<string, bool> CheckRevivalItemInRaidInventory()
        {
            try
            {
                // Get player reference if needed
                if (PlayerClient == null)
                {
                    if (Singleton<GameWorld>.Instantiated)
                    {
                        PlayerClient = Singleton<GameWorld>.Instance.MainPlayer;
                    }
                    else
                    {
                        return new KeyValuePair<string, bool>(string.Empty, false);
                    }
                }

                if (PlayerClient == null)
                {
                    return new KeyValuePair<string, bool>(string.Empty, false);
                }

                // Check for revival item
                string playerId = PlayerClient.ProfileId;
                var inRaidItems = PlayerClient.Inventory.GetPlayerItems(EPlayerItems.Equipment);
                bool hasItem = inRaidItems.Any(item => item.TemplateId == Constants.Constants.ITEM_ID);

                return new KeyValuePair<string, bool>(playerId, hasItem);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error checking revival item: {ex.Message}");
                return new KeyValuePair<string, bool>(string.Empty, false);
            }
        }

        #endregion

        #region Revival Implementation

        /// <summary>
        /// Initializes critical state for a player
        /// </summary>

        private static void InitializeCriticalState(Player player, string playerId)
        {
            // Set the critical state timer
            _playerCriticalStateTimers[playerId] = Settings.TIME_TO_REVIVE.Value;
            _playerIsInvulnerable[playerId] = true;

            // Apply effects and make player revivable
            ApplyCriticalEffects(player);
            ApplyRevivableState(player);

            // Show UI notification for local player
            if (player.IsYourPlayer)
            {
                DisplayCriticalStateNotification(player);

                // Create a countdown timer for critical state
                criticalStateMainTimer = new CustomTimer();
                criticalStateMainTimer.StartCountdown(Settings.TIME_TO_REVIVE.Value, "Critical State Timer", TimerPosition.MiddleCenter);
            }

            // Send initial position packet for multiplayer sync
            FikaBridge.SendPlayerPositionPacket(playerId, new DateTime(), player.Position);
        }


        /// <summary>
        /// Displays critical state notification with available options
        /// </summary>
        private static void DisplayCriticalStateNotification(Player player)
        {
            try
            {
                // Build notification message
                string message = "CRITICAL CONDITION!\n";

                if (Settings.SELF_REVIVAL_ENABLED.Value && CheckRevivalItemInRaidInventory().Value)
                {
                    message += $"Hold {Settings.SELF_REVIVAL_KEY.Value} for {Settings.REVIVAL_HOLD_DURATION.Value}s to use defibrillator\n";
                }

                message += $"Press {Settings.GIVE_UP_KEY.Value} to give up\n";
                message += $"Or wait for a teammate to revive you ({(int)Settings.TIME_TO_REVIVE.Value} seconds)";

                NotificationManagerClass.DisplayMessageNotification(
                    message,
                    ENotificationDurationType.Long,
                    ENotificationIconType.Default,
                    Color.red);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error displaying critical state UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up critical state when exiting that state
        /// </summary>
        private static void CleanupCriticalState(Player player, string playerId)
        {
            // Remove from critical state timer tracking
            _playerCriticalStateTimers.Remove(playerId);

            // Stop the main critical state timer
            if (criticalStateMainTimer != null)
            {
                criticalStateMainTimer.StopTimer();
                criticalStateMainTimer = null;
            }

            // If player is leaving critical state without revival, clean up
            if (!_playerInvulnerabilityTimers.ContainsKey(playerId))
            {
                RemoveRevivableState(player);
                _playerIsInvulnerable.Remove(playerId);
                RestorePlayerMovement(player);
            }
        }

        /// <summary>
        /// Attempts to perform manual revival
        /// </summary>
        public static bool TryPerformManualRevival(Player player)
        {
            if (player == null)
                return false;

            string playerId = player.ProfileId;

            // Check if the player has the revival item
            bool hasDefib = CheckRevivalItemInRaidInventory().Value;

            // Check if revival is on cooldown
            if (IsRevivalOnCooldown(playerId))
                return false;

            if (hasDefib || Settings.TESTING.Value)
            {
                return PerformRevival(player, playerId, hasDefib);
            }
            else
            {
                NotificationManagerClass.DisplayMessageNotification(
                    "No defibrillator found! Unable to revive!",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Alert,
                    Color.red);

                return false;
            }
        }

        /// <summary>
        /// Checks if revival is on cooldown and shows notification if needed
        /// </summary>
        private static bool IsRevivalOnCooldown(string playerId)
        {
            if (!_lastRevivalTimesByPlayer.TryGetValue(playerId, out long lastRevivalTime))
                return false;

            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool isOnCooldown = (currentTime - lastRevivalTime) < Settings.REVIVAL_COOLDOWN.Value;

            if (isOnCooldown)
            {
                // Only show notification if not in test mode
                if (!Settings.TESTING.Value)
                {
                    int remainingCooldown = (int)(Settings.REVIVAL_COOLDOWN.Value - (currentTime - lastRevivalTime));
                    NotificationManagerClass.DisplayMessageNotification(
                        $"Revival on cooldown! Available in {remainingCooldown} seconds",
                        ENotificationDurationType.Long,
                        ENotificationIconType.Alert,
                        Color.yellow);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Performs the actual revival process
        /// </summary>
        private static bool PerformRevival(Player player, string playerId, bool hasDefib)
        {
            // Consume the item if not in test mode
            if (hasDefib && !Settings.TESTING.Value)
            {
                ConsumeDefibItem(player);
            }

            // Remove from critical players list for multiplayer sync
            FikaBridge.SendRemovePlayerFromCriticalPlayersListPacket(playerId);

            // Apply revival effects with limited healing
            ApplyRevivalEffects(player);

            // Apply invulnerability period
            StartInvulnerability(player);

            player.Say(EPhraseTrigger.OnMutter, false, 2f, ETagStatus.Combat, 100, true);

            // Reset critical state
            _playerInCriticalState[playerId] = false;

            // Set last revival time
            _lastRevivalTimesByPlayer[playerId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            criticalStateMainTimer.StopTimer();
            criticalStateMainTimer = null;
            // Show successful revival notification
            NotificationManagerClass.DisplayMessageNotification(
                "Defibrillator used successfully! You are temporarily invulnerable but limited in movement.",
                ENotificationDurationType.Long,
                ENotificationIconType.Default,
                Color.green);

            Plugin.LogSource.LogInfo($"Manual revival performed for player {playerId}");
            return true;
        }

        /// <summary>
        /// Revives a teammate by a player with a defibrillator
        /// </summary>
        public static bool PerformTeammateRevival(string targetPlayerId, Player reviver)
        {
            try
            {
                NotificationManagerClass.DisplayMessageNotification(
                   "Reviving teammate...",
                   ENotificationDurationType.Default,
                   ENotificationIconType.Friend,
                   Color.green);

                ConsumeDefibItem(reviver);
                RMSession.RemovePlayerFromCriticalPlayers(targetPlayerId);

                FikaBridge.SendRemovePlayerFromCriticalPlayersListPacket(targetPlayerId);
                FikaBridge.SendReviveMePacket(targetPlayerId, reviver.ProfileId);

                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in teammate revival: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Consumes a defibrillator item from the player's inventory
        /// </summary>
        private static void ConsumeDefibItem(Player player)
        {
            try
            {
                var inRaidItems = player.Inventory.GetPlayerItems(EPlayerItems.Equipment);
                Item defibItem = inRaidItems.FirstOrDefault(item => item.TemplateId == Constants.Constants.ITEM_ID);

                if (defibItem == null)
                {
                    Plugin.LogSource.LogWarning("No defibrillator item found to consume");
                    return;
                }

                Plugin.LogSource.LogDebug($"Found defib item: {defibItem.TemplateId}");

                // Deplete the item and remove it
                defibItem.GetItemComponent<MedKitComponent>().HpResource = 0f;
                ItemAddress itemAddress = defibItem.GetItemComponent<MedKitComponent>().Item.CurrentAddress;
                itemAddress.RemoveWithoutRestrictions(defibItem);

                // Use the item through UI context
                ItemUiContext context = ItemUiContext.Instance;
                context.UseAll(defibItem);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error consuming defib item: {ex.Message}");
            }
        }

        #endregion

        #region Player Status Effects

        /// <summary>
        /// Applies critical state effects to player
        /// </summary>
        private static void ApplyCriticalEffects(Player player)
        {
            try
            {
                string playerId = player.ProfileId;

                // Store original movement speed if not already stored
                if (!_originalMovementSpeed.ContainsKey(playerId))
                {
                    _originalMovementSpeed[playerId] = player.Physical.WalkSpeedLimit;
                }

                // Apply visual and movement effects
                player.ActiveHealthController.DoContusion(Settings.REVIVAL_DURATION.Value, 1f);
                player.ActiveHealthController.DoStun(Settings.REVIVAL_DURATION.Value / 2, 1f);

                // Severely restrict movement
                player.Physical.WalkSpeedLimit = MOVEMENT_SPEED_MULTIPLIER;

                // Force player to crouch
                if (player.MovementContext != null)
                {
                    player.MovementContext.SetPoseLevel(0f, true);
                    player.ResetLookDirection();
                    player.ActiveHealthController.SetStaminaCoeff(0f);
                }

                Plugin.LogSource.LogDebug($"Applied critical effects to player {playerId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error applying critical effects: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores player movement capabilities after critical/invulnerable state
        /// </summary>
        private static void RestorePlayerMovement(Player player)
        {
            try
            {
                string playerId = player.ProfileId;

                // Restore original movement speed if we stored it
                if (_originalMovementSpeed.TryGetValue(playerId, out float originalSpeed))
                {
                    player.Physical.WalkSpeedLimit = originalSpeed;
                    _originalMovementSpeed.Remove(playerId);
                }

                // Reset pose to standing
                player.MovementContext.SetPoseLevel(1f);

                Plugin.LogSource.LogDebug($"Restored movement for player {playerId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error restoring player movement: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes the player enter a revivable state where AI ignores them
        /// </summary>
        private static void ApplyRevivableState(Player player)
        {
            try
            {
                string playerId = player.ProfileId;

                // Skip if already applied
                if (_originalAwareness.ContainsKey(playerId))
                    return;

                // Store original awareness value
                _originalAwareness[playerId] = player.Awareness;

                // Configure player for revivable state
                player.Awareness = 0f;
                player.PlayDeathSound();
                player.HandsController.IsAiming = false;
                player.MovementContext.EnableSprint(false);
                player.MovementContext.SetPoseLevel(0f, true);
                player.MovementContext.IsInPronePose = true;
                player.SetEmptyHands(null);
                player.ActiveHealthController.IsAlive = false;

                GClass3756.ReleaseBeginSample("Player.OnDead.SoundWork", "OnDead");
                if (player.ShouldVocalizeDeath(player.LastDamagedBodyPart))
                {
                    EPhraseTrigger trigger = player.LastDamageType.IsWeaponInduced() ? EPhraseTrigger.OnDeath : EPhraseTrigger.OnAgony;
                    try
                    {
                        player.Speaker.Play(trigger, player.HealthStatus, true, null);
                        goto IL_12F;
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError(ex.Message);
                        goto IL_12F;
                    }
                }
            IL_12F:
                player.MovementContext.ReleaseDoorIfInteractingWithOne();
                player.MovementContext.OnStateChanged -= player.method_17;
                player.MovementContext.PhysicalConditionChanged -= player.ProceduralWeaponAnimation.PhysicalConditionUpdated;
                //player.HandsController.OnPlayerDead();

                if (player.MovementContext.StationaryWeapon != null)
                {
                    player.MovementContext.StationaryWeapon.Unlock(player.ProfileId);
                }
                if (player.MovementContext.StationaryWeapon != null && player.MovementContext.StationaryWeapon.Item == player.HandsController.Item)
                {
                    player.MovementContext.StationaryWeapon.Show();
                    player.ReleaseHand();
                    return;
                }

                // Use reflection to access protected members to create and assign a corpse
                try
                {
                    // Get the protected Corpse property
                    PropertyInfo corpseProperty = typeof(Player).GetProperty("Corpse",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    // Get the protected CreateCorpse method
                    MethodInfo createCorpseMethod = typeof(Player).GetMethod("CreateCorpse",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    if (corpseProperty != null && createCorpseMethod != null)
                    {
                        // Invoke the CreateCorpse method on the player instance
                        object corpse = createCorpseMethod.Invoke(player, null);

                        // Set the Corpse property with the new corpse
                        corpseProperty.SetValue(player, corpse);

                        Plugin.LogSource.LogDebug($"Created and assigned corpse for player {playerId}");
                    }
                    else
                    {
                        Plugin.LogSource.LogWarning("Could not find Corpse property or CreateCorpse method via reflection");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"Error creating corpse: {ex.Message}");
                }

                // Send initial position for multiplayer sync
                FikaBridge.SendPlayerPositionPacket(playerId, new DateTime(), player.Position);

                Plugin.LogSource.LogDebug($"Applied revivable state to player {playerId}");
                Plugin.LogSource.LogDebug($"Revivable State Variables - Awareness: {player.Awareness}, IsAlive: {player.ActiveHealthController.IsAlive}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error applying revivable state: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the revivable state from a player
        /// </summary>
        private static void RemoveRevivableState(Player player)
        {
            try
            {
                string playerId = player.ProfileId;
                if (!_originalAwareness.ContainsKey(playerId))
                    return;

                // Restore awareness and visibility
                player.Awareness = _originalAwareness[playerId];
                _originalAwareness.Remove(playerId);

                player.IsVisible = true;
                player.ActiveHealthController.IsAlive = true;
                player.ActiveHealthController.DoContusion(25f, 0.25f);
                player.Awareness = 350f;

                Plugin.LogSource.LogInfo($"Removed revivable state from player {playerId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error removing revivable state: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies revival effects to the player with limited healing
        /// </summary>
        private static void ApplyRevivalEffects(Player player)
        {
            try
            {
                ActiveHealthController healthController = player.ActiveHealthController;
                if (healthController == null)
                {
                    Plugin.LogSource.LogError("Could not get ActiveHealthController");
                    return;
                }

                // Restore destroyed body parts if setting enabled and not in hardcore mode
                if (!Settings.HARDCORE_MODE.Value && Settings.RESTORE_DESTROYED_BODY_PARTS.Value)
                {
                    foreach (EBodyPart bodyPart in Enum.GetValues(typeof(EBodyPart)))
                    {
                        Plugin.LogSource.LogDebug($"{bodyPart} is at {healthController.GetBodyPartHealth(bodyPart).Current} health");
                        if (healthController.GetBodyPartHealth(bodyPart).Current < 1)
                        {
                            healthController.FullRestoreBodyPart(bodyPart);
                            Plugin.LogSource.LogDebug($"Restored {bodyPart}");
                        }
                    }
                }

                // Apply disorientation effects
                healthController.DoContusion(Settings.REVIVAL_DURATION.Value, 1f);
                healthController.DoStun(Settings.REVIVAL_DURATION.Value / 2, 1f);

                Plugin.LogSource.LogInfo("Applied revival effects to player");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error applying revival effects: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts invulnerability period for a player
        /// </summary>
        private static void StartInvulnerability(Player player)
        {
            if (player == null)
                return;

            string playerId = player.ProfileId;
            _playerIsInvulnerable[playerId] = true;
            _playerInvulnerabilityTimers[playerId] = Settings.REVIVAL_DURATION.Value;

            // Apply movement restrictions
            ApplyCriticalEffects(player);

            // Start visual effect
            player.StartCoroutine(FlashInvulnerabilityEffect(player));

            Plugin.LogSource.LogInfo($"Started invulnerability for player {playerId} for {Settings.REVIVAL_DURATION.Value} seconds");
        }

        /// <summary>
        /// Ends invulnerability period for a player
        /// </summary>
        private static void EndInvulnerability(Player player)
        {
            if (player == null)
                return;

            string playerId = player.ProfileId;
            _playerIsInvulnerable[playerId] = false;
            _playerInvulnerabilityTimers.Remove(playerId);

            // Remove stealth from player
            RemoveRevivableState(player);

            // Remove movement restrictions
            RestorePlayerMovement(player);

            // Show notification
            if (player.IsYourPlayer)
            {
                NotificationManagerClass.DisplayMessageNotification(
                    "Temporary invulnerability has ended",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Default,
                    Color.white);
            }

            Plugin.LogSource.LogInfo($"Ended invulnerability for player {playerId}");
        }

        /// <summary>
        /// Visual effect coroutine that makes player model flash during invulnerability
        /// </summary>
        private static IEnumerator FlashInvulnerabilityEffect(Player player)
        {
            string playerId = player.ProfileId;
            bool isVisible = true;

            // Store original visibility states of all renderers
            Dictionary<Renderer, bool> originalStates = new Dictionary<Renderer, bool>();

            // First ensure player is visible to start
            if (player.PlayerBody != null && player.PlayerBody.BodySkins != null)
            {
                foreach (var kvp in player.PlayerBody.BodySkins)
                {
                    if (kvp.Value != null)
                    {
                        var renderers = kvp.Value.GetComponentsInChildren<Renderer>(true);
                        foreach (var renderer in renderers)
                        {
                            if (renderer != null)
                            {
                                originalStates[renderer] = renderer.enabled;
                                renderer.enabled = true;
                            }
                        }
                    }
                }
            }

            // Flash the player model while invulnerable
            while (_playerIsInvulnerable.TryGetValue(playerId, out bool isInvulnerable) && isInvulnerable)
            {
                try
                {
                    isVisible = !isVisible; // Toggle visibility

                    // Apply visibility to all renderers in the player model
                    if (player.PlayerBody != null && player.PlayerBody.BodySkins != null)
                    {
                        foreach (var kvp in player.PlayerBody.BodySkins)
                        {
                            if (kvp.Value != null)
                            {
                                var renderers = kvp.Value.GetComponentsInChildren<Renderer>(true);
                                foreach (var renderer in renderers)
                                {
                                    if (renderer != null)
                                    {
                                        renderer.enabled = isVisible;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"Error in flash effect: {ex.Message}");
                }

                yield return new WaitForSeconds(FLASH_INTERVAL);
            }

            // Ensure player is visible when effect ends
            try
            {
                foreach (var kvp in originalStates)
                {
                    if (kvp.Key != null)
                    {
                        kvp.Key.enabled = true; // Force visibility on exit
                    }
                }
            }
            catch
            {
                // Fallback if the dictionary approach fails
                if (player.PlayerBody != null && player.PlayerBody.BodySkins != null)
                {
                    foreach (var kvp in player.PlayerBody.BodySkins)
                    {
                        if (kvp.Value != null)
                        {
                            kvp.Value.EnableRenderers(true);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Forces player death when revival fails or time runs out
        /// </summary>
        private static void ForcePlayerDeath(Player player)
        {
            try
            {
                string playerId = player.ProfileId;

                // Add to override list first (before any other operations)
                KillOverridePlayers[playerId] = true;

                // Clean up all state tracking for this player
                _playerIsInvulnerable[playerId] = false;
                _playerInvulnerabilityTimers.Remove(playerId);
                _playerInCriticalState[playerId] = false;
                _playerCriticalStateTimers.Remove(playerId);

                // Remove player from critical players list for network sync
                RMSession.RemovePlayerFromCriticalPlayers(playerId);
                FikaBridge.SendRemovePlayerFromCriticalPlayersListPacket(playerId);
                criticalStateMainTimer.StopTimer();
                // Show notification about death
                NotificationManagerClass.DisplayMessageNotification(
                    "You have died",
                    ENotificationDurationType.Long,
                    ENotificationIconType.Alert,
                    Color.red);

                // Get the damage type that initially caused critical state
                EDamageType damageType = _playerDamageTypes.TryGetValue(playerId, out var type) ?
                    type : EDamageType.Bullet;

                // Use original Kill method, bypassing our patch
                player.ActiveHealthController.IsAlive = true;
                player.ActiveHealthController.Kill(damageType);               
                Plugin.LogSource.LogInfo($"Player {playerId} has died after critical state");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error forcing player death: {ex.Message}");
            }
        }

        #endregion
    }
}