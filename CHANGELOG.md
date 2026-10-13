# Release notes

## v3.0.0

### Added
- **The wire DTOs live here now**, and so do the platform contracts. 48 DTO types moved in from
  the contracts package, along with `IPlatformContext`, `IPlatformAuthentication`, the five
  value types beside them and the application-lifecycle pair `IApplicationManager` /
  `EApplicationState` — the whole of what used to be the `Virtuademy.SDK.Core` assembly.

  They came here because this is where their implementation and their audience already were:
  `PlatformContextProjection` is in this package, `PlatformContext` is in the realtime package
  which references this one, and an external app developer installs this package. Every consumer
  of the two retired assemblies sat at or below this one, so nothing changed direction.

  The seventeen analytics types did **not** come: a creator authoring an xAPI statement in a
  Visual Scripting graph needs them, and they now live in `Virtuademy.ScriptingApi`, which a
  creator installs and this package does not have to be.

  Namespaces are unchanged — `Virtuademy.SDK.ApiData`, `Virtuademy.SDK.Interface` and
  `Virtuademy.SDK.Core.ApplicationManagement` — so every `using` across the project still
  resolves; only the assembly behind them differs.
- **The WebSocket stack lives here, whole.** `WebSocketClient` (one connection per address,
  opened on demand and held until it is disconnected), `WebSocketHandler`,
  `WebGLWebSocketHandler` with its `webSocketClient.jslib` plugin and `WebSocketMessagesHandler`,
  `IWebSocketHandler`, `IWebSocketListener` and `BaseWebSocketListener` came over from the
  package a creator installs, so a creator project no longer contains a socket at all. The
  registry is per instance: two clients asking for the same address get two sockets. The realtime
  protocol client is not here; it stays in `Virtuademy-SDK-RealtimeApi`.
- **An application the platform did not start can sign a user in, with no framework under it.**
  `PlatformAuthentication` is the first implementation of `IPlatformAuthentication`.
  - `BeginLogin` / `CompleteLoginWithCode` is the **code flow** a headset has always used and the
    only one that needs nothing of the host: the app shows an address and eight characters, the
    user types them on any other device, and the page answers with four characters they type back.
  - `CompleteLoginWithAccessToken` is the **token completion**, for a host that can read the
    redirect: it presents the identity provider's access token to `my/sessions/bind`, the one
    endpoint of the login that is not HMAC, and the platform answers with the same check code.
    From there the two completions are one.
  - `PlatformAuthentication.ForTenant(TenantConfigurationClient)` builds the login from the
    tenant: which profile API, which version and which address the user opens come from the
    tenant, so one build serves more than one. The constructor taking those as strings stays for
    a host that resolved them by other means.
  - `Tokens` is the `ITokenProvider` the login produced (the `ProfileClient` itself), so an app
    that has signed in can call the other APIs.
  - The session is **not persisted**: whether to keep one across runs is the app's policy. An app
    that does keeps the hash and calls `RestoreSession`.
- **`ProfileClient`**, the profile API reduced to the six endpoints signing a user in needs
  (`BeginSession`, `EnableSession`, `BindSession`, `GetSessionByHash`, `KillSession`,
  `KeepAlive`). All but `BindSession` are HMAC, which is what makes the flow possible with no user
  token yet. `PlatformSession` is the login session as it reports it.
- **`IdpLogin`: sign in at the tenant's identity provider** — authorization code with PKCE,
  written out rather than taken from a library, because MSAL is a desktop .NET library that an app
  shipping to a headset or a browser cannot take. No client secret, by construction. Authority,
  policy, audience and client id (the app's own app id) all come from the tenant; nothing about an
  identity provider is compiled into an application. `IdpLogin.ForTenant` builds it,
  `AuthorizeUrl` is the address to open and `CompleteWithRedirect` takes the address the provider
  sent the user back to and returns the access token for `CompleteLoginWithAccessToken`.
- **The redirect comes back the way the host can receive it.** `IRedirectReceiver` has two
  implementations, picked by `RedirectReceiver.ForThisPlatform(appId)`: `LoopbackRedirect`
  (a free port on `http://localhost`, desktop and the editor whatever its build target) and
  `DeepLinkRedirect` (the app's own `v<app id>` scheme, heard through
  `Application.deepLinkActivated`, on Android and iOS). The scheme is the one the build already
  claims for platform launches, so there is nothing extra to declare on Android.

  Two things are outside the code. The provider must have `http://localhost` (loopback) or
  `v<app id>://auth` (mobile) registered against the client id, or it refuses before the browser
  opens (Azure B2C answers `AADB2C90006`). On iOS the scheme must be declared in
  `CFBundleURLTypes`, which nothing in this package does: without it the OS never delivers the
  redirect and the login waits forever, and `DeepLinkRedirect` logs a warning saying so. Where
  `HttpListener` is not supported (mobile, WebGL) `LoopbackRedirect` throws
  `PlatformNotSupportedException`; a web build reads its own address and passes it to
  `IdpLogin.CompleteWithRedirect` directly.
- **`PlatformLaunch` reads the session the platform launched the app with.** `Current` parses
  `Application.absoluteURL` (the deep link on Android and iOS, the page address on WebGL) into the
  `PlatformLaunchData` the contract already describes (`authSessionHash`, `worldId`,
  `experienceId`), and `Received` fires for a launch that arrives while the app is running, since
  a second launch can carry a different user.
- **`PlatformClient.IsConfigured`** says whether anything has given the client an address.

### Changed
- **The package is `Virtuademy-SDK-Library`** (id `com.anotherealitysrl.virtuademy-sdk-library`),
  and the namespace `Virtuademy.SDK.PlatformApi` is `Virtuademy.SDK.ApiData`. What is left here
  once the wire DTOs and the transport moved in is the client an application embedding a world
  needs: the endpoints an app calls, the sign-in flows and the WebSocket stack. Nothing here is
  something a creator installs.
- **`PlatformClient` is the client, and it exists before anybody initialises it.**
  `PlatformClient.Current` creates the instance on first use rather than waiting for a system to
  install one, because the application's systems are initialised in an order nothing here controls
  and more than one of them takes this reference in its own `Init`. The system that owns the
  connection configures it afterwards. Its `DiscoveryApiType` is `"Application"`, so its base URL
  is resolved from the platform record, falling back to the value serialised into the build.
  `PlatformClient.Install` replaces it, for an external app with no platform system or for a test.
- **`IPlatformAuthentication` gained `RestoreSession(string sessionHash)`.** It is the whole of the
  catalog path (the platform hands over a session hash and the app takes it up), and an app
  written against the contract had no way to accept the handover. A hash that arrived with the
  launch is taken before a persisted one.
- The declared dependency `com.anotherealitysrl.virtuademy-sdk-interface` is
  `com.anotherealitysrl.virtuademy-sdk-core`, the same package under the name it now has.
- The package imports the utilities from `SPACS.Utility`; `Virtuademy.SDK.Core.Utilities` is still
  named where something that stayed there (credentials, application state) is used.

### Breaking
- **The package no longer references the platform framework** (`Virtuademy.SystemCore`). The
  assembly reference is gone, not merely unused: an external application installs this package
  and does not receive the system framework. Code that compiled against `Virtuademy.SystemCore`
  only through this assembly must reference it directly.
- **`ReflectisDataAccessSystem` is not in this package, and `PlatformClient` replaces it.** The
  endpoints are on a plain class deriving from `ApiClientBase`, reached through
  `PlatformClient.Current`, not on a `ScriptableObject` system resolved through `SM`. The static
  `Current` no longer falls back to resolving a system. The application's `VirtuademyDataAccessSystem`
  is still a system, lives in the application and installs a `PlatformClient` at startup. A caller
  switches `SM.GetSystem<ReflectisDataAccessSystem>()` (or the former static) to
  `PlatformClient.Current`.
- **`PlatformClient` has sixteen endpoint methods, down from the 61 of v2.1.0.** The line is what
  `IPlatformContext` actually calls: `GetExternalAppWorlds`; `GetWorld`, `GetExperience`,
  `GetSession`, `CreateSession`; `GetMyUserData`, `GetUserData`; `GetMyWorldPermissions`,
  `GetMySessionPermissions`; `LoadSaveData`, `SetMySaveData`, `DeleteMySaveData`,
  `UpdateMyPreferences` (two overloads); `CreateExperienceAnalytic`, `CreateLeaderboardRecord`.
  Everything else — assets, folders, NPCs, tags, the session calendar, world configuration, user
  search, the platform's diagnostics about itself, and the authoring, activation-key, world-limit
  and session-configuration calls that had moved earlier in the cycle — is on
  `ClientModelApiClient` in the application (`Assets/_Project/ApiSystems/`), except the two
  deleted ones (see Removed). Code outside the Unity project that called any of them has to go
  through the application's client or call the platform API itself.
- **`WebSocketSystem` and `IWebSocketSystem` are not in this package.** They are in the
  application, namespace `Virtuademy.ClientModels`. This package ships the transport only
  (`WebSocketClient` and the handlers).
- **The WebSocket namespace is `Virtuademy.SDK.WebSocket`** (it was `Virtuademy.SDK.Core.WebSocket`
  in `Virtuademy-SDK-Core`): change the `using` and add a reference to
  `Virtuademy.SDK.Library`. Four types are still in the **global namespace** — `WebSocketHandler`,
  `WebGLWebSocketHandler`, `IWebSocketHandler` and `WebSocketMessagesHandler` — and will move in
  a later release, which will break code that names them.
- **A project that names the former package ids stops resolving.** `manifest.json` entries for
  `com.anotherealitysrl.reflectis-sdk-reflectisapi` or
  `com.anotherealitysrl.virtuademy-sdk-platformapi` must become
  `com.anotherealitysrl.virtuademy-sdk-library` with the `Virtuademy-SDK-Library.git` URL; the
  namespaces `Reflectis.SDK.ReflectisApi` and `Virtuademy.SDK.PlatformApi` become
  `Virtuademy.SDK.ApiData`, and the assembly is `Virtuademy.SDK.Library`.
- **An implementation of `IPlatformAuthentication` has to add `RestoreSession`.**

### Removed
- `DownloadAssetContent` and `DownloadAssetThumbnail`: no caller anywhere, so they were deleted
  rather than moved.
- The `SM` fallback of `PlatformClient.Current`, now that every call site has moved.
- `CreateNewAsset`, added to `ReflectisDataAccessSystem` on `develop` for uploading a generic
  media file, **is not in this release**: the endpoint surface it would have joined left for the
  application's `ClientModelApiClient`, and it was applied there.

### Fixed
- The dependency block declared none of the packages this one compiles against. It now names
  `spacs-utility`, `virtuademy-sdk-core`, `com.unity.nuget.newtonsoft-json` and the built-in
  module `com.unity.modules.unitywebrequest` — they resolved anyway while every package is
  embedded, and would not have as soon as one was not. It does **not** name
  `virtuademy-systemcore`: that was declared for a while and removed once the assembly stopped
  referencing it, because UPM refused to resolve the package set of an external app that did not
  install a framework package it should not have to.

## v2.1.0

### Added
- Added `NpcDTO` and the runtime `GetNpcs` / `GetNpcDetails` calls to `ReflectisDataAccessSystem`.
- `NpcDTO` carries `IsWorldDefault` and `OrderForPicker`, so a world's favourite NPC is pinned first in the picker.

## v2.0.0

### Changed
- Refactored the API from systems into a static API layer based on `BaseApi`.
- Added class references for system-related types.

### Fixed
- Fixed session participants handling.

## v1.1.1

### Added

- Added 3d asset generation APIs
- Added sessionDTO status and template

## v1.0.1

### Fixed

- Fix context field on send data in `AnalyticDTO`.

## v1.0.0

- Initial release
