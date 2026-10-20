#if UNITY_WEBGL && !UNITY_EDITOR
using Virtuademy.SDK.WebSocket;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public class WebGLWebSocketHandler : IWebSocketHandler
{

    private List<IWebSocketListener> listeners = new List<IWebSocketListener>();

    public List<IWebSocketListener> Listeners => listeners;

    public IWebSocketHandler.EWebSocketState ConnectionState { get; set; }

    public async Task ConnectAsync(string url)
    {
        ConnectionState = IWebSocketHandler.EWebSocketState.Connecting;
        bool opened = await WebSocketMessagesHandler.Instance.Connect(url, this);

        // A failed open ends Closed, as WebSocketHandler does on the other platforms, so the
        // address can be connected again. An open the server closed right away is Closed too:
        // the close handler already set it, and setting Open here would undo that.
        if (!opened)
        {
            ConnectionState = IWebSocketHandler.EWebSocketState.Closed;
        }
    }


    public Task SendMessage(string message)
    {
        WebSocketMessagesHandler.Instance.SendMessage(this, message);
        return Task.CompletedTask;
    }

    public Task SendBuffer(byte[] buffer)
    {
        WebSocketMessagesHandler.Instance.SendBuffer(this, buffer);
        return Task.CompletedTask;
    }

    public void OnSocketClose()
    {
        IWebSocketListener[] closing = listeners.ToArray();
        listeners.Clear();
        ConnectionState = IWebSocketHandler.EWebSocketState.Closed;
        NotifyListeners(closing, listener => listener.OnWebSocketClose());
    }

    public async Task Disconnect()
    {
        ConnectionState = IWebSocketHandler.EWebSocketState.Closing;
        await WebSocketMessagesHandler.Instance.CloseSocket(this);
        OnSocketClose();
    }

    public void RemoveListener(IWebSocketListener listener)
    {
        listeners.Remove(listener);
    }

    internal void OnMessageReceived(string message)
    {
        NotifyListeners(listeners.ToArray(), listener => listener.OnWebSocketMessageReceived(message));
    }

    internal void OnBinaryMessageReceived(byte[] buffer)
    {
        NotifyListeners(listeners.ToArray(), listener => listener.OnWebSocketBinaryMessageReceived(buffer));
    }

    internal void OnSocketError()
    {
        NotifyListeners(listeners.ToArray(), listener => listener.OnWebSocketError("WebGl socket error."));
    }

    /// <summary>
    /// Calls every listener over a snapshot, isolating each one: a listener that unsubscribes
    /// while it runs no longer breaks the iteration, and one that throws no longer stops the
    /// others from hearing the message.
    /// </summary>
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
                UnityEngine.Debug.LogException(ex);
            }
        }
    }
}
#endif
