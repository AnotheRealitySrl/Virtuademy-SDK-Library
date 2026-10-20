using System;
using System.Threading.Tasks;

using Virtuademy.SDK.Core.ApiSystem;
using Virtuademy.SDK.Core.Authentication;
using Virtuademy.SDK.Http;
using Virtuademy.SDK.Interface;
using Virtuademy.SDK.TenantConfiguration;

namespace Virtuademy.SDK.ApiData
{
    /// <summary>
    /// Signs a user in from an application the platform did not start — the standalone case, where
    /// there is no launch data and no session to restore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code flow, which is the one a headset has always used and the only one that works
    /// without a browser the app can read: the app shows an address and eight characters, the user
    /// opens that address on any other device and types them, and the page answers with four
    /// characters the user types back. Nothing in that round trip requires the app to observe a
    /// redirect, which is why it is the flow that works everywhere.
    /// </para>
    /// <para>
    /// <b>The token completion is for a host that can read the redirect</b> — a browser, a web
    /// view, a mobile shell. It presents the identity provider's access token to
    /// <c>my/sessions/bind</c>, the one endpoint of the login that is not HMAC, and the platform
    /// answers with the same check code the web flow would have shown the user. From there the two
    /// completions are one: the session is enabled with that code. What this implementation does
    /// not do is <i>obtain</i> the token — that is the host's job, because only the host has a
    /// browser.
    /// </para>
    /// <para>
    /// The session is not persisted here. Whether an application should keep a platform session
    /// across runs is a policy decision, not a default to inherit, and an app that wants it can
    /// hold the hash itself and hand it to <see cref="RestoreSession"/>.
    /// </para>
    /// <para>
    /// <b>An application is configured from its tenant, exactly as the platform's own is.</b> A
    /// developer receives the app's <c>AppConfig</c> — a credential and the address of the
    /// configuration API — and <see cref="ForTenant"/> fetches the rest: which profile API to talk
    /// to, at which version, and which web address the user opens. Nothing about a tenant is
    /// compiled into an application, which is what lets the same build serve more than one.
    /// </para>
    /// </remarks>
    public sealed class PlatformAuthentication : IPlatformAuthentication
    {
        private readonly ProfileClient profile;
        private readonly string applicationUrl;

        /// <param name="profile">A configured profile client — its connection settles the tenant.</param>
        /// <param name="applicationUrl">
        /// The tenant's web address, which the user opens to complete the login. It comes from the
        /// tenant configuration rather than from this assembly, because an implementation that
        /// composed it would be deciding which tenant the application belongs to.
        /// <see cref="ForTenant"/> is the ordinary way in; this constructor is for a host that has
        /// already resolved both by other means.
        /// </param>
        public PlatformAuthentication(ProfileClient profile, string applicationUrl)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            this.applicationUrl = applicationUrl
                                  ?? throw new ArgumentNullException(nameof(applicationUrl));
        }

        /// <summary>
        /// Builds a login for the tenant a configuration client has already read.
        /// </summary>
        /// <remarks>
        /// The client must have been initialised — this reads the tenant it fetched. The profile
        /// client it builds carries the same credential the configuration one does, because they
        /// are the same application talking to two APIs of the same tenant.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The configuration client has no tenant yet, so there is nothing to configure from.
        /// </exception>
        public static async Task<PlatformAuthentication> ForTenant(TenantConfigurationClient tenant)
        {
            if (tenant == null)
            {
                throw new ArgumentNullException(nameof(tenant));
            }

            TenantConfig config = tenant.TenantConfiguration?.Config
                ?? throw new InvalidOperationException(
                    "The configuration client has not read a tenant. Call Init on it first: an "
                    + "application learns which profile API to use from the tenant, not from its "
                    + "own build.");

            ProfileClient profile = new();
            await profile.Init(new AppIdentification(tenant.AppIdentification.Credential,
                                                     config.ProfileApiUrl,
                                                     config.ProfileApiVersion));

            return new PlatformAuthentication(profile, config.ApplicationUrl);
        }

        /// <summary>
        /// The token provider this login produced, for the other clients an application talks to.
        /// </summary>
        /// <remarks>
        /// The profile client itself: the session is what the tokens are minted against, so the
        /// thing holding the session is the thing that can refresh them. Hand it to a
        /// <see cref=PlatformClient/> or a realtime one and their requests carry a bearer.
        /// </remarks>
        public ITokenProvider Tokens => profile;

        /// <inheritdoc />
        public bool IsAuthenticated { get; private set; }

        /// <inheritdoc />
        public event Action AuthenticationChanged;

        /// <inheritdoc />
        public async Task<LoginChallenge> BeginLogin()
        {
            ApiResponse<PlatformSession> begun = await profile.BeginSession();

            if (!begun.IsSuccess || begun.Content == null)
            {
                throw new InvalidOperationException(
                    $"Could not begin a login: {begun.StatusCode} {begun.ReasonPhrase}.");
            }

            profile.Session = begun.Content;

            return new LoginChallenge(begun.Content.Identifier, applicationUrl);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Two calls, because binding answers with the check code rather than with the session:
        /// the token says who the user is, and enabling with the code it hands back is the same
        /// step the other completion takes. The host never sees the code.
        /// </remarks>
        public async Task CompleteLoginWithAccessToken(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new ArgumentException("An access token is required.", nameof(accessToken));
            }

            ApiResponse<PlatformSession> bound = await profile.BindSession(accessToken);

            if (!bound.IsSuccess || bound.Content == null)
            {
                throw new InvalidOperationException(
                    $"The access token was refused: {bound.StatusCode} {bound.ReasonPhrase}.");
            }

            if (string.IsNullOrEmpty(bound.Content.CheckCode))
            {
                throw new InvalidOperationException(
                    "The session was bound but came back without a check code, so it cannot be "
                    + "enabled. Nothing is signed in.");
            }

            await CompleteLoginWithCode(bound.Content.CheckCode);
        }

        /// <inheritdoc />
        public async Task CompleteLoginWithCode(string confirmationCode)
        {
            ApiResponse<PlatformSession> enabled = await profile.EnableSession(confirmationCode);

            if (!enabled.IsSuccess || enabled.Content == null)
            {
                throw new InvalidOperationException(
                    $"The confirmation code was refused: {enabled.StatusCode} {enabled.ReasonPhrase}.");
            }

            await Establish(enabled.Content.Hash);
        }

        /// <inheritdoc />
        public Task RestoreSession(string sessionHash) => Establish(sessionHash);

        /// <inheritdoc />
        public async Task Logout()
        {
            if (profile.Session != null)
            {
                await profile.KillSession(profile.Session.Id);
            }

            profile.Session = null;
            // The clients cache the token they took; clearing the set tells them to drop it, so
            // the next user to sign in does not inherit this one's tokens.
            profile.ClearTokens();
            SetAuthenticated(false);
        }

        /// <summary>
        /// Reads the session back and mints the tokens against it. Both halves matter: the read
        /// says the session is live, and without the tokens the first call to any other API
        /// arrives with nothing to present.
        /// </summary>
        private async Task Establish(string sessionHash)
        {
            ApiResponse<PlatformSession> session = await profile.GetSessionByHash(sessionHash);

            if (!session.IsSuccess || session.Content == null)
            {
                profile.Session = null;
                SetAuthenticated(false);

                throw new InvalidOperationException(
                    $"The session is not usable: {session.StatusCode} {session.ReasonPhrase}.");
            }

            // The read answers with everything but the hash, which is what identified it in the
            // first place — so it is put back rather than lost.
            session.Content.Hash = sessionHash;
            profile.Session = session.Content;

            await profile.GetTokens();

            SetAuthenticated(true);
        }

        private void SetAuthenticated(bool value)
        {
            if (IsAuthenticated == value)
            {
                return;
            }

            IsAuthenticated = value;
            AuthenticationChanged?.Invoke();
        }
    }
}
