#region LICENSE

// The contents of this file are subject to the Common Public Attribution
// License Version 1.0. (the "License"); you may not use this file except in
// compliance with the License. You may obtain a copy of the License at
// https://github.com/NiclasOlofsson/MiNET/blob/master/LICENSE. 
// The License is based on the Mozilla Public License Version 1.1, but Sections 14 
// and 15 have been added to cover use of software over a computer network and 
// provide for limited attribution for the Original Developer. In addition, Exhibit A has 
// been modified to be consistent with Exhibit B.
// 
// Software distributed under the License is distributed on an "AS IS" basis,
// WITHOUT WARRANTY OF ANY KIND, either express or implied. See the License for
// the specific language governing rights and limitations under the License.
// 
// The Original Code is Niclas Olofsson.
// 
// The Original Developer is the Initial Developer.  The Initial Developer of
// the Original Code is Niclas Olofsson.
// 
// All portions of the code written by Niclas Olofsson are Copyright (c) 2014-2017 Niclas Olofsson. 
// All Rights Reserved.

#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using MiNET.Blocks;
using MiNET.Utils;
using MiNET.Worlds;

namespace MiNET
{
	public class LevelManager
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof (LevelManager));

		public List<Level> Levels { get; set; } = new List<Level>();

		public EntityManager EntityManager { get; set; } = new EntityManager();

		public LevelManager()
		{
		}

		public virtual Level GetLevel(Player player, string name)
		{
			Level level = Levels.FirstOrDefault(l => l.LevelId.Equals(name, StringComparison.InvariantCultureIgnoreCase));
			if (level == null)
			{
				GameMode gameMode = Config.GetProperty("GameMode", GameMode.Survival);
				Difficulty difficulty = Config.GetProperty("Difficulty", Difficulty.Normal);
				int viewDistance = Config.GetProperty("ViewDistance", 11);
				bool enableBlockTicking = Config.GetProperty("EnableBlockTicking", false);
				bool enableChunkTicking = Config.GetProperty("EnableChunkTicking", false);
				bool isWorldTimeStarted = Config.GetProperty("IsWorldTimeStarted", false);

				IWorldProvider worldProvider = null;

				switch (Config.GetProperty("WorldProvider", "flat").ToLower().Trim())
				{
					case "cool":
						worldProvider = new CoolWorldProvider();
						break;
					case "experimental":
						worldProvider = new ExperimentalWorldProvider();
						break;
					case "anvil":
					case "flat":
					case "flatland":
					default:
						worldProvider = new AnvilWorldProvider
						{
							MissingChunkProvider = new FlatlandWorldProvider(),
							ReadSkyLight = !Config.GetProperty("CalculateLights", false),
							ReadBlockLight = !Config.GetProperty("CalculateLights", false),
						};
						break;
				}

				level = new Level(this, name, worldProvider, EntityManager, gameMode, difficulty, viewDistance)
				{
					EnableBlockTicking = enableBlockTicking,
					EnableChunkTicking = enableChunkTicking,
					IsWorldTimeStarted = isWorldTimeStarted
				};
				level.Initialize();

				if (Config.GetProperty("CalculateLights", false))
				{
					{
						AnvilWorldProvider wp = level.WorldProvider as AnvilWorldProvider;
						if (wp != null)
						{
							SkyLightCalculations.Calculate(level);

							Stopwatch sw = new Stopwatch();

							int count = wp.LightSources.Count;
							sw.Restart();
							RecalculateBlockLight(level, wp);

							var chunkCount = wp._chunkCache.Where(chunk => chunk.Value != null).ToArray().Length;
							Log.Debug($"Recalculated sky and block light for {chunkCount} chunks, {chunkCount*16*16*256} blocks and {count} light sources. Time {sw.ElapsedMilliseconds}ms");
						}
					}
				}

				Levels.Add(level);

				OnLevelCreated(new LevelEventArgs(null, level));
			}

			return level;
		}

		public static void RecalculateBlockLight(Level level, AnvilWorldProvider wp)
		{
			Queue<Block> sources = new Queue<Block>(wp.LightSources);

			while (sources.Count > 0)
			{
				var block = sources.Dequeue();
				if (block == null) continue;

				block = level.GetBlock(block.Coordinates);
				BlockLightCalculations.Calculate(level, block);
			}
		}

		public void RemoveLevel(Level level)
		{
			if (Levels.Contains(level))
			{
				Levels.Remove(level);
			}

			level.Close();
		}

		public event EventHandler<LevelEventArgs> LevelCreated;

		protected virtual void OnLevelCreated(LevelEventArgs e)
		{
			LevelCreated?.Invoke(this, e);
		}

		public virtual Level GetDimension(Level level, Dimension dimension)
		{
			if (dimension == Dimension.Overworld) throw new Exception($"Can not get level for '{dimension}' from the LevelManager");
			if (dimension == Dimension.Nether && !level.WorldProvider.HaveNether()) return null;
			if (dimension == Dimension.TheEnd && !level.WorldProvider.HaveTheEnd()) return null;

			AnvilWorldProvider overworld = level.WorldProvider as AnvilWorldProvider;
			if (overworld == null) return null;

			var worldProvider = new AnvilWorldProvider(overworld.BasePath)
			{
				ReadBlockLight = overworld.ReadBlockLight,
				ReadSkyLight = overworld.ReadSkyLight,
				Dimension = dimension,
				MissingChunkProvider = new AirWorldGenerator(),
			};

			Level newLevel = new Level(level.LevelManager, level.LevelId + "_" + dimension, worldProvider, EntityManager, level.GameMode, level.Difficulty, level.ViewDistance)
			{
				OverworldLevel = level,
				Dimension = dimension,
				EnableBlockTicking = level.EnableBlockTicking,
				EnableChunkTicking = level.EnableChunkTicking,
				IsWorldTimeStarted = level.IsWorldTimeStarted
			};

			newLevel.Initialize();

			if (Config.GetProperty("CalculateLights", false))
			{
				SkyLightCalculations.Calculate(newLevel);

				int count = worldProvider.LightSources.Count;
				Log.Debug($"Recalculating block light for {count} light sources.");
				Stopwatch sw = new Stopwatch();
				sw.Start();
				RecalculateBlockLight(newLevel, worldProvider);

				var chunkCount = worldProvider._chunkCache.Where(chunk => chunk.Value != null).ToArray().Length;
				Log.Debug($"Recalc sky and block light for {chunkCount} chunks, {chunkCount*16*16*256} blocks and {count} light sources. Time {sw.ElapsedMilliseconds}ms");
			}

			return newLevel;
		}
	}

	public class SpreadLevelManager : LevelManager
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof (SpreadLevelManager));

		private readonly int _numberOfLevels;

		public SpreadLevelManager(int numberOfLevels)
		{
			Log.Warn($"Creating and caching {numberOfLevels} levels");

			//Level template = CreateLevel("Default", null);

			_numberOfLevels = numberOfLevels;
			Levels = new List<Level>();
			Parallel.For(0, numberOfLevels, i =>
			{
				var name = "Default" + i;
				//Levels.Add(CreateLevel(name, template._worldProvider));
				Levels.Add(CreateLevel(name, null));
				Log.Warn($"Created level {name}");
			});

			Log.Warn("DONE Creating and caching worlds");
		}

		public override Level GetLevel(Player player, string name)
		{
			Random rand = new Random();

			return Levels[rand.Next(0, _numberOfLevels)];
		}

		public virtual Level CreateLevel(string name, IWorldProvider provider)
		{
			GameMode gameMode = Config.GetProperty("GameMode", GameMode.Survival);
			Difficulty difficulty = Config.GetProperty("Difficulty", Difficulty.Peaceful);
			int viewDistance = Config.GetProperty("ViewDistance", 11);

			IWorldProvider worldProvider = null;
			worldProvider = provider ?? new AnvilWorldProvider {MissingChunkProvider = new FlatlandWorldProvider()};

			var level = new Level(this, name, worldProvider, EntityManager, gameMode, difficulty, viewDistance);
			level.Initialize();

			OnLevelCreated(new LevelEventArgs(null, level));

			return level;
		}

		public event EventHandler<LevelEventArgs> LevelCreated;

		protected virtual void OnLevelCreated(LevelEventArgs e)
		{
			LevelCreated?.Invoke(this, e);
		}
	}
}