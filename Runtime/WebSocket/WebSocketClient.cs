using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UnityEngine;

namespace Virtuademy.SDK.WebSocket
{
    /// <summary>
    /// One connection per address, opened on demand and kept until somebody disconnects it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handlers under this — <see cref="WebSocketHandler"/> and its WebGL counterpart — were
    /// always here, in the package a creator and an external app developer both install. What was
    /// not was the registry around them: the map from address to handler, the waiting on a
    /// connection that is still opening, and the scheme normalisation. Those lived on a
    /// <c>BaseSystem</c> in the framework package, so anything that wanted a socket had to resolve
    /// a system to get one — and that put the whole platform framework in the dependency path of a
    /// WebSocket.
    /// </para>
    /// <para>
    /// This is that registry as a plain class. <c>WebSocketSystem</c> is now a wrapper over one of
    /// these, the same shape <c>ApiSystemBase</c> already has over <see cref="ApiClientBase"/>, so
    /// the framework keeps offering a system to whatever resolves one while a caller that has no
    /// framework can just new this up.
    /// </para>
    /// <para>
    /// Instances do not share connections: two clients asking for the same address get two sockets.
    /// That is not a limitation anything currently meets — every caller uses an address of its own —
    /// but it is the reason this is a registry per instance rather than a static one.
    /// </para>
    /// </remarks>
    public class WebSocketClient
    {
        private readonly Dictionary<string, IWebSocketHandler> webSocketHandlers = new();

        /// <summary>
        /// The scheme to assume for an address that carries none. True gives <c>wss</c>.
        /// </summary>
        public bool SecureConnection { get; set; } = true;

        /// <summary>Names this client in log messages. Nothing reads it.</summary>
        public string Label { get; set; } = nameof(WebSocketClient);

        /// <summary>
        /// Installs what the platform needs before the first connection. Idempotent per process,
        /// and a no-op outside WebGL.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The WebGL socket cannot receive on its own: the browser delivers to a callback, and the
        /// bridge needs a live GameObject to deliver it to. Everywhere else the handler owns its
        /// own loop and there is nothing to install.
        /// </para>
        /// <para>
        /// The bridge is one per process, not one per client. The plugin calls back by GameObject
        /// name, and <c>Connect</c> registers its channel on <see cref="WebSocketMessagesHandler.Instance"/>:
        /// with two bridges (the application's <c>WebSocketSystem</c> and the realtime client each
        /// own a client) the open callback could reach the copy that never saw the channel, throw
        /// <c>KeyNotFoundException</c>, and leave <c>Connect</c> waiting forever — every AI chatbot
        /// on WebGL hung that way while the server had accepted the socket.
        /// </para>
        /// </remarks>
        public void Initialize()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (WebSocketMessagesHandler.Instance != null)
            {
                return;
            }

            GameObject webGLWebSocketHandler = new GameObject(nameof(WebSocketMessagesHandler));
            webGLWebSocketHandler.AddComponent<WebSocketMessagesHandler>();
            UnityEngine.Object.DontDestroyOnLoad(webGLWebSocketHandler);
#endif
        }

        /// <summary>
        /// Connects to <paramref name="url"/>, or reports that it could not. Add any listener
        /// before connecting, or the first message is lost.
        /// </summary>
        public async Task<bool> ConnectAsync(string url, Dictionary<string, string> queryParams,
                                             Action<string> onWebSocketOpenError = null)
        {
            IWebSocketHandler webSocketHandler = GetWebSocketHandler(url);

            // Somebody else is already opening this one; wait for them rather than opening a second.
            while (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Connecting)
            {
                await Task.Yield();
            }

            if (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Open)
            {
                return true;
            }

            if (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Closing)
            {
                await Task.Yield();
            }

            try
            {
                await webSocketHandler.ConnectAsync(GetCompleteUrl(url, queryParams));
            }
            catch (Exception e)
            {
                onWebSocketOpenError?.Invoke(e.Message);
                return false;
            }

            // The state, not the absence of an exception, says whether the socket opened: the
            // WebGL handler reports a refused open by ending Closed rather than by throwing. This
            // returned true for every attempt until 2026-10, so a caller waiting for the server's
            // first message after a failed open waited forever.
            if (webSocketHandler.ConnectionState != IWebSocketHandler.EWebSocketState.Open)
            {
                onWebSocketOpenError?.Invoke($"The connection to {url} did not open.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Adds a listener. Before connecting, or the first message arrives with nobody to hear it.
        /// </summary>
        public void AddListener(string url, IWebSocketListener webSocketListener)
        {
            GetWebSocketHandler(url).Listeners.Add(webSocketListener);
        }

        /// <summary>Removes a listener, telling it the socket closed as far as it is concerned.</summary>
        public void RemoveListener(string url, IWebSocketListener webSocketListener)
        {
            if (!webSocketHandlers.ContainsKey(url))
            {
                Debug.LogWarning($"[{Label}] Trying to disconnect a listener on an inactive socket! Url: {url}");
                return;
            }

            if (!webSocketHandlers[url].Listeners.Contains(webSocketListener))
            {
                Debug.LogWarning($"[{Label}] Trying to disconnect an inactive listener!");
                return;
            }

            webSocketHandlers[url].Listeners.Remove(webSocketListener);
            webSocketListener.OnWebSocketClose();
        }

        /// <summary>Closes the socket, for every listener on it.</summary>
        public async Task DisconnectAsync(string url)
        {
            IWebSocketHandler webSocketHandler = GetWebSocketHandler(url);

            while (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Connecting)
            {
                await Task.Yield();
            }

            if (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Open)
            {
                await webSocketHandler.Disconnect();
            }
        }

        /// <summary>Sends text on a socket that is open, waiting first if it is still opening.</summary>
        public async Task SendMessageAsync(string url, string message)
        {
            IWebSocketHandler webSocketHandler = GetWebSocketHandler(url);

            while (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Connecting)
            {
                await Task.Yield();
            }

            if (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Open)
            {
                await webSocketHandler.SendMessage(message);
            }
            else
            {
                Debug.LogError($"[{Label}] Trying to send message on an inactive socket! Url: {url}"
                               + ". Open a new websocket to send a message on this Url.");
            }
        }

        /// <inheritdoc cref="SendMessageAsync"/>
        public async Task SendBufferMessageAsync(string url, byte[] buffer)
        {
            IWebSocketHandler webSocketHandler = GetWebSocketHandler(url);

            while (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Connecting)
            {
                await Task.Yield();
            }

            if (webSocketHandler.ConnectionState == IWebSocketHandler.EWebSocketState.Open)
            {
                await webSocketHandler.SendBuffer(buffer);
            }
            else
            {
                Debug.LogError($"[{Label}] Trying to send buffer on an inactive socket! Url: {url}"
                               + ". Open a new websocket to send a message on this Url.");
            }
        }

        private IWebSocketHandler GetWebSocketHandler(string url)
        {
            if (!webSocketHandlers.ContainsKey(url))
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                IWebSocketHandler webSocketHandler = new WebGLWebSocketHandler();
#else
                IWebSocketHandler webSocketHandler = new WebSocketHandler();
#endif
                webSocketHandlers.Add(url, webSocketHandler);
            }

            return webSocketHandlers[url];
        }

        private string GetCompleteUrl(string url, Dictionary<string, string> queryParams)
        {
            url = ToWebSocketScheme(url);

            string queryString = string.Empty;
            if (queryParams != null)
            {
                bool isFirst = true;
                foreach (KeyValuePair<string, string> item in queryParams)
                {
                    queryString += (isFirst ? "?" : "&") + item.Key + "=" + item.Value;
                    isFirst = false;
                }
            }

            return url + queryString;
        }

        /// <summary>
        /// The same address expressed as a WebSocket URL: <c>http</c> becomes <c>ws</c>,
        /// <c>https</c> becomes <c>wss</c>, an address with no scheme at all gets one from
        /// <see cref="SecureConnection"/>, and a <c>ws</c>/<c>wss</c> URL is already right.
        /// </summary>
        /// <remarks>
        /// This used to prepend a scheme whenever the URL did not already carry <c>ws</c> or
        /// <c>wss</c> — which quietly required every caller to hand over a *schemeless* host,
        /// since anything else produced <c>wss://https://host/path</c>. That parses: the
        /// authority ends at the next slash, so the host becomes literally <c>https</c>, DNS
        /// fails to resolve it, and the only symptom is "Unable to connect to the remote
        /// server" — an error that says nothing about the address being malformed.
        /// <para>
        /// It held together because the requirement was met upstream, in the platform data:
        /// <c>ctn_config.realtimeApiUrl</c> and <c>aiApiUrl</c> are stored as bare hostnames
        /// while the other three URLs in the same object carry <c>https://</c> — and those two
        /// are exactly the APIs reached over a WebSocket. So the convention was real, just
        /// undocumented and enforced nowhere.
        /// </para>
        /// <para>
        /// <c>config.apis.cpi_baseurls</c>, which feeds endpoint discovery and the generated
        /// endpoint asset (ADR 0024 / 0025), stores the same two addresses canonically with a
        /// scheme. The two sources therefore disagree on format, and a client switching from
        /// one to the other trips this. Handling both here is what makes that switch safe, and
        /// what lets the tenant-config side be normalised later without breaking clients still
        /// reading it.
        /// </para>
        /// </remarks>
        private string ToWebSocketScheme(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsedUri))
            {
                return $"ws{(SecureConnection ? "s" : string.Empty)}://{url}";
            }

            switch (parsedUri.Scheme)
            {
                case "ws":
                case "wss":
                    return url;

                case "http":
                case "https":
                    string rest = url.Substring(url.IndexOf("://", StringComparison.Ordinal) + 3);
                    return $"ws{(parsedUri.Scheme == "https" ? "s" : string.Empty)}://{rest}";

                default:
                    Debug.LogWarning($"[{Label}] '{url}' carries the scheme '{parsedUri.Scheme}', which is neither "
                                     + "a WebSocket nor an HTTP one. Connecting to it as-is; if that fails, the "
                                     + "address is coming from somewhere that should be reporting http/https.");
                    return url;
            }
        }
    }
}
