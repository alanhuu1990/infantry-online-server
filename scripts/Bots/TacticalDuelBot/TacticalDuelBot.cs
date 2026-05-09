using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Game;
using InfServer.Scripting;
using InfServer.Bots;
using InfServer.Protocol;

using Assets;

using Axiom.Math;

namespace InfServer.Script.TacticalDuelBot
{
	/// <summary>
	/// Server-side duel bot: engages hostile players and other bots with pacing, strafing, and simple terrain avoidance.
	/// </summary>
	class Script_TacticalDuel : Scripts.IScript
	{
		private Bot _bot;
		private Random _rand;

		private Helpers.ObjectState _targetState;
		private Player _targetPlayer;

		private int _detectRange = 1200;
		private int _loseTargetRange = 1400;
		private int _optimalDistance = 100;
		private int _optimalDistanceTolerance = 25;
		private int _distanceTolerance = 120;
		private int _tickNextStrafeChange;
		private bool _strafeLeft;
		private bool _chasing;
		private int _nextFireTick;
		private int _reactionDelayMs = 280;
		private int _aimJitter = 14;

		public bool init(IEventObject invoker)
		{
			_bot = invoker as Bot;
			if (_bot == null)
				return false;

			_rand = new Random();

			if (!equipProjectileWeapon())
				return false;

			if (_bot._type.Description != null && _bot._type.Description.Length >= 4
				&& _bot._type.Description.Substring(0, 4).Equals("bot=", StringComparison.OrdinalIgnoreCase))
			{
				string[] botparams = _bot._type.Description.Substring(4).Split(',');
				foreach (string botparam in botparams)
				{
					if (!botparam.Contains(':'))
						continue;
					string[] pair = botparam.Split(':');
					if (pair.Length < 2)
						continue;
					string paramname = pair[0].Trim().ToLowerInvariant();
					string paramvalue = pair[1].Trim().ToLowerInvariant();
					switch (paramname)
					{
						case "radius":
						case "detect":
							_detectRange = Convert.ToInt32(paramvalue);
							_loseTargetRange = Math.Max(_detectRange + 150, _loseTargetRange);
							break;
						case "distance":
							_optimalDistance = Convert.ToInt32(paramvalue);
							break;
						case "tolerance":
							_optimalDistanceTolerance = Convert.ToInt32(paramvalue);
							break;
						case "strafe":
							_distanceTolerance = Convert.ToInt32(paramvalue);
							break;
						case "reaction":
							_reactionDelayMs = Math.Max(80, Convert.ToInt32(paramvalue));
							break;
						case "jitter":
							_aimJitter = Math.Max(0, Convert.ToInt32(paramvalue));
							break;
					}
				}
			}

			_nextFireTick = Environment.TickCount;
			return true;
		}

		private bool equipProjectileWeapon()
		{
			if (_bot._type == null || _bot._weapon == null)
				return false;
			foreach (int itemId in _bot._type.InventoryItems)
			{
				if (itemId == 0)
					continue;
				ItemInfo item = AssetManager.Manager.getItemByID(itemId);
				if (item == null)
					continue;
				if (item is ItemInfo.Projectile || item is ItemInfo.MultiUse)
				{
					if (_bot._weapon.equip(item))
						return true;
				}
			}
			if (_bot._type.InventoryItems.Length > 0)
			{
				ItemInfo fallback = AssetManager.Manager.getItemByID(_bot._type.InventoryItems[0]);
				if (fallback != null && _bot._weapon.equip(fallback))
					return true;
			}
			return false;
		}

		public bool poll()
		{
			_bot._movement.stop();

			int tickCount = Environment.TickCount;

			if (_bot.IsDead)
			{
				_bot.destroy(true);
				return false;
			}

			if (_targetPlayer != null && _targetPlayer.IsDead)
			{
				if (AssetManager.Manager.getItemByID(1095) != null)
					_bot._itemUseID = 1095;
			}

			if (!acquireTarget())
			{
				_targetState = null;
				_targetPlayer = null;
				return false;
			}

			Vector2 distanceVector = new Vector2(
				_bot._state.positionX - _targetState.positionX,
				_bot._state.positionY - _targetState.positionY);
			double distance = distanceVector.Length;

			bool aimed;
			int aimResult = _bot._weapon.testAim(_targetState, out aimed);

			if (aimed && _bot._weapon.ableToFire() && _nextFireTick <= tickCount)
			{
				int jitter = _rand.Next(-_aimJitter, _aimJitter + 1);
				int shot = (_bot._state.yaw + jitter) % 240;
				if (shot < 0)
					shot += 240;
				byte shotYaw = (byte)shot;
				_bot._itemUseID = _bot._weapon.ItemID;
				_bot._weapon.shotFired();
				_bot._state.yaw = shotYaw;
				_nextFireTick = tickCount + _reactionDelayMs;
				_bot._movement.stopRotating();
			}
			else if (aimResult > 0)
				_bot._movement.rotateRight();
			else
				_bot._movement.rotateLeft();

			bool inDuelBand = (_chasing && distance < (_optimalDistance + _distanceTolerance)
					&& distance > (_optimalDistance - _distanceTolerance))
				|| (!_chasing && distance < (_optimalDistance + _optimalDistanceTolerance)
					&& distance > (_optimalDistance - _optimalDistanceTolerance));

			if (inDuelBand)
			{
				_chasing = false;
				if (tickCount > _tickNextStrafeChange)
				{
					_tickNextStrafeChange = tickCount + _rand.Next(280, 900);
					_strafeLeft = !_strafeLeft;
				}
				if (_strafeLeft)
					_bot._movement.strafeLeft();
				else
					_bot._movement.strafeRight();
			}
			else
				_chasing = true;

			if (distance > _optimalDistance)
				_bot._movement.thrustForward();
			else
				_bot._movement.thrustBackward();

			if (shouldAvoidForwardTerrain())
			{
				if (_rand.Next(0, 2) == 0)
					_bot._movement.rotateLeft();
				else
					_bot._movement.rotateRight();
			}

			return false;
		}

		private bool shouldAvoidForwardTerrain()
		{
			const int probeDistance = 42;
			Vector2 forward = Vector2.createUnitVector(_bot._state.yaw);
			int probeX = _bot._state.positionX + (int)(forward.x * probeDistance);
			int probeY = _bot._state.positionY + (int)(forward.y * probeDistance);
			int lw = _bot._arena._levelWidth;
			int lh = _bot._arena._levelHeight;
			if (probeX < 16 || probeY < 16 || probeX >= (lw * 16) - 16 || probeY >= (lh * 16) - 16)
				return true;
			return _bot._arena.getTile(probeX, probeY).Blocked;
		}

		private bool hostileTeam(Team t)
		{
			if (t == null)
				return true;
			if (_bot._team == null)
				return true;
			return t != _bot._team;
		}

		private bool acquireTarget()
		{
			_targetPlayer = null;

			Player bestPlayer = _bot._arena.getPlayersInRange(_bot._state.positionX, _bot._state.positionY, _detectRange, true)
				.Where(p => p != null && !p.IsDead && hostileTeam(p._team)
					&& Helpers.distanceTo(_bot._state, p._state) <= _loseTargetRange)
				.OrderBy(p => Helpers.distanceSquaredTo(_bot._state, p._state))
				.FirstOrDefault();

			Bot bestBot = _bot._arena.getBotsInRange(_bot._state.positionX, _bot._state.positionY, _detectRange, true)
				.Where(b => b != null && b != _bot && !b.IsDead && hostileTeam(b._team)
					&& Helpers.distanceTo(_bot, b) <= _loseTargetRange)
				.OrderBy(b => Helpers.distanceSquaredTo(_bot._state, b._state))
				.FirstOrDefault();

			double dPlayer = bestPlayer != null ? Helpers.distanceSquaredTo(_bot._state, bestPlayer._state) : double.MaxValue;
			double dBot = bestBot != null ? Helpers.distanceSquaredTo(_bot._state, bestBot._state) : double.MaxValue;

			if (bestPlayer == null && bestBot == null)
				return false;

			if (dPlayer <= dBot)
			{
				_targetPlayer = bestPlayer;
				_targetState = bestPlayer._state;
			}
			else
				_targetState = bestBot._state;

			return true;
		}
	}
}
