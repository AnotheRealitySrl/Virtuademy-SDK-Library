# Virtuademy-SDK-Library

The platform's client side: the REST client an application calls, the WebSocket transport the
realtime client is built on, the sign-in flows, the platform's wire DTOs, and the contracts an
application implements or consumes. **Nothing a creator installs names anything here** — an authored
world reaches the platform through `Virtuademy.ScriptingApi`, which this package does not have to be.

## What is here today

One assembly, `Virtuademy.SDK.Library`, over four namespaces:

| Namespace | Holds |
|---|---|
| `Virtuademy.SDK.ApiData` | the REST client, the sign-in flows, the wire DTOs, the one remaining projection |
| `Virtuademy.SDK.Interface` | the platform contracts and their five value types |
| `Virtuademy.SDK.WebSocket` | the socket registry and its handlers |
| `Virtuademy.SDK.Core.ApplicationManagement` | `IApplicationManager` and `EApplicationState` |

It compiles against `SPACS-Utility` and `Virtuademy-SDK-Core` (the transport, the tenant
configuration client, `Virtuademy.ScriptingApi`), and against nothing else first-party — the
application framework in particular.

### `PlatformClient` — sixteen endpoints

A plain class on `ApiClientBase`, reached through `PlatformClient.Current`. Sixteen endpoints: the
app's own catalogue, the session it joins, who the user is, what they may do, their own saved data,
and what they report. **The line was drawn from what `IPlatformContext` actually needs** rather than
from what a client happened to have — everything else went to the application's own client on
2026-09-15.

Three things about its shape are deliberate:

- **It is a plain class.** It was a `ScriptableObject` system, which put the platform's system
  framework in the dependency path of every application that wanted to talk to the API.
  `VirtuademyDataAccessSystem` is still that system, in the application where it belongs, and it
  installs one of these at startup.
- **`Current` creates itself** rather than waiting to be installed. The application's systems are
  registered first and initialised afterwards, in an order nothing here controls, and more than one
  of them takes this reference in its own `Init`; a client that only appeared once its own system
  had run would be missing for whoever ran first. It has no address until the system that owns the
  connection configures it — see `IsConfigured`.
- **`DiscoveryApiType` is `"Application"`**, so its base URL is resolved from the platform record
  rather than from the value serialized into the build, falling back to that value when discovery
  has not answered (meta-repo ADR 0024).

`Install` replaces the client, for an external application with no platform system to configure it,
or for a test.

### The platform contracts — `Virtuademy.SDK.Interface`

`IPlatformContext` is platform and session state as an app sees it: who the user is, which world and
session they are in, what they are allowed to do, who else is present, and their own saved data.
`IPlatformAuthentication` is the sibling rather than a member, because initialization presupposes an
authenticated user: `BeginLogin`, the two completions, `RestoreSession`, `Logout`, `IsAuthenticated`
and `AuthenticationChanged`. `WorldChooser` is the delegate an app supplies to pick between
publications, consulted only when there is more than one.

**The contracts hand back the platform's own wire types** — `UserDTO`, `SessionDTO`, `WorldDTO`,
`ExperienceDTO`, `OnlineUserDTO`, `ExternalAppPlacementDTO` — and do not mirror them. An earlier
draft declared a parallel set of value types with a projection mapping each DTO onto its twin; the
argument for it was a perimeter that only held while the DTOs and the client shared an assembly.
They no longer do. The mirror's cost was paid before it came out — four members dropped for having
no wire source, two types renamed for colliding with the family they duplicated, every new DTO field
added twice — and what it bought that was worth keeping is immutability, so the five DTOs the
contracts return are **read-only**.

Five types the contracts still declare themselves, each because there is nothing to name:

- **`PlatformPermission`** — the wire form is a bare **string**, with no DTO at all, and a caller
  cannot be handed raw text and expected to compare it correctly. **Its member names are the wire
  contract**: they are parsed case-sensitively, so renaming one silently stops granting that
  permission. That is not hypothetical — the leaderboard member was singular where the platform's
  identifier is plural, and that permission had never resolved in any client until it was measured.
- **`SessionShard`** — its wire form, `ShardDTO`, lives in the realtime package, which references
  this one; naming it here would invert the dependency. It carries no per-shard maximum: the wire
  carries occupancy and the closed flag, and the ceiling is a single global for the deployment.
- **`PlatformContextState`**, **`PlatformLaunchData`**, **`LoginChallenge`** — concepts of this
  contract with no wire counterpart.

`PlatformContextProjection` is what is left of the projection: the permission endpoints answer with
identifier strings, so those strings become `PlatformPermission` members. That is a real conversion
rather than a re-wrap, which is why it survived.

The implementation, `PlatformContext`, is **not** here — it needs both the REST and the realtime
client, so it lives in `Virtuademy-SDK-RealtimeApi`, which is the only package that sees both.

#### Vocabulary

Three unrelated things were called "session" in the interface these contracts replace, in the same
file. They are named apart on the members, which is where the naming lives:

| Concept | Type | Here |
|---|---|---|
| The session | `int` | `Session.Id` |
| The realtime connection | `string` | `IPlatformContext.ConnectionId` |
| The authentication session | `string` | `PlatformLaunchData.AuthSessionHash` |

### Sign-in, for an application the platform did not start

`PlatformAuthentication` implements `IPlatformAuthentication` for the standalone case, where there
is no launch data and no session to restore. The **code flow** is the one a headset has always used
and the only one that works without a browser the app can read: the app shows an address and eight
characters, the user types them on any other device, and the page answers with four characters they
type back. The **token completion** is for a host that can read the redirect — a browser, a web
view, a mobile shell: it presents the identity provider's access token to `my/sessions/bind`, the
one endpoint of the login that is not HMAC, and the platform answers with the same check code. From
there the two completions are one. The session is **not persisted**; whether to keep one across runs
is a policy decision an app makes for itself.

Around it:

- **`IdpLogin`** — authorization code with PKCE, written out rather than taken from a library. MSAL
  is what the editor uses and it is a desktop .NET library, which an app shipping to a headset or a
  browser cannot take; the protocol is four portable things. No client secret, by construction.
  Authority, policy, audience and client id all come from the tenant — nothing about an identity
  provider is compiled into an application.
- **`ProfileClient`** — the profile API reduced to the six endpoints signing a user in needs, every
  one of them HMAC, which is what makes the flow possible with no user token yet. It is also the
  `ITokenProvider`, because the thing that holds the session is the thing that can refresh a token.
- **`LoopbackRedirect`** and **`RedirectReceiver`** — receiving the redirect, which only the host
  knows how to do: a loopback port on desktop, a deep link on mobile, its own location on web.
- **`PlatformLaunch`** reads what the platform put in the address it launched the app with;
  **`PlatformSession`** is the login session as the profile API reports it.

### WebSocket transport

`WebSocketClient` keeps one connection per address, opened on demand and held until somebody
disconnects it. The handlers under it — `WebSocketHandler` and `WebGLWebSocketHandler`, with its
`webSocketClient.jslib` plugin — were always in a package an external app developer installs; the
**registry** around them was not. The map from address to handler, the waiting on a connection that
is still opening and the scheme normalisation used to sit on a framework `BaseSystem`, so anything
that wanted a socket had to resolve a system to get one. `WebSocketSystem` is now a wrapper over one
of these, the same shape `ApiSystemBase` already has over `ApiClientBase`.

Instances do not share connections: two clients asking for the same address get two sockets. Nothing
currently meets that, since every caller uses an address of its own, but it is why the registry is
per instance rather than static.

The realtime **protocol** client, `RealtimeApiClient`, is not here — it is in
`Virtuademy-SDK-RealtimeApi`, along with its own DTOs.

### The wire DTOs

`Runtime/DTOs/` holds the platform's wire format: the entities (`UserDTO`, `SessionDTO`, `WorldDTO`,
`ExperienceDTO`, `EnvironmentDTO`, `AssetDTO`, `CatalogDTO`, `FolderDTO`, `NpcDTO`, …), the eight
search-criteria shapes, the `Info` projections and the enums. They moved in on 2026-09-14, because
this is where their implementation and their audience already were, and every consumer of the
assembly they came from sat at or below this one.

**The seventeen analytics types did not come with them.** A creator authoring an xAPI statement in a
Visual Scripting graph needs those, so they live in `Virtuademy.ScriptingApi` — which a creator
installs and this package does not have to be.

### `IApplicationManager`

The application-lifecycle pair: `IsEmbedded` (true when Unity runs inside a host application that
owns shared transport, most notably the single realtime WebSocket — time-invariant for a run, so it
is safe to cache), `QuitApplication`, `ErasePlayerSessionData`, `CheckInternetConnection` and
`GetCurrentDevice`.

## Known issues / TODO

- **The version has not been cut.** `package.json` is still 2.1.0 while the CHANGELOG's
  `Unreleased` section already carries the rename, the DTO move and the contracts move.
- **A CHANGELOG entry claims a dependency that is not declared.** It says `spacs-utility` and
  `virtuademy-systemcore` were both added to the block; only `spacs-utility` is there. Nothing is
  actually missing — the assembly does not reference the framework either — so the entry is stale
  rather than the manifest being wrong.
- **Two namespaces do not name this package.** The contracts sit in `Virtuademy.SDK.Interface` and
  the lifecycle pair in `Virtuademy.SDK.Core.ApplicationManagement`, both inherited from where they
  used to live. Renaming either touches every `using` across the project, which is why the move kept
  them: worth a dedicated pass, not an incidental one.
