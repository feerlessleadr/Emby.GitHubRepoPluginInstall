# Controlling the GitHub Repo Plugin Install plugin from Embidextrous

This is the guide for adding this plugin to the Embidextrous Android app (Kotlin, Jetpack Compose, OkHttp 5, kotlinx.serialization). Everything here was checked against a real Emby Server 4.10.1 running the plugin, apart from the items marked **not verified**.

The plugin keeps a list of GitHub repositories, checks their releases, installs new `.dll` files into Emby's plugin folder, and tells you when Emby has to be restarted. The API lets the app see all of that and do everything the plugin's own page can do.

## 1. The short version

- Base URL: `{server}/emby/Plugins/GitHubRepoPluginInstall/...` (use the case shown).
- Auth: `X-Emby-Token: <administrator's token>`. An Emby API key also works for plain calls. Never put the token in a URL.
- Administrators only. No token gives 401; a non-administrator gets 403.
- JSON, PascalCase field names, enums as strings.
- The API is **off until the server owner turns it on** in the plugin's Settings page (*Allow the phone app to control this plugin*). Adding and removing plugins and registries is behind a **second** switch (*...and to add and remove plugins*). The app can read both switches from `GET /info` and can't change them.
- Long operations (updates, checks) return **202 immediately** and are followed by **polling `GET /status`** or by listening for **pushed WebSocket events** (section 6).
- Alerts reach the app through the **events subscription** (section 6), not through Emby session messages: Emby only delivers those to sockets that carry the token in the address, and the app never does that.
- Nothing restarts the server by itself. After an install, `GET /status` has `RestartPending: true` and the app decides whether to call `POST /restart`.

## 2. Conventions (matching the app's other plugins)

| Topic | What the plugin does |
|---|---|
| Token | `X-Emby-Token` header only. |
| HTTPS | Nothing in the plugin depends on it. Use HTTPS in release builds; the plugin never redirects. |
| Field names | PascalCase, like Emby's own. |
| Null fields | A property that is null is **left out** of the JSON. Give every optional field a default in Kotlin. |
| Enums | Strings, listed in full below. A client must map an unknown value to a fallback, because new values can be added without a version bump. |
| Errors | `{ "Title": "...", "Status": 404, "Detail": "..." }` with the real HTTP status. See section 5. |
| IDs | Plugin ids are 32 hex characters; registry ids are a GUID with dashes or `embedded-default`. Anything outside letters, digits and dashes (max 64) is rejected with 400, so validate before building a URL. |
| Size limits | Release notes are at most about 2,000 characters. History is at most 20 runs. `Url` and `RawUrl` at most 300 characters, `Name` and `AssetNameFilter` at most 100, a token at most 255. Request bodies should be small JSON. |
| Time | All timestamps are UTC ISO 8601 (`2026-10-11T23:48:09.6943234Z`). |

Note: errors that Emby itself produces before the plugin runs (a missing or invalid token, or a non-administrator) are Emby's own plain-text bodies with status 401 or 403. The plugin can't change those. Treat any non-JSON error body as "use the status code and show the text".

### How `ApiVersion` changes

`GET /info` returns `ApiVersion` (an integer, currently **1**) and `MinSupportedApiVersion` (currently **1**). It also returns `ApiLevel` (currently 1), the level of the alert features, which can go up for additions the app should know about without breaking anything.

- `ApiVersion` goes up **only for a breaking change**: a removed or renamed field, a changed meaning, or a removed endpoint.
- Adding an endpoint, an optional field or an enum value does **not** change it. So the app must ignore unknown fields (`ignoreUnknownKeys = true`) and handle unknown enum strings.
- When the plugin raises `ApiVersion`, it keeps `MinSupportedApiVersion` at the oldest version it still serves.
- The app should refuse to continue if `ApiVersion < theVersionTheAppWasBuiltFor` (plugin too old, ask the user to update the plugin) or if `MinSupportedApiVersion > theVersionTheAppWasBuiltFor` (app too old).

## 3. Endpoints

All paths below are relative to `/emby/Plugins/GitHubRepoPluginInstall`. "API on" means the first switch; "manage" means the second switch as well. A call that needs a switch which is off gets 403 with `Title` `ApiDisabled` or `ManageDisabled`.

| Method and path | Needs | What it does |
|---|---|---|
| `GET /info` | admin only (works with the API off) | Versions and switch state. Call this first. |
| `GET /status` | API on | Dashboard in one call, including live run progress. |
| `GET /Events?After=&Limit=` | API on | Alerts the app can catch up on, oldest first. See section 6. |
| `GET /Notifications/Catalog` | API on | The alerts this plugin can send. See section 6. |
| `GET /plugins` | API on | All plugins. |
| `GET /plugins/{Id}` | API on | One plugin with its backups. |
| `GET /history?Limit=n` | API on | Last runs, newest first. `Limit` 1 to 20, default 10. |
| `GET /registries` | API on | Registries. |
| `GET /registries/plugins` | API on | Plugins the enabled registries offer. Calls GitHub; can be slow. |
| `GET /settings` | API on | Settings. The GitHub token is never returned. |
| `POST /run` | API on | Start a run. Body `{"Mode": "Auto" \| "All" \| "Check" \| "Available"}`. 202. |
| `POST /plugins/{Id}/update` | API on | Install the newest release of one plugin. 202. |
| `POST /plugins/{Id}/check` | API on | Look for a new release of one plugin. 202. |
| `POST /plugins/{Id}/restore` | API on | Put the newest backup back; turns auto-update off. |
| `POST /plugins/{Id}/settings` | API on | Change auto-update, pre-release and asset filter. Only fields sent change. |
| `POST /restart` | API on | Restart Emby in about 1.5 s. |
| `POST /restart/dismiss` | API on | Clear the restart notice without restarting. |
| `POST /settings` | API on | Save a GitHub token and the restart-after-scheduled-update option. |
| `POST /settings/testtoken` | API on | Check the saved token against GitHub. |
| `POST /registries/{Id}/enabled` | API on | Switch a registry on or off. |
| `POST /plugins` | manage | Add a plugin. |
| `DELETE /plugins/{Id}` | manage | Remove a plugin and delete its file. |
| `POST /registries` | manage | Add a registry. |
| `POST /registries/{Id}` | manage | Edit a registry. |
| `DELETE /registries/{Id}` | manage | Remove a registry (not the built-in one). |

The two API switches themselves can **only** be changed on the plugin's page in the Emby dashboard, so a stolen or buggy app can't widen its own permissions.

### `GET /info`

```json
{
  "PluginVersion": "2026.10.11.1",
  "ApiVersion": 1,
  "MinSupportedApiVersion": 1,
  "ApiLevel": 1,
  "Features": ["events", "catalog"],
  "ApiEnabled": true,
  "AllowManage": false,
  "RunInProgress": false,
  "RestartPending": false
}
```

`AllowManage` is true only when both switches are on. When `ApiEnabled` is false every other call returns 403, so show "Turn on the phone API in the plugin's settings".

### `GET /status`

```json
{
  "Run": { "IsRunning": false, "Total": 0, "Done": 0 },
  "LastRun": {
    "Id": "963a12b45472446eaa1d29acdc4842ba",
    "StartedUtc": "2026-10-11T23:02:11.5764071Z",
    "DurationSeconds": 1.02,
    "Trigger": "Scheduled",
    "Updated": 0, "UpToDate": 1, "Failed": 1, "Waiting": 1,
    "Results": [
      { "PluginId": "5eb67a8b2ffa46c6a627bcd01912b6eb", "Name": "Emby.GitHubRepoPluginInstall",
        "Outcome": "WaitingManual", "ToVersion": "2026.10.11.1",
        "Message": "Auto-update is off for this plugin." }
    ]
  },
  "RestartPending": false,
  "PendingRestart": [],
  "CanSelfRestart": true,
  "Counts": { "Total": 3, "NotChecked": 0, "NoRelease": 1, "UpToDate": 0,
              "UpdateAvailable": 0, "UpdateWaiting": 1, "Failed": 1 },
  "UpdatesReady": 1,
  "ServerTimeUtc": "2026-10-11T23:46:57.0228341Z"
}
```

- `Run` is live. While a run is going: `IsRunning: true`, `Total`, `Done`, `Current` (the plugin being processed), `Trigger` (`Scheduled` or `Manual`), `StartedUtc`.
- `LastRun` is missing until a run that installs (not a check-only run) has happened.
- `UpdatesReady` is the number to show on an "Update N plugins" button: plugins with `Status` `UpdateAvailable` or `UpdateWaiting`.
- `PendingRestart` lists `{ Name, FromVersion, ToVersion }` for each plugin that was changed since Emby last started.
- `CanSelfRestart` is false on setups where Emby can't restart itself; then show "Restart Emby manually" and don't offer the button.

### `GET /plugins` and `GET /plugins/{Id}`

```json
{
  "Items": [
    {
      "Id": "5eb67a8b2ffa46c6a627bcd01912b6eb",
      "Name": "Emby.GitHubRepoPluginInstall",
      "Owner": "feerlessleadr",
      "Url": "https://github.com/feerlessleadr/Emby.GitHubRepoPluginInstall",
      "Status": "UpdateWaiting",
      "StatusText": "Update waiting",
      "AutoUpdate": false,
      "AllowPreRelease": false,
      "AssetNameFilter": "",
      "InstalledVersion": "2026.10.11",
      "LatestVersion": "2026.10.11.1",
      "LatestPublishedUtc": "2026-10-11T22:12:53.0000000Z",
      "LastCheckedUtc": "2026-10-11T23:48:09.6943234Z",
      "LastUpdatedUtc": "2026-10-11T22:30:00.0000000Z",
      "ReleaseNotes": "2026.10.11.1\n\nUpdate only the plugins that have an update...",
      "AssetNames": ["Emby.GitHubRepoPluginInstall.dll"],
      "RestartPending": false
    }
  ]
}
```

`GET /plugins/{Id}` returns one item (no `Items` wrapper) and adds `"Backups": [{ "Version": "2026.10.10", "CreatedUtc": "...", "SizeBytes": 123456 }]`. The list leaves `Backups` out.

`Status` is one of: `NotChecked`, `NoRelease`, `UpToDate`, `UpdateAvailable` (newer release and auto-update on), `UpdateWaiting` (newer release and auto-update off), `Failed`. `LastError` is a plain sentence when the last check or install failed. `InstalledVersion` is null for a plugin the installer hasn't installed itself.

Suggested colours and wording: `Failed` red with `LastError`, `UpdateAvailable` and `UpdateWaiting` amber with an Update button, `UpToDate` green, `NoRelease` and `NotChecked` grey. `StatusText` is ready-made English.

### `POST /run`

Request:

```json
{ "Mode": "Available" }
```

| Mode | Meaning |
|---|---|
| `Auto` | What the daily task does: install for plugins with auto-update on, check the rest. |
| `All` | Install the newest release of every plugin, including those with auto-update off. |
| `Check` | Look for new releases and record them. Installs nothing. |
| `Available` | Install only the plugins that currently show an update (the app's "Update N plugins"). |

Response, status **202**:

```json
{ "Message": "Started. Follow it with GET /status, or listen for the pushed events.", "Total": 3 }
```

Errors: 409 `RunInProgress` if a run is already going, 409 `NothingToUpdate` for `Available` with nothing to do, 400 for an unknown mode.

### `POST /plugins/{Id}/update`, `/check`

No body. 202 with the same shape as above (`Total` is 1). 404 if the id isn't in the list, 409 `RunInProgress` if busy.

### `POST /plugins/{Id}/restore`

No body. Puts the newest backup of the plugin's file back and turns auto-update off for it, so the next daily run doesn't undo it.

```json
{
  "Message": "Restored 2026.10.10 of Emby.GitHubRepoPluginInstall. Auto-update is now off for it.",
  "RestartNeeded": true,
  "Plugin": { "...": "the plugin as in GET /plugins/{Id}" }
}
```

409 `Refused` when there's nothing to restore (`No backup of ... yet`, or the plugin was never installed by this plugin). Restoring right after an update, before a restart, needs no restart (`RestartNeeded: false`).

### `POST /plugins/{Id}/settings`

```json
{ "AutoUpdate": true, "AllowPreRelease": false, "AssetNameFilter": "Emby" }
```

Every field is optional; only fields you send change. Returns `{ "Message", "RestartNeeded": false, "Plugin": {...} }`. `AssetNameFilter` selects which file of a release is used when a release has several (it matches part of the file name); send `""` to clear it. `GET /plugins/{Id}` returns `AssetNames` so the app can offer the files as choices.

### `POST /plugins` (manage)

```json
{ "Url": "owner/repository", "AutoUpdate": true, "AllowPreRelease": false, "AssetNameFilter": "" }
```

`Url` accepts `owner/repository`, `https://github.com/owner/repository`, a `.git` address or a releases-page link. The plugin looks the repository up straight away, so the call can take a few seconds (about 40 s the very first time GitHub is contacted on some networks), and the returned `Plugin` has a real `Status`. A repository that can't be reached is still added, with `Status: "Failed"` and the reason in `LastError`. 400 `InvalidRequest` for a bad address, 409 `AlreadyAdded` for a duplicate.

Use `GET /registries/plugins` to show a picker. Each item is `{ Name, Description, Url, RegistrySource, AlreadyAdded }`; pass its `Url` here.

### `DELETE /plugins/{Id}` (manage)

Removes the entry and **deletes the plugin's file** if it was installed. Always ask the user to confirm. Response: `{ "Message": "Removed X and deleted its file. Restart Emby to finish.", "RestartNeeded": true }`.

### Registries

`GET /registries` -> `{ "Items": [ { "Id": "embedded-default", "Name": "Built-in", "RawUrl": "embedded://default", "Enabled": true, "BuiltIn": true } ] }`.

- `POST /registries` (manage), body `{ "Name": "My registry", "RawUrl": "https://example.com/plugins.json", "Enabled": true }`. `RawUrl` must be https.
- `POST /registries/{Id}` (manage), same body, edits one. The built-in registry can't be edited (409 `Refused`).
- `DELETE /registries/{Id}` (manage). The built-in registry can't be removed (409 `Refused`).
- `POST /registries/{Id}/enabled`, body `{ "Enabled": false }`, works with just the first switch.

A registry is a JSON file: `{ "name": "...", "description": "...", "plugins": [ { "name", "description", "url" } ] }`. Adding a registry is behind the manage switch because it changes which plugins the app offers to install.

### Settings

`GET /settings` -> `{ "TokenSet": true, "RestartServerAfterInstall": false, "ApiEnabled": true, "AllowManage": false }`.

`POST /settings` body (all optional): `{ "Token": "github_pat_...", "ClearToken": false, "RestartServerAfterInstall": false }`. The token is write-only and is stored encrypted. A token raises GitHub's limit from 60 to 5,000 requests per hour and is needed for private repositories. The response is the new settings.

`POST /settings/testtoken` -> `{ "Ok": true, "Message": "No token set. Public repositories work, but GitHub allows only 60 requests per hour without one." }`.

`RestartServerAfterInstall` makes the **daily scheduled task** restart Emby after it installs something. Runs started from the app never restart the server on their own.

## 4. Restarts

- Installing, restoring and removing a plugin file change what is on disk, but Emby keeps running the old code until it restarts.
- `GET /status` -> `RestartPending` and `PendingRestart` say what is waiting. Each action response also carries `RestartNeeded`.
- `POST /restart` restarts Emby after about 1.5 seconds, so the app still gets its reply. The server then goes offline for roughly 15 to 60 seconds; poll `GET /info` until it answers, with a timeout.
- 409 `CannotRestart` when `CanSelfRestart` is false.
- `POST /restart/dismiss` clears the notice (for example if the user restarted another way). The plugin also clears it itself once the server has restarted.

## 5. Errors

Every plugin error is:

```json
{ "Title": "AlreadyAdded", "Status": 409, "Detail": "https://github.com/x/y is already in the list." }
```

`Detail` is a sentence safe to show to the user. Use `Title` for logic.

| Status | Title | When |
|---|---|---|
| 400 | `InvalidRequest` | Bad id, bad URL, missing field, unknown run mode, too long. |
| 401 | (Emby, plain text) | No token, or an invalid or expired token. |
| 403 | `ApiDisabled` | The first switch is off. |
| 403 | `ManageDisabled` | The second switch is off. |
| 403 | (Emby, plain text) | The token isn't an administrator's. |
| 404 | `NotFound` | Unknown plugin or registry id. |
| 409 | `RunInProgress` | A run is already going; wait and retry. |
| 409 | `NothingToUpdate` | `Available` with no updates. |
| 409 | `AlreadyAdded` | The repository is already in the list. |
| 409 | `Refused` | Nothing to restore, or the built-in registry. |
| 409 | `CannotRestart` | Emby can't restart itself here. |
| 500 | `Failed` | Something unexpected; `Detail` has a short reason. |
| 502 | `RegistryUnavailable` | A registry couldn't be read. |

GitHub problems during a check or install (rate limit, bad token, missing repository, checksum mismatch) do **not** fail the HTTP call. They show up as `Status: "Failed"` with `LastError` on the plugin and as a `Failed` outcome in the run's results.

While a run is going, calls that change the plugin list or its files (restore, settings, add, remove) get 409 `RunInProgress`, because a run is writing the same settings.

## 6. Progress and push

### Polling (always works)

`POST` something, then poll `GET /status` every 1 to 2 seconds while `Run.IsRunning` is true. Stop at the first response where it is false, then re-fetch `GET /plugins`. A run that touches one plugin usually finishes in a few seconds; checking many repositories one by one can take much longer.

### Push (while the app is connected to Emby)

The plugin sends messages through Emby's own WebSocket (`/embywebsocket`), to every **administrator session that is open**. The app receives them like any other Emby message:

```json
{ "MessageType": "GitHubRepoPluginInstall.RunProgress",
  "MessageId": "502b8026...",
  "Data": { "Trigger": "Manual", "Total": 3, "Done": 1, "Current": "Emby.GitHubRepoPluginInstall",
            "Updated": 0, "UpToDate": 0, "Failed": 0, "Waiting": 0, "CheckOnly": false } }
```

| `MessageType` | When | Useful `Data` |
|---|---|---|
| `GitHubRepoPluginInstall.RunStarted` | A run begins (scheduled or from anyone's app). | `Trigger`, `Total`, `CheckOnly` |
| `GitHubRepoPluginInstall.RunProgress` | After each plugin. | `Total`, `Done`, `Current`, `CheckOnly` |
| `GitHubRepoPluginInstall.RunCompleted` | A run ends. | `Updated`, `UpToDate`, `Failed`, `Waiting`, `UpdatedPlugins[]`, `FailedPlugins[]`, `CheckOnly` |
| `GitHubRepoPluginInstall.RestartPending` | A run installed something, or a restore or removal needs a restart. | `UpdatedPlugins[]` |
| `GitHubRepoPluginInstall.PluginsChanged` | Settings, restore, add, remove, registry changes. | `Reason`, `PluginId`, `PluginName`, `RestartNeeded` |

Things to know:

- **These session messages do not reach Embidextrous.** Emby links a websocket to an administrator session only when the token is in the websocket's address (`?api_key=`). Embidextrous sends its token in the `X-Emby-Token` header and never in a URL, so it receives none of the messages in this table. Use the events subscription below instead. The session messages still reach Emby's own web admin pages.
- Messages are only sent while the owner has the API switched on.
- They are **fire and forget**: nothing is queued for an app that is offline or asleep. On resume, call `GET /status` to catch up.
- Treat each message as a hint to refresh from `GET /status` and `GET /plugins` rather than trusting it as the only source of truth.
- Push only reaches the app while it holds an Emby WebSocket. For notifications when the app is closed, there is no server-side push service in the plugin (no FCM). Use WorkManager to run `GET /status` every 15 to 30 minutes and raise a local notification when `UpdatesReady` grows, `RestartPending` turns true, or `LastRun.Failed > 0` for a run you haven't shown. **Not verified** in the app.

Notification ideas that fit the events: "3 plugin updates ready" (`UpdatesReady` or a `RunCompleted` with `CheckOnly`), "Restart needed to finish updating X" (`RestartPending`), "Plugin update failed: X" (`RunCompleted.FailedPlugins`), "Update run finished".

### Alerts for the app: the events log, the catalog and the subscription

This is how Embidextrous gets alerts (plugin updates finished, a restart is needed). It follows the push contract the app uses for all its plugins, and it works with the token in the `X-Emby-Token` header only.

**What is logged.** Every time the plugin raises `RunStarted`, `RunCompleted`, `RestartPending` or `PluginsChanged` (the same events as in the table above) it also writes the event to a log of the last 500 events, kept in two files in Emby's plugin configuration folder: `GitHub Repo Plugin Install.events.json` and a separate `GitHub Repo Plugin Install.events.seq` that holds the last number handed out, so `Seq` never goes back, even if the log is deleted or damaged. `RunProgress` is not logged: it is a ticker, and `GET /status` has the same information. Nothing is logged or sent while the phone API switch is off. No file paths appear in any payload; the plugin names the plugins and nothing else.

**Every logged event** looks like this. `Data` has five common fields and then the fields of that event; fields that don't apply are left out.

```json
{ "Seq": 12, "Name": "GitHubRepoPluginInstall.RunCompleted", "AtUtc": "2026-10-14T18:30:12.1234567Z",
  "Data": { "ApiLevel": 1, "Seq": 12, "EventId": "5b1d2b2a-9a4e-4a3e-8d5e-0d6c8b6e0001", "ServerTimeUtc": "2026-10-14T18:30:12.1234567Z",
            "Event": "RunCompleted", "Trigger": "Scheduled", "Total": 5, "Done": 5, "CheckOnly": false,
            "Updated": 1, "UpToDate": 3, "Failed": 1, "Waiting": 0,
            "UpdatedPlugins": ["Example.Plugin"], "FailedPlugins": ["Other.Plugin"] } }
```

| `Name` | When | Event fields in `Data` |
|---|---|---|
| `GitHubRepoPluginInstall.RunStarted` | A run begins. | `Trigger`, `Total`, `CheckOnly` |
| `GitHubRepoPluginInstall.RunCompleted` | A run ends. | `Trigger`, `Total`, `Done`, `CheckOnly`, `Updated`, `UpToDate`, `Failed`, `Waiting`, `UpdatedPlugins[]`, `FailedPlugins[]` |
| `GitHubRepoPluginInstall.RestartPending` | A run installed something, or a restore or removal needs a restart. | `Trigger` (runs only), `Updated`, `UpdatedPlugins[]` |
| `GitHubRepoPluginInstall.PluginsChanged` | Add, remove, restore, settings, registry changes, restart dismissed. | `Reason`, `PluginId`, `PluginName`, `RestartNeeded` |

**Catch up: `GET /Events?After=<seq>&Limit=100`** (API on, administrator). Returns `{ "ApiLevel": 1, "LatestSeq": 12, "Truncated": false, "Events": [ ...as above... ] }`, oldest first. `After` defaults to 0 and `Limit` to 100 (1 to 500). `Truncated: true` means the log no longer reaches back to `After + 1` (or the plugin's data was reset and `After` is ahead of `LatestSeq`); read `GET /status` instead. The app dedupes by `Seq`.

**The catalog: `GET /Notifications/Catalog`** (API on, administrator). The service is `Id` `GitHubRepoPluginInstall`, `Title` "GitHub Repo Plugin Install", `ChannelId` `plugin-updates`, `ChannelTitle` "Plugin updates". It lists two alerts, so the app's settings screen can offer rules for them:

| Event `Id` | `Title` | Rule parameters | Default |
|---|---|---|---|
| `GitHubRepoPluginInstall.RunCompleted` | Plugin update run finished | `OnlyWhenSomethingHappened` (bool, default true): the app sends the alert only when `Updated > 0` or `Failed > 0` | on |
| `GitHubRepoPluginInstall.RestartPending` | Emby needs a restart | none | on |

`RunStarted` and `PluginsChanged` are in the log for catching up, but they are not alerts, so they are not in the catalog. Both catalog events have `OpensTab` `GitHubRepoPluginInstall`.

**`GET /info`** reports `ApiLevel` (1) and `Features`, which includes `"events"` and `"catalog"`. `ApiVersion` did not change.

**The subscription.** Connect to `/embywebsocket` the way the app always does (token in the `X-Emby-Token` header, `deviceId` in the address, no `api_key`), then send:

```json
{ "MessageType": "GitHubRepoPluginInstallEventsStart", "Data": "0,30000,12,<administrator access token>" }
```

The first two values are the usual Emby ones (`0` start delay, `30000` interval). The third is the highest `Seq` the app has already seen (`0` if none; with `0` the first push holds every logged event). The fourth is the signed-in administrator's access token. Emby fills in the websocket's user only when the token is in the address, so a header-authenticated socket looks like nobody; the plugin therefore checks the token from the message itself through Emby's session manager (session, then user, then administrator) and re-checks it on every push. The plugin never logs, echoes or saves the token. Use wss in release builds, since the token travels in the message body. To stop: `{ "MessageType": "GitHubRepoPluginInstallEventsStop" }`. The plugin answers with messages named `GitHubRepoPluginInstallEvents`:

```json
{ "MessageType": "GitHubRepoPluginInstallEvents",
  "Data": { "ApiLevel": 1, "LatestSeq": 13, "Truncated": false,
            "Events": [ { "Seq": 13, "Name": "GitHubRepoPluginInstall.RestartPending", "AtUtc": "...", "Data": { "...": "..." } } ] } }
```

- Right after the subscription: the events after the third value (all of them for `0`). If the third value is left out, the list is empty and only `LatestSeq` is the baseline.
- Within about a second of every new event, only to subscribed connections.
- Every 30 seconds when nothing else was sent, a heartbeat with `"Events": []`, so the app can tell a dead connection from a quiet one.
- Per connection, the plugin remembers the highest `Seq` it sent, so nothing is sent twice on one connection. A second `Start` on the same socket is ignored.
- Only administrators may subscribe: a token that is missing, unknown or not an administrator's (and a socket whose user is not an administrator) gets one message `{ "MessageType": "GitHubRepoPluginInstallEvents", "Data": { "Error": "Administrator login required." } }` and nothing more.
- The phone API switch is checked on every push. When it is off the plugin sends one `Data` of `{ "Error": "The phone API is switched off in the plugin's settings." }` and no events; when it is switched on again the subscription carries on with the events after the last one sent.
- After a reconnect, send `Start` again with the last `Seq` seen, or call `GET /Events`.

Kotlin types for the alert messages (everything optional gets a default, because fields that don't apply are left out):

```kotlin
@Serializable data class AlertEvent(
    @SerialName("Seq") val seq: Long = 0,
    @SerialName("Name") val name: String = "",
    @SerialName("AtUtc") val atUtc: String = "",
    @SerialName("Data") val data: JsonObject = JsonObject(emptyMap()),   // ApiLevel, Seq, EventId, ServerTimeUtc, Event, then the event's own fields
)

@Serializable data class AlertsPush(          // Data of GitHubRepoPluginInstallEvents, and the body of GET /Events
    @SerialName("ApiLevel") val apiLevel: Int = 0,
    @SerialName("LatestSeq") val latestSeq: Long = 0,
    @SerialName("Truncated") val truncated: Boolean = false,
    @SerialName("Events") val events: List<AlertEvent> = emptyList(),
    @SerialName("Error") val error: String? = null,   // set (and nothing else) when the plugin refuses the subscription
)
```

## 7. How updates are checked and installed

1. A **check** asks GitHub for a repository's releases (`GET /repos/{owner}/{repo}/releases`), picks the newest (pre-releases only if `AllowPreRelease`), and records its tag, notes, date and file names. Nothing is installed.
2. An **install** picks the release file: a `.dll`, or a `.zip` containing a `.dll`. GitHub's automatic source-code zips are ignored. `AssetNameFilter` narrows the choice when a release has several files.
3. If the release publishes a SHA-256 (GitHub's `digest` on the asset, a `.sha256` file or a `SHA256SUMS` file), the download is checked against it. A mismatch stops the install and leaves the working plugin alone. Releases without a hash are installed and the activity log says they weren't verified.
4. The current file is copied to `plugins/plugin-backups/` (three newest kept per plugin), then replaced. If Emby has the file locked (Windows), the old file is renamed `*.old` and cleaned up on the next run.
5. The plugin records the new version and marks a restart pending. Emby loads the new version after a restart.
6. The daily scheduled task ("Update Plugins From Github Repos") does `Auto`: installs where auto-update is on and checks the rest. Manual runs from the app never restart the server on their own.

GitHub allows 60 requests per hour without a token and 5,000 with one, so a few repositories checked often are fine but the app shouldn't loop on `POST /run`. A rate-limit hit shows as `Failed` plugins with a message that includes the reset time.

## 8. Kotlin types

```kotlin
@Serializable data class Info(
    @SerialName("PluginVersion") val pluginVersion: String = "",
    @SerialName("ApiVersion") val apiVersion: Int = 0,
    @SerialName("MinSupportedApiVersion") val minSupportedApiVersion: Int = 0,
    @SerialName("ApiLevel") val apiLevel: Int = 0,
    @SerialName("Features") val features: List<String> = emptyList(),
    @SerialName("ApiEnabled") val apiEnabled: Boolean = false,
    @SerialName("AllowManage") val allowManage: Boolean = false,
    @SerialName("RunInProgress") val runInProgress: Boolean = false,
    @SerialName("RestartPending") val restartPending: Boolean = false,
)

enum class PluginStatus { NotChecked, NoRelease, UpToDate, UpdateAvailable, UpdateWaiting, Failed, Unknown }

@Serializable data class Backup(
    @SerialName("Version") val version: String? = null,
    @SerialName("CreatedUtc") val createdUtc: String = "",
    @SerialName("SizeBytes") val sizeBytes: Long = 0,
)

@Serializable data class Plugin(
    @SerialName("Id") val id: String,
    @SerialName("Name") val name: String = "",
    @SerialName("Owner") val owner: String = "",
    @SerialName("Url") val url: String = "",
    @SerialName("Status") val status: String = "NotChecked",   // map with PluginStatus.valueOf, else Unknown
    @SerialName("StatusText") val statusText: String = "",
    @SerialName("AutoUpdate") val autoUpdate: Boolean = false,
    @SerialName("AllowPreRelease") val allowPreRelease: Boolean = false,
    @SerialName("AssetNameFilter") val assetNameFilter: String = "",
    @SerialName("InstalledVersion") val installedVersion: String? = null,
    @SerialName("LatestVersion") val latestVersion: String? = null,
    @SerialName("LatestPublishedUtc") val latestPublishedUtc: String? = null,
    @SerialName("LastCheckedUtc") val lastCheckedUtc: String? = null,
    @SerialName("LastUpdatedUtc") val lastUpdatedUtc: String? = null,
    @SerialName("LastError") val lastError: String? = null,
    @SerialName("ReleaseNotes") val releaseNotes: String? = null,
    @SerialName("AssetNames") val assetNames: List<String> = emptyList(),
    @SerialName("RestartPending") val restartPending: Boolean = false,
    @SerialName("Backups") val backups: List<Backup>? = null,
)

@Serializable data class PluginList(@SerialName("Items") val items: List<Plugin> = emptyList())

@Serializable data class RunProgress(
    @SerialName("IsRunning") val isRunning: Boolean = false,
    @SerialName("Total") val total: Int = 0,
    @SerialName("Done") val done: Int = 0,
    @SerialName("Current") val current: String? = null,
    @SerialName("Trigger") val trigger: String? = null,
    @SerialName("StartedUtc") val startedUtc: String? = null,
)

@Serializable data class RunResult(
    @SerialName("PluginId") val pluginId: String = "",
    @SerialName("Name") val name: String = "",
    @SerialName("Outcome") val outcome: String = "",   // UpToDate, Updated, Failed, WaitingManual, NoRelease
    @SerialName("FromVersion") val fromVersion: String? = null,
    @SerialName("ToVersion") val toVersion: String? = null,
    @SerialName("Message") val message: String? = null,
)

@Serializable data class Run(
    @SerialName("Id") val id: String = "",
    @SerialName("StartedUtc") val startedUtc: String = "",
    @SerialName("DurationSeconds") val durationSeconds: Double = 0.0,
    @SerialName("Trigger") val trigger: String = "",   // Scheduled, Manual
    @SerialName("Updated") val updated: Int = 0,
    @SerialName("UpToDate") val upToDate: Int = 0,
    @SerialName("Failed") val failed: Int = 0,
    @SerialName("Waiting") val waiting: Int = 0,
    @SerialName("Results") val results: List<RunResult> = emptyList(),
)

@Serializable data class PendingRestartItem(
    @SerialName("Name") val name: String = "",
    @SerialName("FromVersion") val fromVersion: String? = null,
    @SerialName("ToVersion") val toVersion: String? = null,
)

@Serializable data class Counts(
    @SerialName("Total") val total: Int = 0, @SerialName("NotChecked") val notChecked: Int = 0,
    @SerialName("NoRelease") val noRelease: Int = 0, @SerialName("UpToDate") val upToDate: Int = 0,
    @SerialName("UpdateAvailable") val updateAvailable: Int = 0, @SerialName("UpdateWaiting") val updateWaiting: Int = 0,
    @SerialName("Failed") val failed: Int = 0,
)

@Serializable data class Status(
    @SerialName("Run") val run: RunProgress = RunProgress(),
    @SerialName("LastRun") val lastRun: Run? = null,
    @SerialName("RestartPending") val restartPending: Boolean = false,
    @SerialName("PendingRestart") val pendingRestart: List<PendingRestartItem> = emptyList(),
    @SerialName("CanSelfRestart") val canSelfRestart: Boolean = false,
    @SerialName("Counts") val counts: Counts = Counts(),
    @SerialName("UpdatesReady") val updatesReady: Int = 0,
    @SerialName("ServerTimeUtc") val serverTimeUtc: String = "",
)

@Serializable data class RunRequest(@SerialName("Mode") val mode: String)   // Auto, All, Check, Available
@Serializable data class RunAccepted(@SerialName("Message") val message: String = "", @SerialName("Total") val total: Int = 0)

@Serializable data class ActionResult(
    @SerialName("Message") val message: String = "",
    @SerialName("RestartNeeded") val restartNeeded: Boolean = false,
    @SerialName("Plugin") val plugin: Plugin? = null,
)

@Serializable data class PluginSettingsRequest(
    @SerialName("AutoUpdate") val autoUpdate: Boolean? = null,
    @SerialName("AllowPreRelease") val allowPreRelease: Boolean? = null,
    @SerialName("AssetNameFilter") val assetNameFilter: String? = null,
)   // encode with explicitNulls = false so unset fields are left out

@Serializable data class AddPluginRequest(
    @SerialName("Url") val url: String,
    @SerialName("AutoUpdate") val autoUpdate: Boolean = true,
    @SerialName("AllowPreRelease") val allowPreRelease: Boolean = false,
    @SerialName("AssetNameFilter") val assetNameFilter: String = "",
)

@Serializable data class AvailablePlugin(
    @SerialName("Name") val name: String = "", @SerialName("Description") val description: String? = null,
    @SerialName("Url") val url: String = "", @SerialName("RegistrySource") val registrySource: String? = null,
    @SerialName("AlreadyAdded") val alreadyAdded: Boolean = false,
)
@Serializable data class AvailablePlugins(@SerialName("Items") val items: List<AvailablePlugin> = emptyList())

@Serializable data class Registry(
    @SerialName("Id") val id: String = "", @SerialName("Name") val name: String = "",
    @SerialName("RawUrl") val rawUrl: String = "", @SerialName("Enabled") val enabled: Boolean = true,
    @SerialName("BuiltIn") val builtIn: Boolean = false,
)
@Serializable data class RegistryList(@SerialName("Items") val items: List<Registry> = emptyList())

@Serializable data class Settings(
    @SerialName("TokenSet") val tokenSet: Boolean = false,
    @SerialName("RestartServerAfterInstall") val restartServerAfterInstall: Boolean = false,
    @SerialName("ApiEnabled") val apiEnabled: Boolean = false,
    @SerialName("AllowManage") val allowManage: Boolean = false,
)

@Serializable data class TokenTest(@SerialName("Ok") val ok: Boolean = false, @SerialName("Message") val message: String = "")

@Serializable data class ApiErrorBody(
    @SerialName("Title") val title: String = "",
    @SerialName("Status") val status: Int = 0,
    @SerialName("Detail") val detail: String = "",
)

// Use one Json instance for everything
val pluginJson = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
```

A small client sketch (OkHttp 5, coroutines):

```kotlin
class GhrpiClient(private val http: OkHttpClient, private val server: String, private val token: () -> String) {
    private val base get() = "$server/emby/Plugins/GitHubRepoPluginInstall"

    private suspend fun call(method: String, path: String, body: String? = null): Response = withContext(Dispatchers.IO) {
        val req = Request.Builder().url("$base$path")
            .header("X-Emby-Token", token())
            .method(method, body?.toRequestBody("application/json".toMediaType()))
            .build()
        http.newCall(req).execute()            // build the client with followRedirects(false) and a 60 s read timeout
    }

    private fun requireId(id: String) = require(Regex("^[A-Za-z0-9-]{1,64}$").matches(id)) { "bad id" }

    suspend fun info(): Info = call("GET", "/info").use { pluginJson.decodeFromString(it.body!!.string()) }
    suspend fun status(): Status = ...
    suspend fun updateOne(id: String): RunAccepted { requireId(id); ... call("POST", "/plugins/$id/update") }
}

// While a run is going
fun statusFlow(client: GhrpiClient): Flow<Status> = flow {
    while (true) {
        val s = client.status()
        emit(s)
        if (!s.run.isRunning) break
        delay(1500)
    }
}
```

Error handling: read the body only when `!response.isSuccessful`; if it parses as `ApiErrorBody` use it, otherwise show the status code and body text (Emby's own 401 and 403).

## 9. Suggested screens

- **Dashboard card** (from `GET /status`): "3 plugins - 1 update ready - 1 failed". Buttons: **Check for updates** (`POST /run` `Check`), **Update N plugins** (`Available`, shown only when `UpdatesReady > 0`, confirm with the names), **Update everything** (`All`, confirm). Show a progress bar from `Run.Done / Run.Total` with `Run.Current`. A banner when `RestartPending`, listing `PendingRestart`, with **Restart now** (confirm) and **Dismiss**.
- **Plugin list** (from `GET /plugins`): problems first, then updates, then the rest. Each row: name, `InstalledVersion -> LatestVersion`, status chip, an **Update** or **Retry** button.
- **Plugin detail** (`GET /plugins/{Id}`): release notes, auto-update switch, pre-release switch, asset filter with the `AssetNames` as choices, **Restore previous version** (shows `Backups`, confirm), and **Remove** (manage only, confirm, warns that the file is deleted).
- **Add plugin** (manage only): `GET /registries/plugins` as a searchable list with `AlreadyAdded` greyed out, plus a free-text `owner/repository` field. After adding, show the returned plugin's status.
- **History** (`GET /history`): each run with the result for every plugin.
- **Settings**: token status and **Test token** (`/settings/testtoken`), **Save token**, the restart-after-scheduled-update switch. Show `ApiEnabled` and `AllowManage` as read-only and say they are changed in the Emby dashboard.
- **Registries** (manage only for add, edit, remove): list with an enabled switch for each.

## 10. Gotchas

- The first GitHub request after Emby starts can take around 40 seconds on some networks (certificate validation). Use a long read timeout (60 s) for `POST /plugins`, `GET /registries/plugins` and `POST /settings/testtoken`. `POST /run` and the per-plugin update and check calls return at once, so they are not affected.
- Two apps, or the Emby dashboard, can change things at once. Always re-fetch after an action rather than patching local state, and handle 409.
- A plugin's `Id` is stable until it is removed. Don't use `Name` as a key; two entries can't share one file, but a rename of the repository changes `Name`.
- The server must be reachable over your VPN address (`https://emby.int.thecgkg.com`) or the public address (`https://emby.thecgkg.com`). The API is identical on both. If your reverse proxy blocks `PATCH`, `PUT` or `DELETE`, note that this API uses only `GET`, `POST` and `DELETE`. WebSockets need the proxy to allow upgrade requests on `/embywebsocket` (Emby clients already depend on that).
- Emby's own 401 and 403 are plain text, not JSON.

## 11. Prompt to hand to the other Claude

> I am adding a section to my Android app (Kotlin, Jetpack Compose, Material 3, OkHttp 5, kotlinx.serialization, coroutines and StateFlow, min API 26) for an Emby plugin called "GitHub Repo Plugin Install". It lets an administrator see which of their Emby plugins have updates on GitHub, update, check, restore, add and remove them, and restart Emby. Read `docs/EMBIDEXTROUS-INTEGRATION.md` from the plugin repository (https://github.com/feerlessleadr/Emby.GitHubRepoPluginInstall) and implement it exactly as written: use the types in section 8, follow the conventions in section 2 (including refusing an `ApiVersion` the app doesn't support), poll `GET /status` for runs, listen on the existing Emby WebSocket for the `GitHubRepoPluginInstall.*` messages as hints to refresh, and build the screens in section 9. The API is off until the server owner switches it on in the plugin's Settings page, and adding or removing plugins needs a second switch; both are shown by `GET /info` and the app must explain when they are off. Match my other plugin integrations for code style, error handling and navigation. Ask me before changing anything outside the new plugin section.
