﻿﻿using Scarlet.Utilities;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO;

namespace Scarlet.Communications
{
    public static class Client
    {
        private static TcpClient ClientTCP;
        private static UdpClient ClientUDP;
        private static Thread SendThread, ReceiveThreadTCP, ReceiveThreadUDP, ProcessThread;
        private static Queue<Packet> SendQueue, ReceiveQueue;
        private static bool Initialized;
        private static bool Stopping;
        private static int ReceiveBufferSize, OperationPeriod;
        private const int TIMEOUT = 5000;

        public static bool StorePackets;
        public static List<Packet> PacketsReceived { get; private set; }
        public static List<Packet> PacketsSent { get; private set; }

        /// <summary>
        /// Starts a Client process.
        /// </summary>
        /// <param name="ServerIP">String representation of the IP Address of server.</param>
        /// <param name="PortTCP">Target port for TCP Communications on the server.</param>
        /// <param name="PortUDP">Target port for UDP Communications on the server.</param>
        /// <param name="ReceiveBufferSize">Size of buffer for incoming data.</param>
        /// <param name="OperationPeriod">Time in between receiving and sending individual packets.</param>
        public static void Start(string ServerIP, int PortTCP, int PortUDP, int ReceiveBufferSize = 64, int OperationPeriod = 20)
        {
            if (!Initialized)
            {
                SendQueue = new Queue<Packet>();
                ReceiveQueue = new Queue<Packet>();
                SendThread = new Thread(new ThreadStart(SendPackets));
                ProcessThread = new Thread(new ThreadStart(ProcessPackets));
				ReceiveThreadTCP = new Thread(new ParameterizedThreadStart(ReceiveFromSocket));
				ReceiveThreadUDP = new Thread(new ParameterizedThreadStart(ReceiveFromSocket));
            }
            IPAddress IP = IPAddress.Parse(ServerIP);
            ClientTCP = new TcpClient(new IPEndPoint(IP, PortTCP));
            ClientUDP = new UdpClient(new IPEndPoint(IP, PortUDP));
            Client.ReceiveBufferSize = ReceiveBufferSize;
            Client.OperationPeriod = OperationPeriod;
            if (!ClientTCP.Connected) { Log.Output(Log.Severity.INFO, Log.Source.NETWORK, "No TCP Server Found"); }
            Initialized = true;
            StartThreads();
        }

        /// <summary>
        /// Starts all primary threads.
        /// </summary>
        private static void StartThreads()
        {
            SendThread.Start();
            ReceiveThreadTCP.Start(ClientTCP.Client);
            ReceiveThreadUDP.Start(ClientUDP.Client);
            ProcessThread.Start();
            SendThread.Join();
            ReceiveThreadTCP.Join();
            ReceiveThreadUDP.Join();
            ProcessThread.Join();
            Initialized = false;
        }

        /// <summary>
        /// Stops the Client completely.
        /// </summary>
        public static void Stop()
        {
            Stopping = true; // Invokes thread joining in StartThreads() due to thread loops (Recommended on SO)
            ClientTCP.GetStream().Close();
            ClientTCP.Close();
            ClientUDP.Close();
            Initialized = false; // Ensure initialized status is false when stopped
        }

        #region Receive

        /// <summary>
        /// Continuously receives from socket, until 
        /// Client.Stopping is true. Automatically distributes
        /// incoming messages to approprate locations.
        /// </summary>
        /// <param name="Socket">Socket to receive on.</param>
        private static void ReceiveFromSocket(object Socket)
        {
			Socket ReceiveFrom = (Socket)Socket;
            while (!Stopping)
            {
                if (ReceiveFrom.Available > 0)
                {
                    byte[] ReceiveBuffer = new byte[Client.ReceiveBufferSize];
                    try
                    {
                        ReceiveFrom.Receive(ReceiveBuffer);
                    }
                    catch (SocketException Exception)
                    {
                        Log.Output(Log.Severity.ERROR, Log.Source.NETWORK, "Failed to receive from socket. Check connection status.");
                        Log.Exception(Log.Source.NETWORK, Exception);
                    }
                    Packet Received = new Packet(new Message(ReceiveBuffer),
                                                 ReceiveFrom.ProtocolType == ProtocolType.Udp,
                                                 (IPEndPoint)ClientTCP.Client.RemoteEndPoint);
                    if (StorePackets) { PacketsReceived.Add(Received); }
                    lock (ReceiveQueue) { ReceiveQueue.Enqueue(Received); }
                    Thread.Sleep(OperationPeriod);
                }
            }
        }

        /// <summary>
        /// Handles packets as they are received
        /// from server.
        /// </summary>
        private static void ProcessPackets()
        {
            while (!Stopping)
            {
                bool HasPackets = false;
                lock(ReceiveQueue) { HasPackets = ReceiveQueue.Count != 0; }
                if (HasPackets)
                {
                    Packet Processing;
                    lock (ReceiveQueue) { Processing = ReceiveQueue.Dequeue(); }
                    Parse.ParseMessage(Processing);
                }
            }
        }

        #endregion

        #region Send

        /// <summary>
        /// Sends a packet. Handles both UDP and TCP.
        /// </summary>
        /// <param name="SendPacket">Packet to send</param>
        /// <returns>Success of packet sending</returns>
        public static bool Send(Packet SendPacket)
        {
            if (!Initialized) { throw new InvalidOperationException("Cannot use Client before initialization. Call Client.Start()."); }
            if (SendPacket.IsUDP) { return SendNow(SendPacket); }
            if (!ClientTCP.Connected)
            {
                Log.Output(Log.Severity.ERROR, Log.Source.NETWORK, "Attemping to send TCP packet without TCP server connection. Check connection status.");
                return false;
            }
            else
            {
                lock (SendQueue) { SendQueue.Enqueue(SendPacket); }
                return true;
            }
        }

        /// <summary>
        /// Sends a packet asynchronously, 
        /// handles both UDP and TCP Packets.
        /// </summary>
        /// <param name="SendPacket">Packet to send.</param>
        /// <returns>Success of packet sending.</returns>
        public static bool SendNow(Packet SendPacket)
        {
            if (!Initialized) { throw new InvalidOperationException("Cannot use Client before initialization. Call Client.Start()."); }
            if (SendPacket.IsUDP)
            {
                int BytesSent = 0;
                try
                {
                    BytesSent = ClientUDP.Send(SendPacket.GetForSend(), SendPacket.GetForSend().Length);
                }
                catch (SocketException Exception)
                {
                    Log.Output(Log.Severity.ERROR, Log.Source.NETWORK, "An error occurred when accessing the socket. Check connection status.");
                    Log.Exception(Log.Source.NETWORK, Exception);
                    return false;
                }
                catch (ObjectDisposedException Exception)
				{
					Log.Output(Log.Severity.ERROR, Log.Source.NETWORK, "Client UDP socket stream is closed. Attempting to reconnect... Consider restart, check connection status.");
					Log.Exception(Log.Source.NETWORK, Exception);
                    // Attempt to reconnect
                    if (!ClientTCP.Connected)
                    {
						IPEndPoint RemoteEndpoint = (IPEndPoint)ClientUDP.Client.RemoteEndPoint;
						ClientUDP.Connect(RemoteEndpoint.Address, RemoteEndpoint.Port);
                        if (ClientTCP.Connected) { return SendNow(SendPacket); }
                    }
                    return false;
				}
                Thread.Sleep(OperationPeriod);
                if (BytesSent != 0 && StorePackets) { PacketsSent.Add(SendPacket); }
                return BytesSent != 0;
            }
            else
            { // Use TCP
                if (!ClientTCP.Connected)
                {
                    Log.Output(Log.Severity.ERROR, Log.Source.NETWORK, "Attemping to send TCP packet without TCP server connection. Check connection status.");
                }
                else
                {
                    try
                    {
                        ClientTCP.GetStream().Write(SendPacket.GetForSend(), 0, SendPacket.GetForSend().Length);
                    }
                    catch (IOException Exception)
                    {
                        Log.Output(Log.Severity.ERROR, Log.Source.NETWORK, "Failed to write to socket stream. Check connection status.");
                        Log.Exception(Log.Source.NETWORK, Exception);
                        return false;
                    }
                    catch (ObjectDisposedException Exception)
                    {
                        Log.Output(Log.Severity.ERROR, Log.Source.NETWORK, "Client TCP socket stream is closed. Attempting to reconnect... Consider restart, check connection status.");
                        Log.Exception(Log.Source.NETWORK, Exception);
                        // Attempt to reconnect
                        if (!ClientTCP.Connected)
                        {
                            IPEndPoint RemoteEndpoint = (IPEndPoint)ClientTCP.Client.RemoteEndPoint;
                            ClientTCP.Connect(RemoteEndpoint.Address, RemoteEndpoint.Port);
                            if (ClientTCP.Connected) { return SendNow(SendPacket); } // Tries to resend if connection established.
                            return false;
                        }

                    }
                    if (StorePackets) { PacketsSent.Add(SendPacket); }
                    Thread.Sleep(OperationPeriod);
                }
                return true;
            }
        }

        /// <summary>
        /// Iteratively sends packets that 
        /// are in the send queue.
        /// </summary>
        private static void SendPackets()
        {
            while (!Stopping)
            {
                bool HasPacket;
                lock (SendQueue) { HasPacket = SendQueue.Count > 0; }
                if (HasPacket)
                {
                    Packet ToSend;
                    lock (SendQueue) { ToSend = (Packet)(SendQueue.Peek().Clone()); }
                    try
                    {
                        SendNow(ToSend);
                        lock (SendQueue) { SendQueue.Dequeue(); } // Remove the packet from the queue when it has been sent sucessfully.
                    }
                    catch (Exception Exc)
                    {
                        Log.Output(Log.Severity.WARNING, Log.Source.NETWORK, "Failed to send packet.");
                        Log.Exception(Log.Source.NETWORK, Exc);
                    }
                }
                Thread.Sleep(OperationPeriod);
            }
        }

        #endregion

        #region Info
        public static int GetReceiveQueueLength() { return ReceiveQueue.Count; }
        public static int GetSendQueueLength() { return SendQueue.Count; }
        #endregion

    }
}
