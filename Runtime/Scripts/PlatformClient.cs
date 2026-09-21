using Newtonsoft.Json;

using SPACS.Utilities;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UnityEngine;
using UnityEngine.Networking;

using Virtuademy.ScriptingApi;
using Virtuademy.SDK.Core.ApiSystem;
using Virtuademy.SDK.Http;

namespace Virtuademy.SDK.ApiData
{
    /// <summary>
    /// The platform REST client, and the whole of what an external application may reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sixteen endpoints: the app's own catalogue, the session it joins, who the user is, what
    /// they may do, their own saved data, and what they report. The line was drawn from what
    /// <c>IPlatformContext</c> actually needs rather than from what a client happened to have —
    /// everything else went to the application's own client on 2026-09-15.
    /// </para>
    /// <para>
    /// A plain class, and that is the point. It was a <c>ScriptableObject</c> system, which put
    /// the platform's system framework in the dependency path of every application that wanted to
    /// talk to the API. <c>VirtuademyDataAccessSystem</c> is still that system, in the application
    /// where it belongs, and it installs one of these at startup.
    /// </para>
    /// </remarks>
    public class PlatformClient : ApiClientBase
    {
        #region Reaching this client

        private static PlatformClient installed;

        /// <summary>
        /// Whether anybody has given this client an address to talk to. A client exists from the
        /// first access; one that has not been configured cannot reach anything.
        /// </summary>
        public static bool IsConfigured => !string.IsNullOrEmpty(Current.ApiLabel);

        /// <summary>
        /// The platform client. Every caller reaches it through here.
        /// </summary>
        /// <remarks>
        /// It creates itself rather than waiting to be installed, and that is not laziness: the
        /// application's systems are registered first and initialised afterwards, in an order
        /// nothing here controls, and more than one of them takes this reference in its own
        /// <c>Init</c>. A client that only appeared once its own system had run would be missing
        /// for whoever ran first — which is exactly the failure this replaced.
        /// <para>
        /// The instance is configured later, by the system that owns the connection. Until then it
        /// has no address: see <see cref="IsConfigured"/>.
        /// </para>
        /// </remarks>
        public static PlatformClient Current => installed ??= new PlatformClient();

        /// <summary>
        /// Replaces the client. For an application that builds its own — an external one with no
        /// platform system to configure it, or a test.
        /// </summary>
        public static void Install(PlatformClient client)
        {
            installed = client ?? throw new ArgumentNullException(nameof(client));
        }

        #endregion

        /// <summary>
        /// Opts this client into endpoint discovery: its base URL is resolved from the platform
        /// record for the platform REST API rather than from the value serialized into the build,
        /// falling back to that value when discovery has not answered. See ADR 0024.
        /// </summary>
        protected override string DiscoveryApiType => "Application";

        /// <summary>Runtime-only, never serialized.</summary>
        public int CacheId { get; set; } = -1;

        public string ApiVersion => apiConfig.ApiVersion;

        /// <summary>The client name the analytics endpoints report.</summary>
        private const string app = "Unity";

        #region Experience
        public async Task<ApiResponse<ExperienceDTO>> GetExperience(int worldId, int experienceId)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"worlds/{worldId}/experiences/{experienceId}");
            await request.SendWebRequest();
            return new ApiResponse<ExperienceDTO>(request.responseCode, request.error, request.downloadHandler.text);
        }

        #endregion

        #region Sessions
        public async Task<ApiResponse<SessionDTO>> CreateSession(int worldId, int experienceId, NewSessionDTO newEvent)
        {
            Debug.Log("Creating event " + JsonConvert.SerializeObject(newEvent));
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbPOST, $"worlds/{worldId}/experiences/{experienceId}/newsession", body: JsonConvert.SerializeObject(newEvent));
            await request.SendWebRequest();

            return new ApiResponse<SessionDTO>(request.responseCode, request.error, request.downloadHandler.text);
        }

        public async Task<ApiResponse<SessionDTO>> GetSession(int worldId, int sessionId)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"worlds/{worldId}/sessions/{sessionId}");
            await request.SendWebRequest();

            return new ApiResponse<SessionDTO>(request.responseCode, request.error, request.downloadHandler.text);
        }

        #endregion

        #region Permissions

        public async Task<ApiResponse<List<string>>> GetMySessionPermissions(int eventId)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"sessions/{eventId}/app/{app}/permissions/my");
            await request.SendWebRequest();

            return new ApiResponse<List<string>>(request.responseCode, request.error, request.downloadHandler.text);
        }

        public async Task<ApiResponse<List<string>>> GetMyWorldPermissions(int worldId)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"worlds/{worldId}/app/{app}/permissions/my");
            await request.SendWebRequest();

            return new ApiResponse<List<string>>(request.responseCode, request.error, request.downloadHandler.text);
        }

        #endregion

        #region Worlds

        public async Task<ApiResponse<WorldDTO>> GetWorld(int worldId)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"worlds/{worldId}");
            await request.SendWebRequest();

            return new ApiResponse<WorldDTO>(request.responseCode, request.error, request.downloadHandler.text);
        }

        /// <summary>
        /// Every world the calling app is published in, one entry per published <c>ExternalApp</c>
        /// experience. How a standalone external app finds out where it may run.
        /// </summary>
        /// <remarks>
        /// <b>No app parameter, by contract.</b> The app is identified by its token's <c>azp</c>
        /// claim, matched server-side against the <c>appObjectId</c> on each experience's config.
        /// So an app cannot ask about another app, and there is no app identity duplicated between
        /// the caller and the record.
        /// <para>
        /// Three answers matter and they are different states, not degrees of the same one:
        /// <b>200</b> with entries, <b>204</b> meaning the app is registered but published in no
        /// world this user can enter — terminal and explainable, not something to retry — and
        /// <b>403</b> meaning the token carries no <c>azp</c> at all, i.e. there is no app asking.
        /// </para>
        /// Contract: <c>contracts/openapi/external-app-worlds.yaml</c> in the meta-repo.
        /// </remarks>
        public async Task<ApiResponseArray<ExternalAppPlacementDTO>> GetExternalAppWorlds()
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, "external-app/worlds");
            await request.SendWebRequest();

            return new ApiResponseArray<ExternalAppPlacementDTO>(request.responseCode, request.error, request.downloadHandler.text);
        }

        #endregion

        #region Users

        public async Task<ApiResponse<UserDTO>> GetUserData(int worldId, int userId)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"worlds/{worldId}/users/{userId}");
            await request.SendWebRequest();

            return new ApiResponse<UserDTO>(request.responseCode, request.error, request.downloadHandler.text);
        }

        public async Task<ApiResponse> UpdateMyPreferences(object newPreferences)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbPOST, $"users/my/preferences", body: JsonConvert.SerializeObject(newPreferences));
            await request.SendWebRequest();

            return new ApiResponse(request.responseCode, request.error, request.downloadHandler.text);
        }

        public async Task<ApiResponse> UpdateMyPreferences(int worldId, object newPreferences)
        {
            //TO DO: REPLACE WITH WORLD API ONCE READY
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbPOST, $"users/my/preferences", body: JsonConvert.SerializeObject(newPreferences));
            await request.SendWebRequest();

            return new ApiResponse(request.responseCode, request.error, request.downloadHandler.text);
        }

        public async Task<ApiResponse<UserDTO>> GetMyUserData()
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"users/my/profile");
            await request.SendWebRequest();

            return new ApiResponse<UserDTO>(request.responseCode, request.error, request.downloadHandler.text);
        }

        #endregion

        #region Experience Analytics
        public async Task<ApiResponse> CreateExperienceAnalytic(AnalyticDTO experienceAnalyticDTO)
        {
            if (experienceAnalyticDTO.Statement == null)
            {
                //make sure the object is null so that it won't be sent to the server
                experienceAnalyticDTO.Locale = null;
                using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbPOST, $"online/experience/progress",
                    body: JsonConvert.SerializeObject(experienceAnalyticDTO));
                await request.SendWebRequest();
                return new ApiResponse(request.responseCode, request.error, request.downloadHandler.text);
            }
            else
            {
                using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbPOST, $"xapi/lrs",
                    body: JsonConvert.SerializeObject(experienceAnalyticDTO));
                await request.SendWebRequest();
                return new ApiResponse(request.responseCode, request.error, request.downloadHandler.text);
            }
        }
        #endregion

        #region Save Data
        public async Task<ApiResponse<CustomType>> LoadSaveData(int worldId)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbGET, $"worlds/{worldId}/users/my/data/all");
            await request.SendWebRequest();
            return new ApiResponse<CustomType>(request.responseCode, request.error, request.downloadHandler.text);
        }

        public async Task<ApiResponse<CustomType>> SetMySaveData(int id, string key, object data)
        {
            CustomType customType = new CustomType();
            customType.Fields[key] = data;

            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbPOST, $"worlds/{id}/users/my/data",
                body: JsonConvert.SerializeObject(customType));

            await request.SendWebRequest();
            return new ApiResponse<CustomType>(request.responseCode, request.error, request.downloadHandler.text);
        }

        public async Task<ApiResponse<CustomType>> DeleteMySaveData(int id, List<string> keys)
        {
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbDELETE, $"worlds/{id}/users/my/data", body: JsonConvert.SerializeObject(keys));
            await request.SendWebRequest();
            return new ApiResponse<CustomType>(request.responseCode, request.error, request.downloadHandler.text);
        }
        #endregion

        #region Leaderboard
        public async Task<ApiResponse> CreateLeaderboardRecord(int worldId, string leaderboardKey, float data)
        {
            LeaderboardRecordDTO record = new LeaderboardRecordDTO(leaderboardKey, data);
            using UnityWebRequest request = await BuildRequest(UnityWebRequest.kHttpVerbPOST, $"worlds/{worldId}/leaderboards/records", body: JsonConvert.SerializeObject(record));
            await request.SendWebRequest();
            return new ApiResponse(request.responseCode, request.error, request.downloadHandler.text);
        }
        #endregion

        #region Overrides

        protected override Dictionary<string, string> SetDefaultHeaders(params string[] values)
        {
            Dictionary<string, string> headers = base.SetDefaultHeaders(values);

            if (CacheId > 0)
            {
                headers.Add("Cache-Id", CacheId.ToString());
            }

            return headers;
        }

        #endregion

    }
}
