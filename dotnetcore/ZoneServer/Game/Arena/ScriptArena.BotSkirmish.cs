using System;
using System.Collections.Generic;
using System.Linq;
using InfServer.Bots;
using InfServer.Protocol;
using Assets;
using Axiom.Math;

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
        private int _botSkirmishSquadSpacing = 180;
        private int _botSkirmishAttachDistance = 950;
        private int _botSkirmishAttachCooldownMs = 1800;

        private class BotSkirmishBrainState
        {
            public short LastX;
            public short LastY;
            public int LastMoveTick;
            public int NextDecisionTick;
            public byte DesiredYaw;
            public bool HasDesiredYaw;
            public int NextFireTick;
            public ushort LeaderId;
            public int NextAttachTick;
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
            try { _botSkirmishSquadSpacing = Math.Max(80, _server._config["bots/squadSpacing"].intValue); } catch { }
            try { _botSkirmishAttachDistance = Math.Max(350, _server._config["bots/attachDistance"].intValue); } catch { }
            try { _botSkirmishAttachCooldownMs = Math.Max(600, _server._config["bots/attachCooldownMs"].intValue); } catch { }

            applyDifficultyTuning();
            Log.write(TLog.Normal, $"[BotSkirmish] initialized enabled={_botSkirmishEnabled} count={_botSkirmishCount} team={_botSkirmishTeam} prefix={_botSkirmishNamePrefix} respawnMs={_botSkirmishRespawnDelayMs} difficulty={_botSkirmishDifficulty}");
        }

        private bool isBlockedAt(int x, int y)
        {
            if (x < 16 || y < 16 || x >= (_levelWidth * 16) - 16 || y >= (_levelHeight * 16) - 16)
                return true;

            return getTile(x, y).Blocked;
        }

        private static void rotateShortestArcToward(Bot bot, byte desiredYawRaw)
        {
            int target = desiredYawRaw % 240;
            int cur = bot._state.yaw % 240;
            int diff = target - cur;
            if (diff > 120)
                diff -= 240;
            if (diff < -120)
                diff += 240;
            if (diff > 0)
                bot._movement.rotateRight();
            else if (diff < 0)
                bot._movement.rotateLeft();
        }

        private bool shouldAvoidForwardTerrain(Bot bot)
        {
            const int probeDistance = 42;
            Vector2 forward = Vector2.createUnitVector(bot._state.yaw);
            int probeX = bot._state.positionX + (int)(forward.x * probeDistance);
            int probeY = bot._state.positionY + (int)(forward.y * probeDistance);
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
                brain.HasDesiredYaw = false;
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

            double bearingDeg = Helpers.calculateDegreesBetweenPoints(
                bot._state.positionX, bot._state.positionY,
                _botSkirmishObjectiveX, _botSkirmishObjectiveY);
            byte desiredYaw = (byte)(((int)Math.Round(bearingDeg)) % 240);

            if ((bot._state.yaw % 240) != desiredYaw)
                rotateShortestArcToward(bot, desiredYaw);

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
                NextFireTick = Environment.TickCount + _botSkirmishReactionDelayMs,
                NextAttachTick = Environment.TickCount + _rand.Next(400, 1000)
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
                    brain.DesiredYaw = (byte)_rand.Next(0, 240);
                    brain.LastMoveTick = now;
                }

                Bot squadLeader = getSquadLeader(bot, brain);
                bool didAttach = maintainSquadAttachSummon(bot, brain, squadLeader, now);
                if (didAttach)
                    continue;

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
                        brain.DesiredYaw = (byte)_rand.Next(0, 240);
                    }

                    if (brain.HasDesiredYaw)
                    {
                        if ((bot._state.yaw % 240) == (brain.DesiredYaw % 240))
                            brain.HasDesiredYaw = false;
                        else
                            rotateShortestArcToward(bot, brain.DesiredYaw);
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
                    int shot = (bot._state.yaw + jitter) % 240;
                    if (shot < 0)
                        shot += 240;
                    byte shotYaw = (byte)shot;
                    bot._itemUseID = bot._weapon.ItemID;
                    bot._weapon.shotFired();
                    bot._state.yaw = shotYaw;
                    bot._movement.stopRotating();
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

        private Bot getSquadLeader(Bot bot, BotSkirmishBrainState brain)
        {
            if (bot == null)
                return null;

            Bot leader = null;
            if (brain.LeaderId != 0)
                leader = _bots.FirstOrDefault(b => b != null && b._id == brain.LeaderId && !b.IsDead);

            if (leader != null && leader != bot)
                return leader;

            leader = _bots
                .Where(b => b != null && !b.IsDead && b != bot && b._team == bot._team)
                .OrderBy(b => Helpers.distanceTo(bot._state, b._state))
                .FirstOrDefault();

            brain.LeaderId = (leader == null) ? (ushort)0 : leader._id;
            return leader;
        }

        private bool maintainSquadAttachSummon(Bot bot, BotSkirmishBrainState brain, Bot leader, int now)
        {
            if (bot == null || leader == null || leader == bot)
                return false;

            double distance = Helpers.distanceTo(bot._state, leader._state);

            bool enemyNearby = getPlayersInRange(bot._state.positionX, bot._state.positionY, _botSkirmishDetectRange, true)
                .Any(p => p != null && !p.IsDead && p._team != bot._team);

            if (distance > _botSkirmishAttachDistance && brain.NextAttachTick <= now && !enemyNearby)
            {
                Helpers.ObjectState attach = new Helpers.ObjectState
                {
                    positionX = (short)(leader._state.positionX + _rand.Next(-_botSkirmishSquadSpacing, _botSkirmishSquadSpacing + 1)),
                    positionY = (short)(leader._state.positionY + _rand.Next(-_botSkirmishSquadSpacing, _botSkirmishSquadSpacing + 1)),
                    positionZ = bot._state.positionZ,
                    yaw = bot._state.yaw
                };
                if (!isBlockedAt(attach.positionX, attach.positionY))
                {
                    bot._movement.warp(attach);
                    brain.LastX = attach.positionX;
                    brain.LastY = attach.positionY;
                    brain.LastMoveTick = now;
                    brain.NextAttachTick = now + _botSkirmishAttachCooldownMs;
                    return true;
                }
            }

            if (distance > _botSkirmishSquadSpacing)
            {
                double bearingDeg = Helpers.calculateDegreesBetweenPoints(
                    bot._state.positionX, bot._state.positionY,
                    leader._state.positionX, leader._state.positionY);
                byte bearingYaw = (byte)(((int)Math.Round(bearingDeg)) % 240);
                rotateShortestArcToward(bot, bearingYaw);

                if (!shouldAvoidForwardTerrain(bot))
                    bot._movement.thrustForward();
                return true;
            }

            return false;
        }
    }
}
