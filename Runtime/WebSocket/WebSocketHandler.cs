using Virtuademy.SDK.WebSocket;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class WebSocketHandler : IWebSocketHandler, IDisposable
{
    private List<IWebSocketListener> listeners = new List<IWebSocketListener>();
    private ClientWebSocket webSocket = null;
    private CancellationTokenSource connectCts; // Token for connection/receive operations
    private CancellationTokenSource disconnectCts; // Token for explicit disconnection

    public List<IWebSocketListener> Listeners => listeners;
    public IWebSocketHandler.EWebSocketState ConnectionState { get; private set; }

    /// <summary>
    /// How long the opening handshake may take before the attempt is abandoned. Without one, an
    /// unreachable host left the attempt to the OS TCP timeout, which can run for minutes.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Opens the socket, or throws. A failed attempt leaves the handler Closed with its resources
    /// released, and the exception reaches the caller.
    /// </summary>
    /// <remarks>
    /// This used to log and swallow every failure, so the caller could not tell a refused
    /// connection from an open one: <see cref="Virtuademy.SDK.WebSocket.WebSocketClient"/> reported
    /// success, and whatever waited for the server's first message (the realtime handshake) waited
    /// forever.
    /// </remarks>
    public virtual async Task ConnectAsync(string url)
    {
        // If there's an existing connection, disconnect it first
        if (webSocket != null && (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.Connecting))
        {
            Debug.LogWarning("Existing connection found, disconnecting before reconnecting.");
            await Disconnect();
        }

        webSocket = new ClientWebSocket();
        connectCts = new CancellationTokenSource(); // Initialize a new CancellationTokenSource for each connection

        ConnectionState = IWebSocketHandler.EWebSocketState.Connecting;
        Debug.Log($"Attempting to connect to: {WithoutQuery(url)}");

        // The timeout applies to the opening only: connectCts also drives the receive loop.
        using CancellationTokenSource openCts = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token);
        openCts.CancelAfter(ConnectTimeout);

        try
        {
            await webSocket.ConnectAsync(new Uri(url), openCts.Token);
            ConnectionState = IWebSocketHandler.EWebSocketState.Open;
            Debug.Log("WebSocket connection opened.");

            // Start the receive routine in the background
            _ = ReceiveMessagesRoutine(); // Use '_' to suppress "await not used" warning
        }
        catch (OperationCanceledException) when (connectCts?.IsCancellationRequested != true)
        {
            Debug.LogError($"WebSocket connection timed out after {ConnectTimeout.TotalSeconds:0} s.");
            ConnectionState = IWebSocketHandler.EWebSocketState.Closed;
            Dispose();
            throw new TimeoutException($"The WebSocket connection did not open within {ConnectTimeout.TotalSeconds:0} s.");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Connection attempt cancelled.");
            ConnectionState = IWebSocketHandler.EWebSocketState.Closed;
            Dispose(); // Ensure resources are released
            throw;
        }
        catch (WebSocketException wse)
        {
            Debug.LogError($"WebSocket error during connection: {wse.Message}");
            ConnectionState = IWebSocketHandler.EWebSocketState.Closed;
            Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Generic error during connection: {ex.Message}");
            ConnectionState = IWebSocketHandler.EWebSocketState.Closed;
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The address without its query string, for the log: the realtime socket carries the user's
    /// JWT there (<c>?token=…</c>).
    /// </summary>
    private static string WithoutQuery(string url)
    {
        int query = url?.IndexOf('?') ?? -1;
        return query < 0 ? url : url.Substring(0, query) + "?<redacted>";
    }

    public virtual async Task SendMessage(string message)
    {
        // Check WebSocket state before sending
        if (webSocket == null || webSocket.State != WebSocketState.Open)
        {
            Debug.LogWarning("Attempting to send message on a non-open or null WebSocket.");
            return;
        }

        try
        {
            // Use the primary cancellation token for sending
            await webSocket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Message send cancelled.");
        }
        catch (WebSocketException wse)
        {
            Debug.LogError($"WebSocket error during send: {wse.Message}");
            // Connection might be broken, handle disconnection
            HandleDisconnection("Error during message send");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Generic error during send: {ex.Message}");
            HandleDisconnection("Generic error during message send");
        }
    }

    public virtual async Task SendBuffer(byte[] buffer)
    {
        // Check WebSocket state before sending
        if (webSocket == null || webSocket.State != WebSocketState.Open)
        {
            Debug.LogWarning("Attempting to send buffer on a non-open or null WebSocket.");
            return;
        }

        try
        {
            // Use the primary cancellation token for sending
            await webSocket.SendAsync(buffer, WebSocketMessageType.Binary, true, connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Buffer send cancelled.");
        }
        catch (WebSocketException wse)
        {
            Debug.LogError($"WebSocket error during send: {wse.Message}");
            // Connection might be broken, handle disconnection
            HandleDisconnection("Error during buffer send");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Generic error during send: {ex.Message}");
            HandleDisconnection("Generic error during buffer send");
        }
    }

    public virtual async Task Disconnect()
    {
        if (webSocket == null || (webSocket.State != WebSocketState.Open && webSocket.State != WebSocketState.CloseSent && webSocket.State != WebSocketState.CloseReceived))
        {
            Debug.Log("WebSocket is not in a state that requires disconnection or is already closed.");
            // If the socket is already closed or in an invalid state, call Dispose and set the state
            ConnectionState = IWebSocketHandler.EWebSocketState.Closed;
            Dispose();
            return;
        }

        ConnectionState = IWebSocketHandler.EWebSocketState.Closing;
        Debug.Log("Client is initiating disconnection.");

        // Cancel any ongoing receive/send operations
        connectCts?.Cancel();

        // Use a CancellationToken for CloseAsync with a timeout
        disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // Timeout for graceful close
        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", disconnectCts.Token);
            Debug.Log("CloseAsync completed successfully.");
        }
        catch (OperationCanceledException)
        {
            Debug.LogWarning("CloseAsync was cancelled due to timeout. Aborting connection.");
            webSocket.Abort(); // Force close
        }
        catch (WebSocketException wse)
        {
            Debug.LogError($"WebSocket error during CloseAsync: {wse.Message}. Aborting connection.");
            webSocket.Abort();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Generic error during CloseAsync: {ex.Message}. Aborting connection.");
            webSocket.Abort();
        }
        finally
        {
            while (webSocket != null && webSocket.State != WebSocketState.Closed)
            {
                // Wait for the WebSocket to fully close
                await Task.Yield();
            }
            // The ReceiveMessagesRoutine will handle the final state and disposal.
            // We just need to ensure the receive loop stops.
            // Final disposal will occur when the ReceiveMessagesRoutine loop terminates.
        }
    }

    /// <summary>
    /// Only for non-WebGL environment
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task ReceiveMessagesRoutine()
    {
        Debug.Log("Message receive routine started.");
        int bufferSize = 1000;
        var buffer = new byte[bufferSize];

        try
        {
            // Continue as long as the socket is open or we're processing a server-initiated close
            while (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                var offset = 0;
                var free = buffer.Length;
                WebSocketReceiveResult result = null;

                // Inner loop to read a complete message
                while (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    connectCts.Token.ThrowIfCancellationRequested(); // Check for cancellation

                    try
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, free), connectCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.Log("ReceiveAsync cancelled.");
                        break; // Exit inner loop
                    }
                    catch (WebSocketException wse)
                    {
                        Debug.LogError($"WebSocket error during receive: {wse.Message}");
                        HandleDisconnection($"Error during receive: {wse.Message}");
                        return; // Terminate receive routine
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Generic error during receive: {ex.Message}");
                        HandleDisconnection($"Generic error during receive: {ex.Message}");
                        return; // Terminate receive routine
                    }

                    offset += result.Count;
                    free -= result.Count;

                    if (result.EndOfMessage)
                        break;

                    if (free == 0)
                    {
                        // Resize buffer if necessary. Doubling, not +1000: a growth step of a
                        // fixed size copied the whole message again for every kilobyte of it.
                        var newSize = (long)buffer.Length * 2;
                        if (newSize > 2E+09) // 2GB limit
                        {
                            throw new Exception("Maximum buffer size exceeded");
                        }
                        var newBuffer = new byte[(int)newSize];
                        Array.Copy(buffer, 0, newBuffer, 0, offset);
                        buffer = newBuffer;
                        free = buffer.Length - offset;
                    }
                }

                // If we exited the inner loop due to cancellation, exit the outer one too
                if (connectCts.Token.IsCancellationRequested)
                {
                    Debug.Log("Receive routine terminated by cancellation.");
                    break;
                }

                // Process the result
                if (result != null)
                {
                    switch (result.MessageType)
                    {
                        case WebSocketMessageType.Binary:
                            Debug.Log("Binary message received.");
                            var partialBytes = new byte[offset];
                            Array.Copy(buffer, partialBytes, offset);
                            NotifyListeners(listener => listener.OnWebSocketBinaryMessageReceived(partialBytes));
                            break;
                        case WebSocketMessageType.Close:
                            Debug.Log($"Close message received from server. Status: {result.CloseStatus}, Description: {result.CloseStatusDescription}");
                            // Server initiated the close, respond with a close frame
                            // and then abort if it doesn't close in time
                            ConnectionState = IWebSocketHandler.EWebSocketState.Closing;
                            if (webSocket.State == WebSocketState.CloseReceived)
                            {
                                // Server has already sent a close message, we need to send ours
                                var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); // Timeout for CloseOutputAsync
                                try
                                {
                                    await webSocket.CloseOutputAsync(result.CloseStatus.Value, result.CloseStatusDescription, timeoutCts.Token);
                                    Debug.Log("Response to server close sent.");
                                }
                                catch (OperationCanceledException)
                                {
                                    Debug.LogWarning("CloseOutputAsync cancelled. Aborting connection.");
                                    webSocket.Abort();
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogError($"Error during CloseOutputAsync: {ex.Message}. Aborting connection.");
                                    webSocket.Abort();
                                }
                            }
                            // The routine will terminate, calling HandleDisconnection below
                            break;
                        case WebSocketMessageType.Text:
                            var message = Encoding.UTF8.GetString(buffer, 0, offset); // Use offset for correct length
                            NotifyListeners(listener => listener.OnWebSocketMessageReceived(message));
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Receive routine interrupted by external cancellation.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Critical error in receive routine: {ex.Message}");
        }
        finally
        {
            // This part will execute when the receive loop terminates (due to close, error, or cancellation)
            HandleDisconnection("Receive routine terminated.");
        }
    }

    /// <summary>
    /// Calls every listener over a snapshot of the list, isolating each one.
    /// </summary>
    /// <remarks>
    /// A listener may add or remove listeners while it runs (a close handler that reconnects,
    /// a message handler that unsubscribes), which threw out of a <c>foreach</c> over the live
    /// list. And a listener that threw used to escape into the receive loop, whose catch-all then
    /// closed the socket for everybody and cleared every listener.
    /// </remarks>
    private void NotifyListeners(Action<IWebSocketListener> notify) => NotifyListeners(listeners.ToArray(), notify);

    private static void NotifyListeners(IWebSocketListener[] targets, Action<IWebSocketListener> notify)
    {
        foreach (IWebSocketListener listener in targets)
        {
            if (listener == null)
            {
                continue;
            }

            try
            {
                notify(listener);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }

    private void HandleDisconnection(string reason)
    {
        Debug.Log($"Handling disconnection: {reason}");
        // Ensure state is closing or closed to avoid redundant calls
        if (ConnectionState != IWebSocketHandler.EWebSocketState.Closed)
        {
            ConnectionState = IWebSocketHandler.EWebSocketState.Closed;

            // Notify listeners of the closure. The list is emptied first, so a listener that
            // registers again from its close handler (to reconnect) keeps its new registration.
            IWebSocketListener[] closing = listeners.ToArray();
            listeners.Clear();
            NotifyListeners(closing, listener => listener.OnWebSocketClose());

            // Cancel any pending operations
            connectCts?.Cancel();
            disconnectCts?.Cancel();

            // Close and release socket resources
            Dispose();

            Debug.Log("WebSocket disconnected and resources released.");
        }
    }

    public void Dispose()
    {
        if (webSocket != null)
        {
            // Abort() is safe to call even if it's already closed/aborted.
            // It ensures the underlying socket resources are released.
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent || webSocket.State == WebSocketState.CloseReceived)
            {
                webSocket.Abort(); // Force close to free resources
            }
            webSocket.Dispose();
            webSocket = null;
        }

        connectCts?.Dispose();
        connectCts = null;

        disconnectCts?.Dispose();
        disconnectCts = null;
    }
}