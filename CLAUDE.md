# Working notes for this repository

An Emby plugin (net8.0) that installs and updates other Emby plugins from GitHub releases. Code is in `Emby.GitHubRepoPluginInstall`, tests in `Emby.GitHubRepoPluginInstall.Tests`. Read `ReadMe.md` and `docs/EMBIDEXTROUS-INTEGRATION.md` first.

## Build and test
- `dotnet build Emby.GitHubRepoPluginInstall.sln` and `dotnet test Emby.GitHubRepoPluginInstall.Tests`.
- `global.json` asks for SDK 8.0.x with `latestMinor`. A machine that has only a newer SDK needs `rollForward` changed to `latestMajor` temporarily; do not commit that change.

## Releases
- The version comes from the git tag, not from a file. Tags are dates (`2026.10.14`, or `2026.10.14.1`), optionally with a leading `v`. Pushing a tag runs `.github/workflows/release.yml`, which tests and then publishes a release with `Emby.GitHubRepoPluginInstall.dll` attached. The release notes are the annotated tag's message.
- Do not tag or publish unless the owner asks.

## The phone API and alerts (Embidextrous)
- Routes live under `/emby/Plugins/GitHubRepoPluginInstall`, administrators only, and are off until the owner turns on the switch in the plugin's settings. The same switch gates alerts.
- `ApiVersion` changes only for a breaking change. `ApiLevel` (in `ApiVersions.Level`) is the level of the alert features and is carried by every pushed event.
- The update code raises events through `RunEvents.Raise`. `PushBridge` receives them: it logs the four that are alerts (`EventRecorder`, into `EventLogStore`), tells the websocket listener (`EventsPushListener`) and also sends the old session message. Emby delivers session messages only to sockets with `?api_key=` in the address, which the app never uses, so the app depends on the log, the `Events` route and the subscription.
- The subscription rules (who may subscribe, first push, heartbeat, per-connection last-sent `Seq`) are in `EventsSubscription`, which has no Emby types in its logic so it is unit tested. The listener is a thin shell around it.
- The app's header-authenticated socket has an empty `connection.User`, so the start message body carries the administrator's token (`"0,30000,<afterSeq>,<token>"`). `EventsPushListener.TokenIsAdministrator` checks it through `ISessionManager` and `IUserManager` on every push. Never log, echo or save the token.
- Event payloads must never contain file paths. `EventRecorder.Scrub` removes anything path-like.
- Sequence numbers are in a separate `.events.seq` file so they never go back.

## Conventions
- Plain American English in docs and messages. No file paths, LAN addresses, share names or user names in tracked files.
- Commits are authored as `feerlessleadr <github@thecgkg.com>`; the message ends with the Co-Authored-By line the session gives.
- `scripts/Test-EventsSubscription.ps1` connects like the app (token only in the `X-Emby-Token` header) for live checks against a real server.
