using SPACS.Utilities;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UnityEngine.Networking;

using Virtuademy.SDK.Core.ApiSystem;
using Virtuademy.SDK.Core.Authentication;
using Virtuademy.SDK.Core.Utilities;
using Virtuademy.SDK.Http;

namespace Virtuademy.SDK.ApiData
{
    /// <summary>
    /// The profile API, reduced to what signing a user in needs — and to what an external
    /// application is entitled to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Six endpoints, every one of them HMAC. That is the whole reason this is possible without the
    /// platform: the login flow authenticates with the application's own credential, not with a
    /// user token it does not have yet, and HMAC is the one thing the package plan says cannot be
    /// withheld from an app developer.
    /// </para>
    /// <para>
    /// It is also an <see cref="ITokenProvider"/>, which is not an extra role but the same one: the
    /// session this obtains is what the tokens are minted against, so the thing that holds the
    /// session is the thing that can refresh them.
    /// </para>
    /// <para>
    /// The platform's own <c>AuthenticationSystem</c> reaches the same endpoints and keeps its own
    /// state machine, events and persistence. This is not a replacement for it and does not try to
    /// be: it is the subset an external host needs, with no framework under it.
    /// </para>
    /// </remarks>
    public class ProfileClient : ApiClientBase, ITokenProvider, ITokenProviderNotifications
    {
        private readonly Dictionary<string, JwtToken> tokens = new();

        private Task tokensInFlight;

        /// <inheritdoc />
        public event Action TokensChanged;

        /// <summary>The session in hand, or null before one is begun.</summary>
        public PlatformSession Session { get; set; }

        /// <summary>
        /// Opens a pending session. The response carries the identifier the user types on the web.
        /// </summary>
        public async Task<ApiResponse<PlatformSession>> BeginSession()
        {
            using UnityWebRequest request = await BuildRequest(
                UnityWebRequest.kHttpVerbPOST, "my/sessions", authentication: EAuthentication.Hmac);
            await request.SendWebRequest();

            return new ApiResponse<PlatformSession>(request.responseCode, request.error,
                                                    request.downloadHandler.text);
        }

        /// <summary>
        /// Turns the pending session into a live one, with the code the web flow showed the user.
        /// </summary>
        public async Task<ApiResponse<PlatformSession>> EnableSession(string checkCode)
        {
            if (string.IsNullOrEmpty(Session?.Identifier))
            {
                return new ApiResponse<PlatformSession>(
                    400, $"No pending session — call {nameof(BeginSession)} first.", string.Empty);
            }

            Dictionary<string, string> queryParams = new()
            {
                { "identifier", Session.Identifier },
                { "checkcode", checkCode },
            };

            using UnityWebRequest request = await BuildRequest(
                UnityWebRequest.kHttpVerbPOST, "my/sessions/enable", queryParams,
                authentication: EAuthentication.Hmac);
            await request.SendWebRequest();

            return new ApiResponse<PlatformSession>(request.responseCode, request.error,
                                                    request.downloadHandler.text);
        }

        /// <summary>
        /// Binds the pending session to the user an identity-provider access token names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The other way to finish a login, and the only endpoint of the six that is not HMAC: the
        /// point of it is precisely that the caller presents a user token rather than the
        /// application's credential. A host that can observe the sign-in redirect — a browser, a
        /// web view, a mobile shell — has such a token; a headset does not, which is why the code
        /// flow exists beside this one.
        /// </para>
        /// <para>
        /// It answers with the same check code the web flow would have shown the user, so the two
        /// completions converge: what the user types in one, the host is handed in the other, and
        /// the session is enabled the same way from there.
        /// </para>
        /// </remarks>
        public async Task<ApiResponse<PlatformSession>> BindSession(string accessToken)
        {
            if (string.IsNullOrEmpty(Session?.Identifier))
            {
                return new ApiResponse<PlatformSession>(
                    400, $"No pending session — call {nameof(BeginSession)} first.", string.Empty);
            }

            Dictionary<string, string> queryParams = new()
            {
                { "identifier", Session.Identifier },
            };

            // The token goes on by hand, with authentication off. EAuthentication.Bearer means
            // "the platform token this client holds for this API", which is what a login produces
            // and therefore cannot be what completes one — the token here is the identity
            // provider's, and the client has nothing of its own yet.
            Dictionary<string, string> headers = new()
            {
                { "Authorization", $"Bearer {accessToken}" },
            };

            using UnityWebRequest request = await BuildRequest(
                UnityWebRequest.kHttpVerbPOST, "my/sessions/bind", queryParams,
                authentication: EAuthentication.None, additionalHeaders: headers);
            await request.SendWebRequest();

            return new ApiResponse<PlatformSession>(request.responseCode, request.error,
                                                    request.downloadHandler.text);
        }

        /// <summary>
        /// Reads back the session a hash names. This is what turns a persisted or handed-over hash
        /// into a session, and what says whether it is still alive.
        /// </summary>
        public async Task<ApiResponse<PlatformSession>> GetSessionByHash(string sessionHash)
        {
            using UnityWebRequest request = await BuildRequest(
                UnityWebRequest.kHttpVerbGET, "my/sessions/hash",
                additionalHeaders: Header(sessionHash), authentication: EAuthentication.Hmac);
            await request.SendWebRequest();

            return new ApiResponse<PlatformSession>(request.responseCode, request.error,
                                                    request.downloadHandler.text);
        }

        /// <summary>Ends the session, on the platform's side as well as this one's.</summary>
        public async Task<ApiResponse> KillSession(int sessionId)
        {
            using UnityWebRequest request = await BuildRequest(
                UnityWebRequest.kHttpVerbPOST, $"my/sessions/{sessionId}/kill",
                additionalHeaders: Header(Session?.Hash), authentication: EAuthentication.Hmac);
            await request.SendWebRequest();

            return new ApiResponse(request.responseCode, request.error, request.downloadHandler.text);
        }

        /// <summary>Tells the platform the session is still in use.</summary>
        public async Task<ApiResponse<PlatformSession>> KeepAlive(string sessionHash)
        {
            using UnityWebRequest request = await BuildRequest(
                UnityWebRequest.kHttpVerbPOST, "my/sessions/keepalive",
                additionalHeaders: Header(sessionHash), authentication: EAuthentication.Hmac);
            await request.SendWebRequest();

            return new ApiResponse<PlatformSession>(request.responseCode, request.error,
                                                    request.downloadHandler.text);
        }

        // ------------------------------------------------------------------- ITokenProvider

        /// <inheritdoc />
        public JwtToken FindToken(string apiLabel)
            => tokens.TryGetValue(apiLabel, out JwtToken token)
               ? token
               : throw new KeyNotFoundException(
                   $"No token held for '{apiLabel}'. Tokens are minted per API, against the "
                   + "session, so a label that was never in the set means the session does not "
                   + "reach that API.");

        /// <inheritdoc />
        /// <remarks>
        /// Callers that ask while a fetch is running share it: every client that finds its token
        /// expired at the same moment asks, and they used to fetch the whole set once each.
        /// </remarks>
        public Task GetTokens()
        {
            if (tokensInFlight == null || tokensInFlight.IsCompleted)
            {
                tokensInFlight = FetchTokens();
            }

            return tokensInFlight;
        }

        /// <summary>
        /// Forgets the token set and tells the clients that took tokens from it, so none goes on
        /// signing with a token of a session that has ended.
        /// </summary>
        public void ClearTokens()
        {
            tokens.Clear();
            TokensChanged?.Invoke();
        }

        private async Task FetchTokens()
        {
            string sessionHash = Session?.Hash;
            if (string.IsNullOrEmpty(sessionHash))
            {
                return;
            }

            using UnityWebRequest request = await BuildRequest(
                UnityWebRequest.kHttpVerbGET, "my/tokens",
                additionalHeaders: Header(sessionHash), authentication: EAuthentication.Hmac);
            await request.SendWebRequest();

            // A sign-out (or another sign-in) while the request was out: these tokens belong to a
            // session that is no longer the one in hand.
            if (Session?.Hash != sessionHash)
            {
                return;
            }

            ApiResponseArray<JwtToken> response = new(request.responseCode, request.error,
                                                      request.downloadHandler.text);
            if (!response.IsSuccess || response.Content == null)
            {
                return;
            }

            tokens.Clear();
            foreach (JwtToken token in response.Content)
            {
                tokens[token.ApiLabel] = token;
            }

            TokensChanged?.Invoke();
        }

        private static Dictionary<string, string> Header(string sessionHash)
            => new() { { "SessionHash", sessionHash } };
    }
}
