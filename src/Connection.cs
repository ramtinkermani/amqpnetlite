//  ------------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation
//  All rights reserved. 
//  
//  Licensed under the Apache License, Version 2.0 (the ""License""); you may not use this 
//  file except in compliance with the License. You may obtain a copy of the License at 
//  http://www.apache.org/licenses/LICENSE-2.0  
//  
//  THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, 
//  EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR 
//  CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR 
//  NON-INFRINGEMENT. 
// 
//  See the Apache Version 2.0 License for specific language governing permissions and 
//  limitations under the License.
//  ------------------------------------------------------------------------------------

namespace Amqp
{
    using Amqp.Framing;
    using Amqp.Sasl;
    using Amqp.Types;
    using System;
    using System.Threading;

    public class Connection : AmqpObject
    {
        enum State
        {
            Start,
            HeaderSent,
            OpenPipe,
            OpenClosePipe,
            HeaderReceived,
            HeaderExchanged,
            OpenSent,
            OpenReceived,
            Opened,
            CloseReceived,
            ClosePipe,
            CloseSent,
            End
        }

        public static bool DisableServerCertValidation;

        const uint DefaultMaxFrameSize = 16 * 1024;
        const int MaxSessions = 4;
        readonly Address address;
        readonly Session[] localSessions;
        readonly Session[] remoteSessions;
        State state;
        ITransport transport;
        uint maxFrameSize;
        Pump reader;
        Writer writer;

        public Connection(Address address)
        {
            this.address = address;
            this.localSessions = new Session[MaxSessions];
            this.remoteSessions = new Session[MaxSessions];
            this.maxFrameSize = DefaultMaxFrameSize;
            this.writer = new Writer(this);
            this.Connect();
        }

        object ThisLock
        {
            get { return this; }
        }

        internal ushort AddSession(Session session)
        {
            lock (this.ThisLock)
            {
                this.ThrowIfClosed("AddSession");
                this.StartIfNeeded();

                for (int i = 0; i < this.localSessions.Length; ++i)
                {
                    if (this.localSessions[i] == null)
                    {
                        this.localSessions[i] = session;
                        return (ushort)i;
                    }
                }

                throw new AmqpException(ErrorCode.NotAllowed,
                    Fx.Format(SRAmqp.AmqpHandleExceeded, MaxSessions));
            }
        }

        internal void SendCommand(ushort channel, DescribedList command, int initBufferSize)
        {
            this.SendCommand(channel, command, initBufferSize, null);
        }

        internal void SendCommand(ushort channel, DescribedList command, int initBufferSize, ByteBuffer payload)
        {
            this.ThrowIfClosed("Send");
            ByteBuffer buffer = Frame.GetBuffer(FrameType.Amqp, channel, command, initBufferSize, payload == null ? 0 : payload.Length);
            if (payload != null)
            {
                int payloadSize = Math.Min(payload.Length, (int)this.maxFrameSize - buffer.Length);
                AmqpBitConverter.WriteBytes(buffer, payload.Buffer, payload.Offset, payloadSize);
                payload.Complete(payloadSize);
            }

            this.writer.Write(buffer);
            Trace.WriteLine(TraceLevel.Frame, "SEND (ch={0}) {1}", channel, command);
        }

        protected override bool OnClose(Error error = null)
        {
            lock (this.ThisLock)
            {
                State newState = State.Start;
                if (this.state == State.OpenPipe )
                {
                    newState = State.OpenClosePipe;
                }
                else if (state == State.OpenSent)
                {
                    newState = State.ClosePipe;
                }
                else if (this.state == State.Opened)
                {
                    newState = State.CloseSent;
                }
                else if (this.state == State.CloseReceived)
                {
                    newState = State.End;
                }
                else if (this.state == State.End)
                {
                    return true;
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "Close", this.state));
                }

                this.SendClose(error);
                this.state = newState;
                return this.state == State.End;
            }
        }

        void Connect()
        {
            TcpTransport tcpTransport = new TcpTransport();
            if (!tcpTransport.ConnectAsync(
                    this.address.Host,
                    this.address.Port,
                    this.address.UseSsl ? this.address.Host : null,
                    DisableServerCertValidation,
                    OnTcpConnect,
                    this))
            {
                OnTcpConnect(tcpTransport, true, null, this);
            }
        }

        static void OnTcpConnect(ITransport tcpTransport, bool syncComplete, Exception exception, object state)
        {
            Connection thisPtr = (Connection)state;
            if (exception != null)
            {
                thisPtr.OnIoException(exception);
                return;
            }

            try
            {
                if (thisPtr.address.User != null)
                {
                    SaslTransport saslTransport = new SaslTransport(tcpTransport);
                    saslTransport.Open(thisPtr.address.Host, new SaslPlanProfile(thisPtr.address.User, thisPtr.address.Password));
                    OnConnect(saslTransport, null, thisPtr);
                }
                else
                {
                    OnConnect(tcpTransport, null, thisPtr);
                }
            }
            catch (Exception exception2)
            {
                if (syncComplete)
                {
                    throw;
                }
                else
                {
                    thisPtr.OnIoException(exception2);
                }
            }
        }

        static void OnConnect(ITransport transport, Exception exception, object state)
        {
            Connection thisPtr = (Connection)state;
            if (exception != null)
            {
                thisPtr.OnIoException(exception);
                return;
            }

            thisPtr.transport = transport;
            lock (thisPtr.ThisLock)
            {
                thisPtr.StartIfNeeded();
            }

            thisPtr.reader = new Pump(thisPtr);
            thisPtr.reader.Start();
        }

        void ThrowIfClosed(string operation)
        {
            if (this.state >= State.ClosePipe)
            {
                throw new AmqpException(ErrorCode.IllegalState,
                    Fx.Format(SRAmqp.AmqpIllegalOperationState, operation, this.state));
            }
        }

        void StartIfNeeded()
        {
            // need to be called with lock held
            if (this.state == State.Start)
            {
                this.SendHeader();
                this.SendOpen();
                this.state = State.OpenPipe;
            }
        }

        void SendHeader()
        {
            byte[] header = new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 1, 0, 0 };
            this.writer.Write(new ByteBuffer(header, 0, header.Length));
            Trace.WriteLine(TraceLevel.Frame, "SEND AMQP 0 1.0.0");
        }

        void SendOpen()
        {
            Open open = new Open()
            {
                ContainerId = Guid.NewGuid().ToString(),
                HostName = this.address.Host,
                MaxFrameSize = this.maxFrameSize,
                ChannelMax = MaxSessions - 1
            };

            this.SendCommand(0, open, 128);
        }

        void SendClose(Error error)
        {
            this.SendCommand(0, new Close() { Error = error }, 128);
        }

        void OnOpen(Open open)
        {
            lock (this.ThisLock)
            {
                if (this.state == State.OpenSent)
                {
                    this.state = State.Opened;
                }
                else if (this.state == State.ClosePipe)
                {
                    this.state = State.CloseSent;
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "OnOpen", this.state));
                }

                if (open.MaxFrameSize < this.maxFrameSize)
                {
                    this.maxFrameSize = open.MaxFrameSize;
                }
            }
        }

        void OnClose(Close close)
        {
            lock (this.ThisLock)
            {
                if (this.state == State.Opened)
                {
                    this.SendClose(null);
                }
                else if (this.state == State.CloseSent)
                {
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "OnClose", this.state));
                }

                this.state = State.End;
                this.OnEnded(close.Error);
            }
        }

        void OnBegin(ushort remoteChannel, Begin begin)
        {
            lock (this.ThisLock)
            {
                Session session = this.GetSession(this.localSessions, begin.RemoteChannel);
                session.OnBegin(remoteChannel, begin);
                this.remoteSessions[remoteChannel] = session;
            }
        }

        void OnEnd(ushort remoteChannel, End end)
        {
            Session session = this.GetSession(this.remoteSessions, remoteChannel);
            if (session.OnEnd(end))
            {
                lock (this.ThisLock)
                {
                    this.localSessions[session.Channel] = null;
                    this.remoteSessions[remoteChannel] = null;
                }
            }
        }

        void OnSessionCommand(ushort remoteChannel, DescribedList command, ByteBuffer buffer)
        {
            this.GetSession(this.remoteSessions, remoteChannel).OnCommand(command, buffer);
        }

        Session GetSession(Session[] sessions, ushort channel)
        {
            lock (this.ThisLock)
            {
                Session session = null;
                if (channel < sessions.Length)
                {
                    session = sessions[channel];
                }

                if (session == null)
                {
                    throw new AmqpException(ErrorCode.NotFound,
                        Fx.Format(SRAmqp.AmqpChannelNotFound, channel));
                }

                return session;
            }
        }

        void OnHeader(ProtocolHeader header)
        {
            Trace.WriteLine(TraceLevel.Frame, "RECV AMQP {0}", header);
            lock (this.ThisLock)
            {
                if (this.state == State.OpenPipe)
                {
                    this.state = State.OpenSent;
                }
                else if (this.state == State.OpenClosePipe)
                {
                    this.state = State.ClosePipe;
                }
                else
                {
                    throw new AmqpException(ErrorCode.IllegalState,
                        Fx.Format(SRAmqp.AmqpIllegalOperationState, "OnHeader", this.state));
                }

                if (header.Major != 1 || header.Minor != 0 || header.Revision != 0)
                {
                    throw new AmqpException(ErrorCode.NotImplemented, header.ToString());
                }
            }
        }

        void OnFrame(ByteBuffer buffer)
        {
            try
            {
                ushort channel;
                DescribedList command;
                Frame.GetFrame(buffer, out channel, out command);
                Trace.WriteLine(TraceLevel.Frame, "RECV (ch={0}) {1}", channel, command);

                if (command.Descriptor.Code == Codec.Open.Code)
                {
                    this.OnOpen((Open)command);
                }
                else if (command.Descriptor.Code == Codec.Close.Code)
                {
                    this.OnClose((Close)command);
                }
                else if (command.Descriptor.Code == Codec.Begin.Code)
                {
                    this.OnBegin(channel, (Begin)command);
                }
                else if (command.Descriptor.Code == Codec.End.Code)
                {
                    this.OnEnd(channel, (End)command);
                }
                else
                {
                    this.OnSessionCommand(channel, command, buffer);
                }
            }
            catch (Exception exception)
            {
                this.OnException(exception);
            }
        }

        void OnException(Exception exception)
        {
            Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", exception.ToString());
            AmqpException amqpException = exception as AmqpException;
            Error error = amqpException != null ?
                amqpException.Error :
                new Error() { Condition = ErrorCode.InternalError, Description = exception.Message };

            if (this.state < State.ClosePipe)
            {
                try
                {
                    this.Close(0, error);
                }
                catch
                {
                    this.state = State.End;
                }
            }
            else
            {
                this.state = State.End;
            }

            if (this.state == State.End)
            {
                this.OnEnded(error);
            }
        }

        void OnIoException(Exception exception)
        {
            if (this.state != State.End)
            {
                this.state = State.End;
                this.OnEnded(new Error() { Condition = ErrorCode.ConnectionForced });
            }
        }

        void OnEnded(Error error)
        {
            if (this.transport != null)
            {
                this.transport.Close();
            }

            this.NotifyClosed(error);
        }

        sealed class Writer
        {
            readonly Connection connection;
            ByteBuffer[] outgoingQueue;
            int head;
            int count;

            public Writer(Connection connection)
            {
                this.connection = connection;
                this.outgoingQueue = new ByteBuffer[8];
            }

            public void Write(ByteBuffer buffer)
            {
                bool shouldWrite;
                lock (this)
                {
                    shouldWrite = this.Enqueue(buffer);
                }

                if (shouldWrite)
                {
                    this.WriteCore(buffer);
                }
            }

            void WriteCore(ByteBuffer buffer)
            {
                do
                {
                    try
                    {
                        this.connection.transport.Send(buffer);
                    }
                    catch (Exception exception)
                    {
                        this.connection.OnIoException(exception);
                        break;
                    }

                    lock (this)
                    {
                        this.Dequeue(out buffer);
                    }
                }
                while (buffer != null);
            }

            bool Enqueue(ByteBuffer buffer)
            {
                if (this.count == this.outgoingQueue.Length)
                {
                    ByteBuffer[] expanded = new ByteBuffer[this.count * 2];
                    int c1 = this.count - this.head;
                    Array.Copy(this.outgoingQueue, this.head, expanded, 0, c1);
                    if (this.head > 0)
                    {
                        Array.Copy(this.outgoingQueue, 0, expanded, c1, this.head);
                    }

                    this.outgoingQueue = expanded;
                    this.head = 0;
                }

                int index = (this.head + this.count) % this.outgoingQueue.Length;
                this.outgoingQueue[index] = buffer;
                this.count++;

                return this.count == 1 && this.connection.transport != null;
            }

            void Dequeue(out ByteBuffer next)
            {
                next = null;
                this.outgoingQueue[this.head] = null;
                this.head = (this.head + 1) % this.outgoingQueue.Length;
                if (--this.count > 0)
                {
                    next = this.outgoingQueue[this.head];
                }
            }
        }

        sealed class Pump
        {
            readonly Connection connection;

            public Pump(Connection connection)
            {
                this.connection = connection;
            }

            public void Start()
            {
                Fx.StartThread(this.PumpThread);
            }

            void PumpThread()
            {
                try
                {
                    ProtocolHeader header = Reader.ReadHeader(this.connection.transport);
                    this.connection.OnHeader(header);
                }
                catch (Exception exception)
                {
                    this.connection.OnIoException(exception);
                    return;
                }

                byte[] sizeBuffer = new byte[FixedWidth.UInt];
                while (sizeBuffer != null && this.connection.state != State.End)
                {
                    try
                    {
                        ByteBuffer buffer = Reader.ReadFrameBuffer(this.connection.transport, sizeBuffer, this.connection.maxFrameSize);
                        if (buffer != null)
                        {
                            this.connection.OnFrame(buffer);
                        }
                        else
                        {
                            sizeBuffer = null;
                        }
                    }
                    catch (Exception exception)
                    {
                        this.connection.OnIoException(exception);
                    }
                }
            }
        }
    }
}