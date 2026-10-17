# Changelog

## [1.2.0](https://github.com/M-archand/Clientprefs-CSS/compare/v1.1.1...v1.2.0) (2026-10-17)


### Bug Fixes

* ship `Microsoft.Data.Sqlite.dll` and the native SQLite library (`runtimes/linux-x64`, `runtimes/win-x64`) in the plugin package; the build previously stripped them, so the default SQLite backend could not load on a clean install ([b399090](https://github.com/M-archand/Clientprefs-CSS/commit/b399090dbc10ff5cd23e0d8d08d316718c5aadc6))
* keep MySQL mode working even if the SQLite assembly is missing, and always log assembly/native library load failures instead of hiding them behind Debug ([b399090](https://github.com/M-archand/Clientprefs-CSS/commit/b399090dbc10ff5cd23e0d8d08d316718c5aadc6))
* the map-end flush no longer evicts connected players' cookie caches, so a rejected `changelevel` or a slow save no longer leaves players with "not loaded" errors, and values set while the flush is running are no longer lost ([5aec56d](https://github.com/M-archand/Clientprefs-CSS/commit/5aec56d6bb8bc171b08730866da736ceaba28d2d))
* a reconnecting player whose cookies are still cached (map change) is no longer reloaded from the database over values set right after connect; `OnPlayerCookiesCached` still fires ([5aec56d](https://github.com/M-archand/Clientprefs-CSS/commit/5aec56d6bb8bc171b08730866da736ceaba28d2d))
* a player who reconnects while their previous disconnect save is still running keeps their cookie cache; the cache is evicted only once the player is no longer connected ([007b416](https://github.com/M-archand/Clientprefs-CSS/commit/007b41674c42f76db39a4ce58a8483f14c5def66))
* the disconnect save waits for the player's cookie load to finish instead of polling a 0.1s timer up to 50 times; a disconnect during a slow load no longer leaves the cache unsaved and unevicted ([ad626f8](https://github.com/M-archand/Clientprefs-CSS/commit/ad626f8a22709fe912ec5d89433392ae39dbc015))
* database rows are read through typed row classes; a NULL cookie description or access no longer yields a null or crashing `Cookie`, and id conversions no longer rely on dynamic casts ([18ed518](https://github.com/M-archand/Clientprefs-CSS/commit/18ed51896309dc7408a241cef7e3fc3378137a82))
* cookies are flushed on every map end through the engine map-end listener, not only when `changelevel`/`map` console commands are used, so engine-initiated map changes (nextlevel, mapgroup rotation, match end) no longer lose changes; unload now also waits for saves already in flight ([fddebb3](https://github.com/M-archand/Clientprefs-CSS/commit/fddebb316864afa8eea8b6d6d777f21a77bb8399))
* `css_cookie <name>` without a value now only shows the cookie instead of setting it to empty (which deleted the row on the next save); a public cookie the player never had before can now be set from the console ([bd38c3c](https://github.com/M-archand/Clientprefs-CSS/commit/bd38c3c14d23c2408f21a84ce45a8ac16ed0928d))
* `OnPlayerCookiesCached` and `OnDatabaseLoaded` subscribers are invoked one by one under their own guard; a consumer plugin that throws is logged (event, method, assembly) and no longer stops delivery to other players or other consumers ([918ce1f](https://github.com/M-archand/Clientprefs-CSS/commit/918ce1fda5f7a8de879faf1810538461c840e473))
* a player connecting in the same moment the database comes up is no longer loaded twice (double `OnPlayerCookiesCached`); the ready flag now flips on the main thread together with the boot batch load, and the batch skips players already loading ([5db8f12](https://github.com/M-archand/Clientprefs-CSS/commit/5db8f123010cad5c793be831e62a4760e285b8a0))
* **security:** MySQL `DatabaseSslmode` now defaults to `preferred` (MySqlConnector's own default); existing configs keep their explicit value. An invalid mode disables the plugin at load with a clear error instead of throwing inside the connect retry loop ([dfd8d00](https://github.com/M-archand/Clientprefs-CSS/commit/dfd8d004a171e33bf845a1d5b12295351a35da91))
* `TableName` / `TableNamePlayerData` are validated at config load (letters, digits, underscores, 1-64 chars, must differ) and backtick-quoted in every statement, so reserved words no longer break table creation ([dfd8d00](https://github.com/M-archand/Clientprefs-CSS/commit/dfd8d004a171e33bf845a1d5b12295351a35da91))
* the per-player disconnect save runs one world update after `player_disconnect`, and a cache entry with unsaved changes is saved again instead of evicted; consumer plugins can set cookies from their own `player_disconnect` handler again (as promised in v1.0.3) without the value being lost or `SetPlayerCookie` throwing "not loaded" ([7503daf](https://github.com/M-archand/Clientprefs-CSS/commit/7503daf8aa8fae2f8e53612e76f02b8c1075a46b))
* a plugin disabled by an invalid config, missing MySQL settings or a database that never came up logs that error once; `player_connect_full`, map end and unload no longer log `Error` on every event while the database is not loaded, and the boot-window "not loaded yet" messages are Debug only ([b32ff72](https://github.com/M-archand/Clientprefs-CSS/commit/b32ff720f147124c5bc464129e12256167e63ced))


### Performance Improvements

* the cookie flush writes dirty rows in multi-row statements of up to 200 rows (`INSERT ... ON DUPLICATE KEY UPDATE` on MySQL, `INSERT ... ON CONFLICT DO UPDATE` on SQLite, one batched `DELETE` for cleared cookies) instead of one `REPLACE` round trip per cookie, so a full map-end or unload flush on a remote MySQL server takes a few round trips instead of hundreds ([99fc15a](https://github.com/M-archand/Clientprefs-CSS/commit/99fc15afd56d74566fb6bf26be9a11fb25db14cc))


### Code Refactoring

* all plugin output goes through the CounterStrikeSharp logger; debug messages drop the manual console colours and background threads no longer marshal log calls to the main thread ([8fbbf33](https://github.com/M-archand/Clientprefs-CSS/commit/8fbbf334c9734bc4fc88e5d4be565b66957d7c76))
* remove the never-implemented cookie settings menu surface (`CookieMenu` / `CookieMenuAction` enums, commented menu stubs, `css_settings`) and the unused `Cookie List`, `Choose Option`, `Client Settings` lang strings. Breaking only for consumers that referenced those enums; none are known ([56f6c2f](https://github.com/M-archand/Clientprefs-CSS/commit/56f6c2f652fbc523d470dc8ccb8b9b31ed4dda8a))


### Config Changes

**Changed defaults**

* `DatabaseSslmode` (`"none"` -> `"preferred"`) - accepts `none`, `preferred`, `required`, `verifyca` or `verifyfull`; existing configs keep their explicit value ([dfd8d00](https://github.com/M-archand/Clientprefs-CSS/commit/dfd8d004a171e33bf845a1d5b12295351a35da91))
* `TableName` (`"css_cookies"` -> `"clientprefs_cookies"`) and `TableNamePlayerData` (`"css_cookies_playerdata"` -> `"clientprefs_playerdata"`) - existing configs keep their explicit value; a regenerated config creates new empty tables, so set the old names back to keep existing cookie data ([0985f45](https://github.com/M-archand/Clientprefs-CSS/commit/0985f454945f20c8e9ee66e5addfc18147370a4d))


### Miscellaneous

* **ci:** verify the release zip contains every runtime dependency before publishing ([b399090](https://github.com/M-archand/Clientprefs-CSS/commit/b399090dbc10ff5cd23e0d8d08d316718c5aadc6))
* **ci:** release notes are read from this version's `CHANGELOG.md` section; bump `actions/checkout` to v7 and `actions/setup-dotnet` to v6 ([a29b93a](https://github.com/M-archand/Clientprefs-CSS/commit/a29b93a747565eb95efbfe4993dfd99412f14f24))
* **deps:** bump MySqlConnector to 2.6.2, Microsoft.Data.Sqlite to 10.0.13 and Dapper to 2.1.89 ([b399090](https://github.com/M-archand/Clientprefs-CSS/commit/b399090dbc10ff5cd23e0d8d08d316718c5aadc6))
* **docs:** move the changelog to `CHANGELOG.md` and update the README config example ([d4b2ac5](https://github.com/M-archand/Clientprefs-CSS/commit/d4b2ac5cbbf0296fc15406f8dcd237034521e73d))

## [1.1.1](https://github.com/M-archand/Clientprefs-CSS/compare/v1.1.0...v1.1.1) (2026-06-18)


### Bug Fixes

* keep player cookies in memory until the database save commits, so a failed save no longer drops pending changes ([16ec9d3](https://github.com/M-archand/Clientprefs-CSS/commit/16ec9d30ae5d9919bb65745dcd0f20a1897ca135))
* cap the unload cookie flush at 30s and print start/end/timeout progress to the console ([1affebe](https://github.com/M-archand/Clientprefs-CSS/commit/1affebed4421184d07621d7be7d97629ff5ebfc1))
* gate database exception details behind the Debug flag and scrub the password from any log output ([7e79835](https://github.com/M-archand/Clientprefs-CSS/commit/7e79835433a266e82c25a12f516f76df2f945b6b))


### Code Refactoring

* hide the unimplemented cookie settings menu API so consumers can't call the crashing stubs ([a7e6006](https://github.com/M-archand/Clientprefs-CSS/commit/a7e600699d01c0dbbbbb746913639ec06a63576d))
* consolidate duplicate FindPlayerCookie logic into a single TryFindCookieId ([3612f2f](https://github.com/M-archand/Clientprefs-CSS/commit/3612f2fc06885adac33d564541e09964a17e1974))


### Miscellaneous

* remove dead code (unused pawn-validation helpers, write-only LatestClientprefID field, redundant constructors) ([cac6cf1](https://github.com/M-archand/Clientprefs-CSS/commit/cac6cf13b5d8ce7fd48bb027159a503471f8667d)) ([e532e6c](https://github.com/M-archand/Clientprefs-CSS/commit/e532e6c3c2abcaf3605e427108279a9f23a923bd)) ([e6edc17](https://github.com/M-archand/Clientprefs-CSS/commit/e6edc175239f5dfc0a80b9c2453d0869001756df))

## [1.1.0](https://github.com/M-archand/Clientprefs-CSS/compare/ac579cb26de291c4496f6a97c27ed730bfac0f1d...v1.1.0) (2026-06-18)


### Features

* async cookie registration via RegPlayerCookieAsync ([678e6de](https://github.com/M-archand/Clientprefs-CSS/commit/678e6de2e0c59c05a3492c860ccda2ce3846b8b6))
* ship the API as a separate shared ClientprefsApi assembly ([ca1fa4d](https://github.com/M-archand/Clientprefs-CSS/commit/ca1fa4d9a4476dd8dd9009f6fd4aa7feefde6a9e))


### Bug Fixes

* preference loss across server restarts by using the database autoincrement id as the cookie id ([551a9cc](https://github.com/M-archand/Clientprefs-CSS/commit/551a9cc7ad7ec0db1f802d5087ca9e9a95d3efbd))
* retry transient database connect failures with backoff at boot so OnDatabaseLoaded still fires ([82cb4b1](https://github.com/M-archand/Clientprefs-CSS/commit/82cb4b1d53a91e81ee936a97056ebc264f0ac8d5))
* enforce max cookie value length on the console set path ([3631c5c](https://github.com/M-archand/Clientprefs-CSS/commit/3631c5c6cd49aa01c369f60f8697975db5308914))
* dispose the MySQL data source on unload to avoid hot-reload leaks ([c0234b4](https://github.com/M-archand/Clientprefs-CSS/commit/c0234b43bcd29c4aaffa7aa7db5cba16ef231a2a))


### Performance Improvements

* batch-load player cookies on connect and enable SQLite WAL mode ([9810259](https://github.com/M-archand/Clientprefs-CSS/commit/98102598c1fa377a00fae81889e25ba41f22041d))


### Code Refactoring

* rebuild for .NET 10 / CSS 369 ([ca1fa4d](https://github.com/M-archand/Clientprefs-CSS/commit/ca1fa4d9a4476dd8dd9009f6fd4aa7feefde6a9e))


### Miscellaneous

* **ci:** add GitHub Actions build & release workflow ([b0fa7f7](https://github.com/M-archand/Clientprefs-CSS/commit/b0fa7f7b965da7d5271fcea6cb172b94ee2b16a4))

## [1.0.7](https://github.com/M-archand/Clientprefs-CSS/commit/916bea71447fc49299696e3c1c4ba4c5921a5ac1) (2026-02-19)


### Bug Fixes

* cases where player cookies were not getting saved


### Code Refactoring

* whole codebase

## [1.0.6](https://github.com/M-archand/Clientprefs-CSS/commit/244806d6922b3dc1232df358a1cc662631a3359f) (2025-05-12)


### Bug Fixes

* exceptions on player cached & disconnect

## [1.0.4](https://github.com/M-archand/Clientprefs-CSS/commit/b27cb43d9505832815ab3e333321e73697c317bc) (2025-01-23)


### Bug Fixes

* CreatePlayerCookie native using wrong parameter name
* SavePlayerCookie missing not operator
* player preferences not getting saved at map change ([ef2c17e](https://github.com/M-archand/Clientprefs-CSS/commit/ef2c17ef4e881591a9dc0029b7ca3d5c4b779a2d))

## [1.0.3](https://github.com/M-archand/Clientprefs-CSS/commit/e95629740ef6bdd1d27b17d27ffbdf530aa27ebd) (2024-08-21)


### Features

* devs can now save clientpref for player at PlayerDisconnect(HookMode.Post) too, as the plugin now saves in database after 0.5 seconds
* add more error logging and a config variable to enable debug logs


### Bug Fixes

* plugin will no longer throw "Native d01e4eb5 was invoked on a non-main thread" on playerConnect ([41ab924](https://github.com/M-archand/Clientprefs-CSS/commit/41ab924aa5d4c68debfef791da8bbd1476a3bd49))

## [1.0.2](https://github.com/M-archand/Clientprefs-CSS/commit/181991cfd5117082a97c0948a41209e8a76aff5d) (2024-07-27)


### Bug Fixes

* dictionary errors for players after map change

## [1.0.1](https://github.com/M-archand/Clientprefs-CSS/commit/3006eceb396c3ae0a1dfdb266401e1d670802b47) (2024-07-21)


### Code Refactoring

* changed hook method of OnClientprefDatabaseReady & OnPlayerCookiesCached. Please re-refer ClientprefsExample
