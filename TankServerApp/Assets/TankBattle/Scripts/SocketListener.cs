﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using System.Collections.Generic;

using UnityEngine;

public class NetworkPlayer
{
    public Socket remoteSocket;
    public bool isActive;

    public static implicit operator Socket(NetworkPlayer p)
    {
        return p.remoteSocket;
    }
}

public class StateObject
{
    public Socket remoteSocket = null;                // socket to player

    public const int BufferSize = 1024;             // maximum amount of data buffered for a user
    public int bytesRead = 0;                       // bytes currently in the buffer

    public byte[] buffer = new byte[BufferSize];    // access to buffer
}
public class SocketEventArgs : EventArgs
{
    public enum SocketEventType
    {
        ACCEPT,
        READ,
        SEND
    }
    public readonly SocketEventType socketEvent;
    public readonly NetworkPlayer endpoint;

    public SocketEventArgs(SocketEventType e, NetworkPlayer remote)
    {
        socketEvent = e;
        endpoint = remote;
    }
}
public class SocketEvent
{
    public readonly SocketEventArgs eventArgs;
    public readonly byte[] data;

    public SocketEvent(SocketEventArgs e, byte[] d)
    {
        eventArgs = e;
        data = d;
    }
}
public delegate void SocketEventHandler(byte[] data, SocketEventArgs e);

// Setup a listener on a given port
public class SocketListener
{
    Socket localListener;
    List<StateObject> remoteConnections = new List<StateObject>();
    Dictionary<Socket, NetworkPlayer> players = new Dictionary<Socket, NetworkPlayer>();

    public int port { get; private set; }
    private ManualResetEvent allDone = new ManualResetEvent(false);
    public bool paused
    {
        get
        {
            return _paused;
        }
        set
        {
            if(value)
            {
                allDone.Reset(); // block
            }
            else
            {
                allDone.Set();  // allow
            }

            _paused = value;
        }
    }
    private bool _paused;

    public List<SocketEvent> events = new List<SocketEvent>();

    // TODO: StateObject is hard-coded to max out at 1024 bytes. Please define somewhere consistent.
    // TODO: Add RTCs for data size. Should not exceed max or be less than zero.
    // TODO: Genericize this to work with other types of data

    public void Send(Socket handler, String data)
    {
        byte[] byteData = Encoding.ASCII.GetBytes(data);

        handler.BeginSend(byteData, 0, byteData.Length, 0,
            new AsyncCallback(SendCallback), handler);
    }
    public void Send(Socket handler, byte[] data)
    {
        handler.BeginSend(data, 0, data.Length, 0,
            new AsyncCallback(SendCallback), handler);
    }
    public void Send(Socket handler, object data)
    {
        Send(handler, DataUtils.GetBytes(data));
    }

    private void AcceptCallback(IAsyncResult ar)
    {
        allDone.Set();

        Socket listener = (Socket)ar.AsyncState;
        Socket handler = listener.EndAccept(ar);

        var endpoint = (IPEndPoint)handler.RemoteEndPoint;

        Debug.Log("Connection from " + endpoint.Address.ToString());

        StateObject state = new StateObject();
        state.remoteSocket = handler;

        remoteConnections.Add(state);

        NetworkPlayer netPlayer = new NetworkPlayer();
        netPlayer.remoteSocket = handler;

        players[handler] = netPlayer;

        events.Add(new SocketEvent(new SocketEventArgs(SocketEventArgs.SocketEventType.ACCEPT, netPlayer), null));

        Debug.Log("Setting up to recieve from " + endpoint.Address.ToString());

        listener.BeginAccept(new AsyncCallback(AcceptCallback), listener);

        handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
            new AsyncCallback(ReadCallback), state);
    }
    private void ReadCallback(IAsyncResult ar)
    {
        StateObject state = (StateObject)ar.AsyncState;
        Socket handler = state.remoteSocket;

        int bytesRead = handler.EndReceive(ar);
        state.bytesRead += bytesRead;   // add to running total of bytesRead for this message

        try {

            if (bytesRead > 0)
            {
                //Console.WriteLine("Read {0} bytes from socket.", bytesRead);

                // TODO: Can we add generics to callbacks?
                // How can we simplify this to be reusable?

                // do we have enough data to work from?
                while (state.bytesRead >= DataUtils.SizeOf<TankBattleHeader>())
                {
                    var header = DataUtils.FromBytes<TankBattleHeader>(state.buffer);

                    //Debug.LogFormat("There are {0} unread bytes in the buffer.", state.bytesRead);

                    // have we recieved the full message?
                    if (state.bytesRead >= header.messageLength)
                    {
                        if (header.messageLength <= 0)
                        {
                            Debug.LogWarning("Invalid message length; exiting recieve loop.");
                            break;
                        }

                        events.Add(new SocketEvent(new SocketEventArgs(SocketEventArgs.SocketEventType.READ, players[handler]), (byte[])state.buffer.Clone()));
                        //Debug.Log("Message recieved.");

                        // subtract the number of bytes needed to be processed
                        state.bytesRead -= header.messageLength;

                        // remove this record from the buffer
                        state.buffer.ShiftLeft(header.messageLength);
                        //Array.Clear(state.buffer, header.messageLength, state.buffer.Length - header.messageLength);
                    }
                    else
                    {
                        Debug.Log("Need more information; expecting " + header.messageLength + " bytes of data.");
                        break;
                    }
                }
            }

            // set up to recieve again
            handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                new AsyncCallback(ReadCallback), state);
        }
        catch (Exception e)
        {
            Debug.LogError(e.Message + e.StackTrace);
        }
    }
    private void SendCallback(IAsyncResult ar)
    {
        try
        {
            Socket handler = (Socket)ar.AsyncState; // how the fuck did this become the AsyncState now?
                                                    // ... the send call is different. data is the buffer, socket is state.

            int bytesSent = handler.EndSend(ar);
            Console.WriteLine("Sent {0} bytes to client.", bytesSent);

            events.Add(new SocketEvent(new SocketEventArgs(SocketEventArgs.SocketEventType.SEND, players[handler]), null));

            Debug.Log("Message sent.");
        }
        catch (Exception e)
        {
            Debug.LogError(e.Message + e.StackTrace);
        }

        
    }

    private void Disconnect(Socket remote)
    {
        try
        {
            if (remote.Connected)
            {
                remote.Disconnect(true);

                remote.Shutdown(SocketShutdown.Both);
                remote.Close();
            }
        }
        catch (Exception e)
        {
            if (e is SocketException)
            {
                Debug.LogError("Socket ErrorCode:" + ((SocketException)e).SocketErrorCode);
            }

            Debug.LogError(e.Message + e.StackTrace);
        }
    }

    public void StartListening(int port)
    {
        // bind to all local IPv4 interfaces
        IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, port);

        Socket listener = new Socket(AddressFamily.InterNetwork,
                SocketType.Stream, ProtocolType.Tcp);

        localListener = listener;
        try
        {
            Debug.LogFormat("Binding to all local IPv4 interfaces.");
            listener.Bind(localEndPoint);
            listener.Listen(100);

            allDone.Reset();

            listener.BeginAccept(new AsyncCallback(AcceptCallback), listener);
            Debug.Log("Ready to accept connections.");

            Debug.Log("Socket Initialized.");
            Debug.LogFormat("Listening on port <{0}>.", port);
        }
        catch (Exception e)
        {
            Debug.LogError(e.ToString());
            Debug.LogError("Failed to initialize socket.");
        }
    }
    public void StopListening()
    {
        // close local socket
        Disconnect(localListener);

        // close sockets to other machines
        Debug.LogFormat("Closing {0} sockets to remote machines.", remoteConnections.Count);
        for(int i = 0; i < remoteConnections.Count; ++i)
        {
            Disconnect(remoteConnections[i].remoteSocket);
        }
    }
}