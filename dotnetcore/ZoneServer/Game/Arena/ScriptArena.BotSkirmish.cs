using System;
using System.Collections.Generic;
using System.Linq;
using InfServer.Bots;
using InfServer.Protocol;
using Assets;

namespace InfServer.Game
{
    public partial class ScriptArena
    {
        private bool _botSkirmishEnabled;
        private bool _botSkirmishBootstrapped;
        private int _botSkirmishCount = 1;
        private int _botSkirmishRespawnDelayMs = 5000;
        private int _botSkirmishTeam = 9999;
        private int _botSkirmishVehicleId = 453;
        private string _botSkirmishNamePrefix = "Bot";
        private string _botSkirmishDifficulty = "Normal";
        private readonly Dictionary<ushort, int> _botSkirmishPendingRespawns = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, BotSkirmishBrainState> _botSkirmishBrain = new Dictionary<ushort, BotSkirmishBrainState>();
        private int _botSkirmishSequence;
        private int _botSkirmishReactionDelayMs = 350;
        private int _botSkirmishDetectRange = 1200;
        private int _botSkirmishAimJitter = 20;
        private bool _botSkirmishObjectiveEnabled;
        private short _botSkirmishObjectiveX;
        private short _botSkirmishObjectiveY;
        private int _botSkirmishObjectiveRadius = 220;

        private class BotSkirmishBrainState
        {
            public short LastX;
            public short LastY;
            public int LastMoveTick;
            public int NextDecisionTick;
            public byte DesiredYaw;
            public bool HasDesiredYaw;
            public int NextFireTick;
        }

        private void initBotSkirmishSupport()
        {
            _botSkirmishEnabled = _scriptType != null && _scriptType.Equals("GameType_BotSkirmish", StringComparison.OrdinalIgnoreCase);
            if (!_botSkirmishEnabled)
                return;

            try { _botSkirmishEnabled = _server._config["bots/enabled"].boolValue; } catch { }
            try { _botSkirmishCount = Math.Max(1, _server._config["bots/count"].intValue); } catch { }
            try { _botSkirmishTeam = _server._config["bots/team"].intValue; } catch { }
            try { _botSkirmishNamePrefix = _server._config["bots/namePrefix"].Value; } catch { }
            try { _botSkirmishRespawnDelayMs = Math.Max(500, _server._config["bots/respawnDelayMs"].intValue); } catch { }
            try { _botSkirmishDifficulty = _server._config["bots/difficulty"].Value; } catch { }
            try { _botSkirmishVehicleId = _server._config["bots/vehicleId"].intValue; } catch { }
            try { _botSkirmishObjectiveEnabled = _server._config["bots/objectiveEnabled"].boolValue; } catch { }
            try { _botSkirmishObjectiveX = (short)_server._config["bots/objectiveX"].intValue; } catch { }
            try { _botSkirmishObjectiveY = (short)_server._config["bots/objectiveY"].intValue; } catch { }
            try { _botSkirmishObjectiveRadius = Math.Max(80, _server._config["bots/objectiveRadius"].intValue); } catch { }

            applyDifficultyTuning();
            Log.write(TLog.Normal, $"[BotSkirmish] initialized enabled={_botSkirmishEnabled} count={_botSkirmishCount} team={_botSkirmishTeam} prefix={_botSkirmishNamePrefix} respawnMs={_botSkirmishRespawnDelayMs} difficulty={_botSkirmishDifficulty}");
        }

        private bool isBlockedAt(int x, int y)
        {
            if (x < 16 || y < 16 || x >= (_levelWidth * 16) - 16 || y >= (_levelHeight * 16) - 16)
                return true;

            return getTile(x, y).Blocked;
        }

        private bool shouldAvoidForwardTerrain(Bot bot)
        {
            const double yawToRad = Math.PI / 20.0;
            double angle = bot._state.yaw * yawToRad;
            int probeDistance = 42;
            int probeX = bot._state.positionX + (int)(Math.Cos(angle) * probeDistance);
            int probeY = bot._state.positionY + (int)(Math.Sin(angle) * probeDistance);
            return isBlockedAt(probeX, probeY);
        }

        private bool moveTowardObjective(Bot bot, BotSkirmishBrainState brain, int now)
        {
            if (!_botSkirmishObjectiveEnabled || (_botSkirmishObjectiveX == 0 && _botSkirmishObjectiveY == 0))
                return false;

            int dx = _botSkirmishObjectiveX - bot._state.positionX;
            int dy = _botSkirmishObjectiveY - bot._state.positionY;
            int distSq = (dx * dx) + (dy * dy);
            if (distSq <= _botSkirmishObjectiveRadius * _botSkirmishObjectiveRadius)
            {
                if (brain.NextDecisionTick <= now)
                {
                    brain.NextDecisionTick = now + _rand.Next(250, 600);
                    if (_rand.Next(0, 100) < 75)
                        bot._movement.stop();
                    else
                        bot._movement.thrustForward();
                }
                return true;
            }

            int targetYaw = (int)(Math.Atan2(dy, dx) * (20.0 / Math.PI));
            if (targetYaw < 0)
                targetYaw += 40;
            byte desiredYaw = (byte)(targetYaw % 40);

            if (bot._state.yaw != desiredYaw)
            {
                if (((byte)(desiredYaw - bot._state.yaw)) < 128)
                    bot._movement.rotateRight();
                else
                    bot._movement.rotateLeft();
            }

            if (shouldAvoidForwardTerrain(bot))
            {
                if (_rand.Next(0, 2) == 0)
                    bot._movement.rotateLeft();
                else
                    bot._movement.rotateRight();
            }
            else
                bot._movement.thrustForward();

            return true;
        }

        private void applyDifficultyTuning()
        {
            string difficulty = (_botSkirmishDifficulty ?? string.Empty).Trim().ToLowerInvariant();
            if (difficulty == "easy")
            {
                _botSkirmishReactionDelayMs = 650;
                _botSkirmishDetectRange = 900;
                _botSkirmishAimJitter = 40;
            }
            else if (difficulty == "hard")
            {
                _botSkirmishReactionDelayMs = 150;
                _botSkirmishDetectRange = 1600;
                _botSkirmishAimJitter = 8;
            }
        }

        private void ensureBotSkirmishBootstrap()
        {
            if (!_botSkirmishEnabled || _botSkirmishBootstrapped || PlayerCount <= 0)
                return;

            _botSkirmishBootstrapped = true;
            spawnBotSkirmishBots();
        }

        private void spawnBotSkirmishBots()
        {
            Team team = getTeamByID(_botSkirmishTeam) ?? getTeamByName("spec");
            Player seed = PlayersIngame.FirstOrDefault();
            if (seed == null)
                return;

            for (int i = 0; i < _botSkirmishCount; i++)
                spawnSingleSkirmishBot(team, seed, i);
        }

        private void spawnSingleSkirmishBot(Team team, Player seed, int offset)
        {
            Helpers.ObjectState state = new Helpers.ObjectState();
            state.positionX = (short)(seed._state.positionX + offset * 40);
            state.positionY = (short)(seed._state.positionY + offset * 40);
            state.yaw = (byte)(_rand.Next(0, 40) + offset * 20);

            Bot bot = newBot(typeof(Bot), (ushort)_botSkirmishVehicleId, team, null, state);
            if (bot == null)
                return;

            equipSkirmishBotWeapon(bot);
            _botSkirmishSequence++;
            _botSkirmishBrain[bot._id] = new BotSkirmishBrainState
            {
                LastX = bot._state.positionX,
                LastY = bot._state.positionY,
                LastMoveTick = Environment.TickCount,
                NextDecisionTick = Environment.TickCount,
                NextFireTick = Environment.TickCount + _botSkirmishReactionDelayMs
            };

            Log.write(TLog.Normal, $"[BotSkirmish] bot created id={bot._id} name={_botSkirmishNamePrefix}{_botSkirmishSequence}");
            Log.write(TLog.Normal, $"[BotSkirmish] bot spawned id={bot._id} x={bot._state.positionX} y={bot._state.positionY}");
        }

        private void equipSkirmishBotWeapon(Bot bot)
        {
            if (bot == null || bot._type == null || bot._weapon == null)
                return;

            foreach (int itemId in bot._type.InventoryItems)
            {
                if (itemId == 0)
                    continue;
                ItemInfo item = _server._assets.getItemByID(itemId);
                if (item == null)
                    continue;

                if (item is ItemInfo.Projectile || item is ItemInfo.MultiUse)
                {
                    if (bot._weapon.equip(item))
                        return;
                }
            }

            Log.write(TLog.Warning, $"[BotSkirmish] bot id={bot._id} has no valid projectile weapon equipped.");
        }

        private void onBotSkirmishBotKilled(Bot dead, Player killer, int weaponID)
        {
            if (!_botSkirmishEnabled || dead == null)
                return;

            _botSkirmishBrain.Remove(dead._id);
            Log.write(TLog.Normal, $"[BotSkirmish] bot killed id={dead._id} by={(killer != null ? killer._alias : "unknown")} weapon={weaponID}");
            _botSkirmishPendingRespawns[dead._id] = Environment.TickCount + _botSkirmishRespawnDelayMs;
        }

        private void pollBotSkirmishSupport()
        {
            if (!_botSkirmishEnabled)
                return;

            if (!_botSkirmishBootstrapped)
                ensureBotSkirmishBootstrap();

            int now = Environment.TickCount;
            foreach (Bot bot in _bots.ToList())
            {
                if (bot == null || bot.IsDead)
                    continue;

                BotSkirmishBrainState brain;
                if (!_botSkirmishBrain.TryGetValue(bot._id, out brain))
                {
                    brain = new BotSkirmishBrainState();
                    _botSkirmishBrain[bot._id] = brain;
                }

                int movementDistance = Math.Abs(bot._state.positionX - brain.LastX) + Math.Abs(bot._state.positionY - brain.LastY);
                if (movementDistance > 16)
                {
                    brain.LastMoveTick = now;
                    brain.LastX = bot._state.positionX;
                    brain.LastY = bot._state.positionY;
                }
                else if (now - brain.LastMoveTick > 1800)
                {
                    brain.HasDesiredYaw = true;
                    brain.DesiredYaw = (byte)_rand.Next(0, 255);
                    brain.LastMoveTick = now;
                }

                Player target = getPlayersInRange(bot._state.positionX, bot._state.positionY, _botSkirmishDetectRange, true)
                    .Where(p => p != null && p._team != bot._team)
                    .OrderBy(p => Helpers.distanceTo(p._state, bot._state))
                    .FirstOrDefault();

                if (target == null)
                {
                    if (moveTowardObjective(bot, brain, now))
                        continue;

                    if (brain.NextDecisionTick <= now)
                    {
                        brain.NextDecisionTick = now + _rand.Next(350, 900);
                        brain.HasDesiredYaw = true;
                        brain.DesiredYaw = (byte)_rand.Next(0, 255);
                    }

                    if (brain.HasDesiredYaw)
                    {
                        if (bot._state.yaw == brain.DesiredYaw)
                            brain.HasDesiredYaw = false;
                        else if (((byte)(brain.DesiredYaw - bot._state.yaw)) < 128)
                            bot._movement.rotateRight();
                        else
                            bot._movement.rotateLeft();
                    }

                    if (_rand.Next(0, 100) < 65)
                        bot._movement.thrustForward();
                    else
                        bot._movement.stop();

                    continue;
                }

                bool aimed;
                int testAim = bot._weapon.testAim(target._state, out aimed);
                if (testAim >= 0)
                    bot._movement.rotateRight();
                else
                    bot._movement.rotateLeft();

                if (shouldAvoidForwardTerrain(bot))
                {
                    if (_rand.Next(0, 100) < 50)
                        bot._movement.rotateLeft();
                    else
                        bot._movement.rotateRight();
                }
                else
                    bot._movement.thrustForward();
                if (aimed && bot._weapon.ableToFire() && brain.NextFireTick <= now)
                {
                    int jitter = _rand.Next(-_botSkirmishAimJitter, _botSkirmishAimJitter + 1);
                    byte shotYaw = (byte)(bot._state.yaw + jitter);
                    bot._itemUseID = bot._weapon.ItemID;
                    bot._weapon.shotFired();
                    bot._state.yaw = shotYaw;
                    brain.NextFireTick = now + _botSkirmishReactionDelayMs;
                }
            }

            Player seed = PlayersIngame.FirstOrDefault();
            Team team = getTeamByID(_botSkirmishTeam) ?? getTeamByName("spec");
            foreach (ushort deadId in _botSkirmishPendingRespawns.Keys.ToList())
            {
                if (_botSkirmishPendingRespawns[deadId] > now)
                    continue;

                _botSkirmishPendingRespawns.Remove(deadId);
                if (seed != null)
                    spawnSingleSkirmishBot(team, seed, _botSkirmishSequence + 1);

                Log.write(TLog.Normal, $"[BotSkirmish] bot respawned deadId={deadId}");
            }
        }
    }
}
