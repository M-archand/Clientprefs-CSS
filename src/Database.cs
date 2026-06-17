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
                cookie_id int(10) NOT NULL,
                value varchar({IClientprefsApi.COOKIE_MAX_VALUE_LENGTH}),
                timestamp int,
                PRIMARY KEY (steamid, cookie_id)
            )";

			if (IsMySQL())
			{
				if (Config.DatabaseHost == "" || Config.DatabaseName == "" || Config.DatabaseUsername == "" || Config.DatabasePassword == "")
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
                    cookie_id int(10) NOT NULL,
                    value varchar({IClientprefsApi.COOKIE_MAX_VALUE_LENGTH}),
                    timestamp int NOT NULL,
                    PRIMARY KEY (steamid, cookie_id)
                )";
			}

			using (var connection = CreateConnection())
			{
				await connection.OpenAsync();

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
					LatestClientprefID = 0;

					foreach (var row in rows)
					{
						if (Cookies.ContainsKey((int)row.id) || _cookiesByName.ContainsKey((string)row.name)) continue;

						if ((int)row.id > LatestClientprefID)
						{
							LatestClientprefID = (int)row.id;
						}

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

					if (LatestClientprefID > 0)
						LatestClientprefID++;
				}

				_databaseLoaded = true;

				Server.NextWorldUpdate(() =>
				{
					foreach (var p in Utilities.GetPlayers())
					{
						if (p == null || !p.IsValidPlayer())
						{
							continue;
						}

						var steamId = p.SteamID.ToString();
						GetPlayerCookies(steamId);
					}

					// Timer so that other plugin can catch this event else this is called before AllPluginsLoaded
					AddTimer(2.0f, ClientprefsApi.CallOnDatabaseLoaded);

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
						PlayerSettings[steamId].ModifyCookie((int)row.cookie_id, row.value, row.value);
					}

					PlayerSettings[steamId].Loaded = true;

					DebugLog($"Cookies for {steamId} loaded: {PlayerSettings[steamId].CookieCount}");

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

	private async Task<bool> UpsertCookieAsync(string name, string description, CookieAccess access)
	{
		string query = @$"REPLACE INTO `{Config.TableName}`
            (name, description, access)
            VALUES (@name, @description, @access);";

		try
		{
			using (var connection = CreateConnection())
			{
				await connection.OpenAsync();

				await connection.QueryAsync(query, new { name, description, access = (int)access });

				DebugLog($"Created playercookie with name {name}");
			}
		}
		catch (Exception ex)
		{
			Server.NextWorldUpdate(() => Logger.LogError($"An error occurred while creating a player cookie: {ex.Message}"));
			return false;
		}
		return true;
	}

	public async Task<bool> CreatePlayerCookieNew(string name, string description, CookieAccess access)
	{
		if (!_databaseLoaded)
		{
			Logger.LogError($"CreatePlayerCookieNew called when Database is not loaded yet.");
			return false;
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
			if (!PlayerSettings.ContainsKey(steamId64))
			{
				return Task.CompletedTask;
			}

			if (!PlayerSettings[steamId64].Loaded)
			{
				return Task.CompletedTask;
			}

			aPlayers.Add(steamId64, [.. PlayerSettings[steamId64].Cookies]);
			PlayerSettings.TryRemove(steamId64, out _);
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
				PlayerSettings.TryRemove(steamId, out _);
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
								var isNewValueNull = string.IsNullOrWhiteSpace(pref.NewValue);

								if (pref.OldValue == pref.NewValue && !isNewValueNull) continue;

								if (!Cookies.ContainsKey(pref.Id)) continue;

								if (isNewValueNull)
								{
									deleteIds.Add(pref.Id);
									continue;
								}

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
			}
			catch (Exception ex)
			{
				Server.NextWorldUpdate(() => Logger.LogError($"An error occurred while saving player(s) data: {ex.Message}"));
				throw;
			}
		});
	}
}