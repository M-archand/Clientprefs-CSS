using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Data.Common;
using Microsoft.Data.Sqlite;


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

	// Retries transient DB connect failures with backoff so OnDatabaseLoaded still fires; misconfiguration is not retried.
	private async Task ConnectDatabaseTableWithRetry()
	{
		if (!IsDatabaseConfigComplete())
		{
			Server.NextWorldUpdate(() => Logger.LogError("Database connection information is missing. Please fill in the information in the config file."));
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
				int currentAttempt = attempt;
				Server.NextWorldUpdate(() => Logger.LogWarning($"Database connection attempt {currentAttempt}/{maxAttempts} failed. Retrying in {delaySeconds}s..."));
				await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
			}
		}

		Server.NextWorldUpdate(() => Logger.LogError($"Database connection failed after {maxAttempts} attempts. Clientprefs is disabled; dependent plugins will not receive OnDatabaseLoaded."));
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
		if (IsMySQL())
		{
			return _mySqlDataSource!.CreateConnection();
		}
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
			Server.NextWorldUpdate(() => Logger.LogError($"Unable to connect to database: {ex.Message}"));
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

		Task.Run(async () =>
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
				Server.NextWorldUpdate(() => Logger.LogError($"An error occurred while fetching player preferences: {ex.Message}"));
			}
		});
	}

	private void GetPlayerCookies(string steamId)
	{
		if (!_databaseLoaded)
		{
			Logger.LogError($"GetPlayerCookies called when Database is not loaded yet.");
			return;
		}

		PlayerSettings.GetOrAdd(steamId, _ => new());

		Task.Run(async () =>
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

					Server.NextWorldUpdate(() =>
					{
						var player = Utilities.GetPlayers().FirstOrDefault(p => p.IsValidPlayer() && p.SteamID.ToString() == steamId);
						if (player == null) return;

						ClientprefsApi.CallOnPlayerCookiesCached(player);
					});
				}
			}
			catch (Exception ex)
			{
				if (PlayerSettings.TryGetValue(steamId, out var pref))
				{
					pref.LoadFailed = true;
				}
				Server.NextWorldUpdate(() => Logger.LogError($"An error occurred while fetching player preferences: {ex.Message}"));
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
			Server.NextWorldUpdate(() => Logger.LogError($"An error occurred while creating a player cookie: {ex.Message}"));
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

	private Task SavePlayerCookies(string steamId64 = "")
	{
		if (!_databaseLoaded)
		{
			Logger.LogError($"SavePlayerCookies called when Database is not loaded yet.");
			return Task.CompletedTask;
		}

		var aPlayers = new Dictionary<string, List<ClientCookie>>();

		if (!string.IsNullOrEmpty(steamId64))
		{
			if (!PlayerSettings.TryGetValue(steamId64, out var single) || !single.Loaded)
			{
				return Task.CompletedTask;
			}

			aPlayers.Add(steamId64, [.. single.Cookies]);
		}
		else
		{
			foreach (var pair in PlayerSettings.ToList())
			{
				var steamId = pair.Key;
				var value = pair.Value;

				if (!value.Loaded)
				{
					continue;
				}

				aPlayers.Add(steamId, [.. value.Cookies]);
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
							var cookies = pair.Value;
							var deleteIds = new List<int>();

							foreach (var pref in cookies)
							{
								if (!Cookies.ContainsKey(pref.Id)) continue;

								var isNewValueEmpty = string.IsNullOrWhiteSpace(pref.NewValue);

								if (isNewValueEmpty)
								{
									if (pref.OldValue != null)
									{
										deleteIds.Add(pref.Id);
									}
									continue;
								}

								if (pref.OldValue == pref.NewValue) continue;

								query = @$"REPLACE INTO `{Config.TableNamePlayerData}`
                                (steamid, cookie_id, value, timestamp)
                                VALUES (@steam, @id, @value, @timestamp);";

								await connection.ExecuteAsync(query,
								new { steam = steamId, id = pref.Id, value = pref.NewValue, timestamp = time },
								transaction: transaction);
								DebugLog($"Saving cookies {pref.Id} for {steamId}");
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

				foreach (var steamId in aPlayers.Keys)
				{
					PlayerSettings.TryRemove(steamId, out _);
				}
			}
			catch (Exception ex)
			{
				Server.NextWorldUpdate(() => Logger.LogError($"An error occurred while saving player(s) data; entries kept for retry: {ex.Message}"));
			}
		});
	}
}