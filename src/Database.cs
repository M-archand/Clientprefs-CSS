using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Dapper;
using MySqlConnector;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;


using Clientprefs.API;

namespace Clientprefs;

public partial class Clientprefs
{
	private bool IsMySQL()
	{
		return _isMySQL;
	}

	private bool IsDatabaseConfigComplete()
	{
		if (!IsMySQL()) return true;

		return Config.DatabaseHost != "" && Config.DatabaseName != ""
			&& Config.DatabaseUsername != "" && Config.DatabasePassword != "";
	}

	private string ScrubSecrets(string message)
	{
		if (!string.IsNullOrEmpty(Config.DatabasePassword))
		{
			message = message.Replace(Config.DatabasePassword, "***");
		}
		return message;
	}

	private static bool IsLoadFailure(Exception ex)
	{
		for (Exception? e = ex; e != null; e = e.InnerException)
		{
			if (e is FileNotFoundException or FileLoadException or DllNotFoundException
				or BadImageFormatException or TypeInitializationException)
			{
				return true;
			}
		}
		return false;
	}

	private static string DescribeExceptionChain(Exception ex)
	{
		var parts = new List<string>();
		for (Exception? e = ex; e != null; e = e.InnerException)
		{
			parts.Add($"{e.GetType().Name}: {e.Message}");
		}
		return string.Join(" <- ", parts);
	}

	private void LogDbError(string context, Exception ex)
	{
		if (IsLoadFailure(ex))
		{
			Logger.LogError($"{context}: {ScrubSecrets(DescribeExceptionChain(ex))}. The plugin install is missing a dependency; reinstall the full release package (including the runtimes folder).");
		}
		else if (Config.Debug)
		{
			Logger.LogError($"{context}: {ScrubSecrets(ex.Message)}");
		}
		else
		{
			Logger.LogError($"{context} ({ex.GetType().Name}). Enable Debug in the config for details.");
		}
	}

	// Retries transient DB connect failures with backoff so OnDatabaseLoaded still fires; misconfiguration is not retried.
	private async Task ConnectDatabaseTableWithRetry()
	{
		if (!IsDatabaseConfigComplete())
		{
			Logger.LogError("Database connection information is missing. Please fill in the information in the config file.");
			return;
		}

		const int maxAttempts = 5;

		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			if (await ConnectDatabaseTable())
			{
				return;
			}

			if (attempt < maxAttempts)
			{
				int delaySeconds = Math.Min(30, attempt * 5);
				Logger.LogWarning($"Database connection attempt {attempt}/{maxAttempts} failed. Retrying in {delaySeconds}s...");
				await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
			}
		}

		Logger.LogError($"Database connection failed after {maxAttempts} attempts. Clientprefs is disabled; dependent plugins will not receive OnDatabaseLoaded.");
	}

	private void BuildMySqlDataSource()
	{
		MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
		{
			Server = Config.DatabaseHost,
			UserID = Config.DatabaseUsername,
			Password = Config.DatabasePassword,
			Database = Config.DatabaseName,
			Port = (uint)Config.DatabasePort,
			SslMode = Enum.Parse<MySqlSslMode>(Config.DatabaseSslmode, true),
			AllowUserVariables = true,
		};

		_mySqlDataSource?.Dispose();
		_mySqlDataSource = new MySqlDataSource(builder.ToString());
	}

	private DbConnection CreateConnection()
	{
		return IsMySQL() ? CreateMySqlConnection() : CreateSqliteConnection();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private DbConnection CreateMySqlConnection()
	{
		return _mySqlDataSource!.CreateConnection();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private DbConnection CreateSqliteConnection()
	{
		return new SqliteConnection($"Data Source={Path.Join(ModuleDirectory, "clientprefs.db")}");
	}

	private async Task<bool> ConnectDatabaseTable()
	{
		try
		{
			string cookiequery = @$"CREATE TABLE IF NOT EXISTS {Config.TableName}
            (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name varchar({IClientprefsApi.COOKIE_MAX_NAME_LENGTH}) NOT NULL UNIQUE,
                description varchar({IClientprefsApi.COOKIE_MAX_DESCRIPTION_LENGTH}),
                access INTEGER
            )";
			string playerquery = @$"CREATE TABLE IF NOT EXISTS {Config.TableNamePlayerData}
            (
                steamid varchar(65) NOT NULL,
                cookie_id INT NOT NULL,
                value varchar({IClientprefsApi.COOKIE_MAX_VALUE_LENGTH}),
                timestamp int,
                PRIMARY KEY (steamid, cookie_id)
            )";

			if (IsMySQL())
			{
				if (!IsDatabaseConfigComplete())
				{
					Logger.LogError($"Database connection information is missing. Please fill in the information in the config file.");
					return false;
				}

				BuildMySqlDataSource();

				cookiequery = @$"CREATE TABLE IF NOT EXISTS {Config.TableName}
                (
                    id INTEGER unsigned NOT NULL auto_increment,
                    name varchar({IClientprefsApi.COOKIE_MAX_NAME_LENGTH}) NOT NULL UNIQUE,
                    description varchar({IClientprefsApi.COOKIE_MAX_DESCRIPTION_LENGTH}),
                    access INTEGER,
                    PRIMARY KEY (id)
                )";

				playerquery = @$"CREATE TABLE IF NOT EXISTS {Config.TableNamePlayerData}
                (
                    steamid varchar(32) NOT NULL,
                    cookie_id INT NOT NULL,
                    value varchar({IClientprefsApi.COOKIE_MAX_VALUE_LENGTH}),
                    timestamp int NOT NULL,
                    PRIMARY KEY (steamid, cookie_id)
                )";
			}

			using (var connection = CreateConnection())
			{
				await connection.OpenAsync();

				if (!IsMySQL())
				{
					await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
				}

				using (var transaction = await connection.BeginTransactionAsync())
				{
					await connection.ExecuteAsync(cookiequery, transaction: transaction);
					await connection.ExecuteAsync(playerquery, transaction: transaction);

					await transaction.CommitAsync();
				}

				cookiequery = @$"SELECT * FROM {Config.TableName}";

				var rows = await connection.QueryAsync(cookiequery);

				lock (_clientprefIdLock)
				{
					Cookies.Clear();
					_cookiesByName.Clear();

					foreach (var row in rows)
					{
						if (Cookies.ContainsKey((int)row.id) || _cookiesByName.ContainsKey((string)row.name)) continue;

						var cookie = new Cookie
						{
							Id = (int)row.id,
							Name = row.name,
							Description = row.description,
							Access = (CookieAccess)row.access,
						};
						Cookies[cookie.Id] = cookie;
						_cookiesByName[cookie.Name] = cookie;
					}
				}

				_databaseLoaded = true;

				Server.NextWorldUpdate(() =>
				{
					var steamIds = new List<string>();
					foreach (var p in Utilities.GetPlayers())
					{
						if (p == null || !p.IsValidPlayer())
						{
							continue;
						}

						steamIds.Add(p.SteamID.ToString());
					}

					GetPlayerCookiesBatch(steamIds);

					TryFireDatabaseLoaded();

					DebugLog("Database connection established.");
				});

				return true;
			}
		}
		catch (Exception ex)
		{
			LogDbError("Unable to connect to database", ex);
			return false;
		}
	}

	// Bulk-loads cookie rows for all given steamIds via one WHERE steamid IN (...) query on a single background Task, 
	// then marks each PlayerSetting as Loaded.
	private void GetPlayerCookiesBatch(List<string> steamIds)
	{
		if (!_databaseLoaded)
		{
			Logger.LogError($"GetPlayerCookiesBatch called when Database is not loaded yet.");
			return;
		}

		if (steamIds.Count == 0) return;

		foreach (var steamId in steamIds)
		{
			PlayerSettings.GetOrAdd(steamId, _ => new());
		}

		var load = Task.Run(async () =>
		{
			try
			{
				using (var connection = CreateConnection())
				{
					await connection.OpenAsync();

					string query = $"SELECT steamid, cookie_id, value FROM {Config.TableNamePlayerData} WHERE steamid IN @steamIds";

					var rows = await connection.QueryAsync(query, new { steamIds });

					foreach (var row in rows)
					{
						var steamId = (string)row.steamid;
						if (PlayerSettings.TryGetValue(steamId, out var pref))
						{
							pref.LoadCookie((int)row.cookie_id, (string)(row.value ?? ""));
						}
					}

					foreach (var steamId in steamIds)
					{
						if (PlayerSettings.TryGetValue(steamId, out var pref))
						{
							pref.Loaded = true;
						}
					}

					Server.NextWorldUpdate(() =>
					{
						foreach (var player in Utilities.GetPlayers())
						{
							if (player == null || !player.IsValidPlayer()) continue;
							if (!steamIds.Contains(player.SteamID.ToString())) continue;

							ClientprefsApi.CallOnPlayerCookiesCached(player);
						}
					});
				}
			}
			catch (Exception ex)
			{
				foreach (var steamId in steamIds)
				{
					if (PlayerSettings.TryGetValue(steamId, out var pref))
					{
						pref.LoadFailed = true;
					}
				}
				LogDbError("An error occurred while fetching player preferences", ex);
			}
		});

		foreach (var steamId in steamIds)
		{
			if (PlayerSettings.TryGetValue(steamId, out var pref))
			{
				pref.LoadTask = load;
			}
		}
	}

	private static CCSPlayerController? FindConnectedPlayer(string steamId)
	{
		return Utilities.GetPlayers().FirstOrDefault(p => p.IsValidPlayer() && p.SteamID.ToString() == steamId);
	}

	private void EvictIfDisconnected(string steamId)
	{
		if (_connectedSteamIds.Contains(steamId)) return;

		PlayerSettings.TryRemove(steamId, out _);
	}

	private void FireCookiesCachedNextFrame(string steamId)
	{
		Server.NextWorldUpdate(() =>
		{
			var player = FindConnectedPlayer(steamId);
			if (player == null) return;

			ClientprefsApi.CallOnPlayerCookiesCached(player);
		});
	}

	private void GetPlayerCookies(string steamId)
	{
		if (!_databaseLoaded)
		{
			Logger.LogError($"GetPlayerCookies called when Database is not loaded yet.");
			return;
		}

		var setting = PlayerSettings.GetOrAdd(steamId, _ => new());

		if (setting.Loaded)
		{
			DebugLog($"Cookies for {steamId} already cached: {setting.CookieCount}");
			FireCookiesCachedNextFrame(steamId);
			return;
		}

		if (setting.LoadTask is { IsCompleted: false })
		{
			return;
		}

		setting.LoadFailed = false;

		setting.LoadTask = Task.Run(async () =>
		{
			try
			{
				using (var connection = CreateConnection())
				{
					await connection.OpenAsync();

					string query = $"SELECT cookie_id, value FROM {Config.TableNamePlayerData} WHERE steamid = @steam";

					var rows = await connection.QueryAsync(query, new { steam = steamId });

					foreach (var row in rows)
					{
						if (PlayerSettings.TryGetValue(steamId, out var pref))
						{
							pref.LoadCookie((int)row.cookie_id, (string)(row.value ?? ""));
						}
					}

					if (PlayerSettings.TryGetValue(steamId, out var loaded))
					{
						loaded.Loaded = true;
						DebugLog($"Cookies for {steamId} loaded: {loaded.CookieCount}");
					}

					FireCookiesCachedNextFrame(steamId);
				}
			}
			catch (Exception ex)
			{
				if (PlayerSettings.TryGetValue(steamId, out var pref))
				{
					pref.LoadFailed = true;
				}
				LogDbError("An error occurred while fetching player preferences", ex);
			}
		});
	}

	private async Task<int> UpsertCookieAsync(string name, string description, CookieAccess access)
	{
		string query = IsMySQL()
			? @$"INSERT INTO `{Config.TableName}` (name, description, access)
            VALUES (@name, @description, @access)
            ON DUPLICATE KEY UPDATE description = @description, access = @access;"
			: @$"INSERT INTO `{Config.TableName}` (name, description, access)
            VALUES (@name, @description, @access)
            ON CONFLICT(name) DO UPDATE SET description = @description, access = @access;";

		try
		{
			using (var connection = CreateConnection())
			{
				await connection.OpenAsync();

				await connection.ExecuteAsync(query, new { name, description, access = (int)access });

				int id = await connection.QuerySingleAsync<int>(
					@$"SELECT id FROM `{Config.TableName}` WHERE name = @name;",
					new { name });

				DebugLog($"Created playercookie with name {name} (id {id})");
				return id;
			}
		}
		catch (Exception ex)
		{
			LogDbError("An error occurred while creating a player cookie", ex);
			return -1;
		}
	}

	public async Task<int> CreatePlayerCookieNew(string name, string description, CookieAccess access)
	{
		if (!_databaseLoaded)
		{
			Logger.LogError($"CreatePlayerCookieNew called when Database is not loaded yet.");
			return -1;
		}

		return await UpsertCookieAsync(name, description, access);
	}

	private readonly record struct PendingCookieWrite(ClientCookie Cookie, int Id, string? Value);

	private List<PendingCookieWrite> CollectPendingWrites(PlayerSetting setting)
	{
		var writes = new List<PendingCookieWrite>();

		foreach (var cookie in setting.Cookies)
		{
			if (!Cookies.ContainsKey(cookie.Id)) continue;

			var newValue = cookie.NewValue;
			var oldValue = cookie.OldValue;

			if (string.IsNullOrWhiteSpace(newValue))
			{
				if (oldValue != null)
				{
					writes.Add(new PendingCookieWrite(cookie, cookie.Id, null));
				}
				continue;
			}

			if (oldValue == newValue) continue;

			writes.Add(new PendingCookieWrite(cookie, cookie.Id, newValue));
		}

		return writes;
	}

	private Task SavePlayerCookies(string steamId64 = "")
	{
		if (!_databaseLoaded)
		{
			Logger.LogError($"SavePlayerCookies called when Database is not loaded yet.");
			return Task.CompletedTask;
		}

		bool evictAfterSave = !string.IsNullOrEmpty(steamId64);
		var aPlayers = new Dictionary<string, List<PendingCookieWrite>>();

		if (evictAfterSave)
		{
			if (!PlayerSettings.TryGetValue(steamId64, out var single) || !single.Loaded)
			{
				return Task.CompletedTask;
			}

			var writes = CollectPendingWrites(single);
			if (writes.Count == 0)
			{
				EvictIfDisconnected(steamId64);
				return Task.CompletedTask;
			}

			aPlayers.Add(steamId64, writes);
		}
		else
		{
			foreach (var pair in PlayerSettings.ToList())
			{
				if (!pair.Value.Loaded)
				{
					continue;
				}

				var writes = CollectPendingWrites(pair.Value);
				if (writes.Count == 0)
				{
					continue;
				}

				aPlayers.Add(pair.Key, writes);
			}
		}

		if (aPlayers.Count == 0) return Task.CompletedTask;

		int time = GetEpochTime();

		return Task.Run(async () =>
		{
			try
			{
				using (var connection = CreateConnection())
				{
					await connection.OpenAsync();

					string query;

					using (var transaction = await connection.BeginTransactionAsync())
					{
						foreach (var pair in aPlayers)
						{
							var steamId = pair.Key;
							var writes = pair.Value;
							var deleteIds = new List<int>();

							foreach (var write in writes)
							{
								if (write.Value == null)
								{
									deleteIds.Add(write.Id);
									continue;
								}

								query = @$"REPLACE INTO `{Config.TableNamePlayerData}`
                                (steamid, cookie_id, value, timestamp)
                                VALUES (@steam, @id, @value, @timestamp);";

								await connection.ExecuteAsync(query,
								new { steam = steamId, id = write.Id, value = write.Value, timestamp = time },
								transaction: transaction);
								DebugLog($"Saving cookies {write.Id} for {steamId}");
							}

							if (deleteIds.Count > 0)
							{
								query = @$"DELETE FROM `{Config.TableNamePlayerData}`
								WHERE steamid = @steam AND cookie_id IN @ids;";

								await connection.ExecuteAsync(query,
								new { steam = steamId, ids = deleteIds },
								transaction: transaction);
								DebugLog($"Deleting {deleteIds.Count} cookies for {steamId}");
							}
						}
						await transaction.CommitAsync();
						DebugLog($"Saved cookies");
					}
				}

				foreach (var writes in aPlayers.Values)
				{
					foreach (var write in writes)
					{
						write.Cookie.OldValue = write.Value;
					}
				}

				if (evictAfterSave)
				{
					Server.NextWorldUpdate(() => EvictIfDisconnected(steamId64));
				}
			}
			catch (Exception ex)
			{
				LogDbError("An error occurred while saving player(s) data; entries kept for retry", ex);
			}
		});
	}
}