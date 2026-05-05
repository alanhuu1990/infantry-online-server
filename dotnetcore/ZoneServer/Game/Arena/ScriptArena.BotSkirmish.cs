using System;
using System.Collections.Generic;
using System.Linq;
using InfServer.Bots;

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
        private const int BotSkirmishSpreadRadius = 220;

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

            _botSkirmishEnabled = readBotBool("bots/enabled", _botSkirmishEnabled);
            _botSkirmishCount = Math.Max(1, readBotInt("bots/count", _botSkirmishCount));
            _botSkirmishTeam = readBotInt("bots/team", _botSkirmishTeam);
            _botSkirmishNamePrefix = readBotString("bots/namePrefix", _botSkirmishNamePrefix);
            _botSkirmishRespawnDelayMs = Math.Max(500, readBotInt("bots/respawnMs", _botSkirmishRespawnDelayMs));
            _botSkirmishDifficulty = readBotString("bots/difficulty", _botSkirmishDifficulty);
            _botSkirmishVehicleId = readBotInt("bots/vehicleId", _botSkirmishVehicleId);
            _botSkirmishDetectRange = Math.Max(200, readBotInt("bots/detectRange", _botSkirmishDetectRange));
            _botSkirmishReactionDelayMs = Math.Max(100, readBotInt("bots/reactionDelayMs", _botSkirmishReactionDelayMs));
            _botSkirmishAimJitter = Math.Max(0, readBotInt("bots/aimJitter", _botSkirmishAimJitter));

            applyDifficultyTuning();
            Log.write(TLog.Normal, $"[BotSkirmish] initialized enabled={_botSkirmishEnabled} count={_botSkirmishCount} team={_botSkirmishTeam} prefix={_botSkirmishNamePrefix} respawnMs={_botSkirmishRespawnDelayMs} difficulty={_botSkirmishDifficulty}");
        }

        private void applyDifficultyTuning()
        {
            string difficulty = (_botSkirmishDifficulty ?? string.Empty).Trim().ToLowerInvariant();
            if (difficulty == "normal" || string.IsNullOrWhiteSpace(difficulty))
                return;

            if (difficulty == "easy")
            {
                _botSkirmishReactionDelayMs = Math.Max(_botSkirmishReactionDelayMs, 650);
                _botSkirmishDetectRange = Math.Min(_botSkirmishDetectRange, 900);
                _botSkirmishAimJitter = Math.Max(_botSkirmishAimJitter, 40);
            }
            else if (difficulty == "hard")
            {
                _botSkirmishReactionDelayMs = Math.Min(_botSkirmishReactionDelayMs, 150);
                _botSkirmishDetectRange = Math.Max(_botSkirmishDetectRange, 1600);
                _botSkirmishAimJitter = Math.Min(_botSkirmishAimJitter, 8);
            }
            else
                Log.write(TLog.Warning, $"[BotSkirmish] invalid difficulty '{_botSkirmishDifficulty}', using configured timing/range/jitter values.");
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
            Team team = getTeamByFreq(_botSkirmishTeam) ?? _specTeam;
            Player seed = PlayersIngame.FirstOrDefault();
            if (seed == null)
                return;

            Log.write(TLog.Normal, $"[BotSkirmish] spawning {_botSkirmishCount} bot(s).");
            for (int i = 0; i < _botSkirmishCount; i++)
                spawnSingleSkirmishBot(team, seed, i, _botSkirmishCount);
        }

        private void spawnSingleSkirmishBot(Team team, Player seed, int index, int total)
        {
            Helpers.ObjectState state = new Helpers.ObjectState();
            getBotSpawnState(seed, index, total, state);

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
        private bool isValidBotTarget(Bot bot, Player player)
        {
            if (bot == null || player == null)
                return false;
            if (player._bSpectator || player._bDisconnected || player.IsDead)
                return false;
            if (player._team == null || bot._team == null || player._team == bot._team)
                return false;
            if (player._state == null || player._occupiedVehicle == null && player._state.positionX == 0 && player._state.positionY == 0)
                return false;
            return true;
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

                Player target = getPlayersInRange(bot._state.positionX, bot._state.positionY, _botSkirmishDetectRange, true)
                    .Where(p => isValidBotTarget(bot, p))
                    .OrderBy(p => Helpers.distanceTo(p._state, bot._state))
                    .FirstOrDefault();

                if (target == null)
                {
                    if (brain.NextDecisionTick <= now)
                    {
                        brain.NextDecisionTick = now + _rand.Next(350, 900);
                        brain.HasDesiredYaw = true;
                        brain.DesiredYaw = (byte)_rand.Next(0, 240);
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

                bot._movement.thrustForward();
                if (aimed && bot._weapon.ableToFire() && brain.NextFireTick <= now)
                {
                    int jitter = _rand.Next(-_botSkirmishAimJitter, _botSkirmishAimJitter + 1);
                    int rawYaw = bot._state.yaw + jitter;
                    while (rawYaw < 0)
                        rawYaw += 240;
                    byte shotYaw = (byte)(rawYaw % 240);
                    bot._itemUseID = bot._weapon.ItemID;
                    bot._weapon.shotFired();
                    handleBotFire(bot, shotYaw);
                    brain.NextFireTick = now + _botSkirmishReactionDelayMs;
                }
            }

            Player seed = PlayersIngame.FirstOrDefault();
            Team team = getTeamByFreq(_botSkirmishTeam) ?? _specTeam;
            foreach (ushort deadId in _botSkirmishPendingRespawns.Keys.ToList())
            {
                if (_botSkirmishPendingRespawns[deadId] > now)
                    continue;

                _botSkirmishPendingRespawns.Remove(deadId);
                if (seed != null)
                    spawnSingleSkirmishBot(team, seed, _botSkirmishSequence % Math.Max(1, _botSkirmishCount), Math.Max(1, _botSkirmishCount));

                Log.write(TLog.Normal, $"[BotSkirmish] bot respawned deadId={deadId}");
            }
        }

        private bool readBotBool(string key, bool fallback)
        {
            try { return _server._config[key].boolValue; }
            catch { Log.write(TLog.Warning, $"[BotSkirmish] invalid config '{key}', using fallback '{fallback}'."); return fallback; }
        }
        private int readBotInt(string key, int fallback)
        {
            try { return _server._config[key].intValue; }
            catch { Log.write(TLog.Warning, $"[BotSkirmish] invalid config '{key}', using fallback '{fallback}'."); return fallback; }
        }
        private string readBotString(string key, string fallback)
        {
            try { return _server._config[key].Value; }
            catch { Log.write(TLog.Warning, $"[BotSkirmish] invalid config '{key}', using fallback '{fallback}'."); return fallback; }
        }

        private void getBotSpawnState(Player seed, int index, int total, Helpers.ObjectState state)
        {
            short baseX = seed._state.positionX;
            short baseY = seed._state.positionY;
            int safeTotal = Math.Max(1, total);
            double angle = (2.0 * Math.PI * index) / safeTotal;
            int radius = Math.Min(BotSkirmishSpreadRadius, 60 + (index % 4) * 35);
            int x = baseX + (int)(Math.Cos(angle) * radius);
            int y = baseY + (int)(Math.Sin(angle) * radius);
            state.positionX = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, x));
            state.positionY = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, y));
            state.yaw = (byte)_rand.Next(0, 240);
        }
    }
}
