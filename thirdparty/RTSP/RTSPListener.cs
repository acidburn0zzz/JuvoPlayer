﻿namespace Rtsp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using Rtsp.Messages;
    using System.Threading;

    /// <summary>
    /// Rtsp lister
    /// </summary>
    public class RtspListener : IDisposable
    {
//        private static NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        private IRtspTransport _transport;

        private Thread _listenTread;
        private Stream _stream;

        private int _sequenceNumber;

        private Dictionary<int, RtspRequest> _sentMessage = new Dictionary<int, RtspRequest>();

        /// <summary>
        /// Initializes a new instance of the <see cref="RtspListener"/> class from a TCP connection.
        /// </summary>
        /// <param name="connection">The connection.</param>
        public RtspListener(IRtspTransport connection)
        {
            //            Contract.EndContractBlock();

            _transport = connection ?? throw new ArgumentNullException("connection");
            _stream = connection.GetStream();
        }

        /// <summary>
        /// Gets the remote address.
        /// </summary>
        /// <value>The remote adress.</value>
        public string RemoteAdress
        {
            get
            {
                return _transport.RemoteAddress;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        public void Start()
        {
            _listenTread = new Thread(new ThreadStart(DoJob));
            _listenTread.Start();
        }

        /// <summary>
        /// Stops this instance.
        /// </summary>
        public void Stop()
        {
            // brutally  close the TCP socket....
            // I hope the teardown was sent elsewhere
            _transport.Close();

        }

        /// <summary>
        /// Occurs when message is received.
        /// </summary>
        public event EventHandler<RtspChunkEventArgs> MessageReceived;

        /// <summary>
        /// Raises the <see cref="E:MessageReceived"/> event.
        /// </summary>
        /// <param name="e">The <see cref="Rtsp.RtspChunkEventArgs"/> instance containing the event data.</param>
        protected void OnMessageReceived(RtspChunkEventArgs e)
        {
            MessageReceived?.Invoke(this, e);
        }

        /// <summary>
        /// Occurs when message is received.
        /// </summary>
        public event EventHandler<RtspChunkEventArgs> DataReceived;

        /// <summary>
        /// Raises the <see cref="E:DataReceived"/> event.
        /// </summary>
        /// <param name="rtspChunkEventArgs">The <see cref="Rtsp.RtspChunkEventArgs"/> instance containing the event data.</param>
        protected void OnDataReceived(RtspChunkEventArgs rtspChunkEventArgs)
        {
            DataReceived?.Invoke(this, rtspChunkEventArgs);
        }

        /// <summary>
        /// Does the reading job.
        /// </summary>
        /// <remarks>
        /// This method read one message from TCP connection.
        /// If it a response it add the associate question.
        /// The sopping is made by the closing of the TCP connection.
        /// </remarks>
        private void DoJob()
        {
            try
            {
//                _logger.Debug("Connection Open");
                while (_transport.Connected)
                {
                    // La lectuer est blocking sauf si la connection est coupé
                    RtspChunk currentMessage = ReadOneMessage(_stream);

                    if (currentMessage != null)
                    {
                        if (!(currentMessage is RtspData))
                        {
                            // on logue le tout
                            //if (currentMessage.SourcePort != null)
                            //    _logger.Debug(CultureInfo.InvariantCulture, "Receive from {0}", currentMessage.SourcePort.RemoteAdress);
                            currentMessage.LogMessage();
                        }
                        if (currentMessage is RtspResponse)
                        {

                            RtspResponse response = currentMessage as RtspResponse;
                            lock (_sentMessage)
                            {
                                // add the original question to the response.
                                if (_sentMessage.TryGetValue(response.CSeq, out RtspRequest originalRequest))
                                {
                                    _sentMessage.Remove(response.CSeq);
                                    response.OriginalRequest = originalRequest;
                                }
                                else
                                {
                                    //_logger.Warn(CultureInfo.InvariantCulture, "Receive response not asked {0}", response.CSeq);
                                }
                            }
                            OnMessageReceived(new RtspChunkEventArgs(response));

                        }
                        else if (currentMessage is RtspRequest)
                        {
                            OnMessageReceived(new RtspChunkEventArgs(currentMessage));
                        }
                        else if (currentMessage is RtspData)
                        {
                            OnDataReceived(new RtspChunkEventArgs(currentMessage));
                        }

                    }
                    else
                    {
                        _stream.Dispose();
                        _transport.Close();
                    }
                }
                //_logger.Debug("Connection Close");
            }
            catch (IOException error)
            {
                //_logger.Warn("IO Error", error);
                _stream.Dispose();
                _transport.Close();
            }
            catch (SocketException error)
            {
                //_logger.Warn("Socket Error", error);
                _stream.Dispose();
                _transport.Close();
            }
            catch (ObjectDisposedException error)
            {
                //_logger.Warn("Object Disposed", error);
            }
            catch (Exception error)
            {
                //_logger.Warn("Unknow Error", error);
                throw;
            }
        }

        //[Serializable]
        private enum ReadingState
        {
            NewCommand,
            Headers,
            Data,
            End,
            InterleavedData,
            MoreInterleavedData,
        }

        /// <summary>
        /// Sends the message.
        /// </summary>
        /// <param name="message">A message.</param>
        /// <returns><see cref="true"/> if it is Ok, otherwise <see cref="false"/></returns>
        public bool SendMessage(RtspMessage message)
        {
            if (message == null)
                throw new ArgumentNullException("message");
//            Contract.EndContractBlock();

            if (!_transport.Connected)
            {
//                _logger.Warn("Reconnect to a client, strange !!");
                try
                {
                    Reconnect();
                }
                catch (SocketException)
                {
                    // on a pas put se connecter on dit au manager de plus compter sur nous
                    return false;
                }
            }

            // if it it a request  we store the original message
            // and we renumber it.
            //TODO handle lost message (for example every minute cleanup old message)
            if (message is RtspRequest)
            {
                RtspMessage originalMessage = message;
                // Do not modify original message
                message = message.Clone() as RtspMessage;
                _sequenceNumber++;
                message.CSeq = _sequenceNumber;
                lock (_sentMessage)
                {  
                    _sentMessage.Add(message.CSeq, originalMessage as RtspRequest);
                }
            }

//            _logger.Debug("Send Message");
            message.LogMessage();
            message.SendTo(_stream);
            return true;
        }

        /// <summary>
        /// Reconnect this instance of RtspListener.
        /// </summary>
        /// <exception cref="System.Net.Sockets.SocketException">Error during socket </exception>
        public void Reconnect()
        {
            //if it is already connected do not reconnect
            if (_transport.Connected)
                return;

            // If it is not connected listenthread should have die.
            if (_listenTread != null && _listenTread.IsAlive)
                _listenTread.Join();

            if (_stream != null)
                _stream.Dispose();

            // reconnect 
            _transport.Reconnect();
            _stream = _transport.GetStream();

            // If listen thread exist restart it
            if (_listenTread != null)
                Start();
        }

        /// <summary>
        /// Reads one message.
        /// </summary>
        /// <param name="commandStream">The Rtsp stream.</param>
        /// <returns>Message readen</returns>
        public RtspChunk ReadOneMessage(Stream commandStream)
        {
            if (commandStream == null)
                throw new ArgumentNullException("commandStream");
//            Contract.EndContractBlock();

            ReadingState currentReadingState = ReadingState.NewCommand;
            // current decode message , create a fake new to permit compile.
            RtspChunk currentMessage = null;

            int size = 0;
            int byteReaden = 0;
            List<byte> buffer = new List<byte>(256);
            string oneLine = String.Empty;
            while (currentReadingState != ReadingState.End)
            {

                // if the system is not reading binary data.
                if (currentReadingState != ReadingState.Data && currentReadingState != ReadingState.MoreInterleavedData)
                {
                    oneLine = String.Empty;
                    bool needMoreChar = true;
                    // I do not know to make readline blocking
                    while (needMoreChar)
                    {
                        int currentByte = commandStream.ReadByte();

                        switch (currentByte)
                        {
                            case -1:
                                // the read is blocking, so if we got -1 it is because the client close;
                                currentReadingState = ReadingState.End;
                                needMoreChar = false;
                                break;
                            case '\n':
                                oneLine = ASCIIEncoding.UTF8.GetString(buffer.ToArray());
                                buffer.Clear();
                                needMoreChar = false;
                                break;
                            case '\r':
                                // simply ignore this
                                break;
                            case '$': // if first caracter of packet is $ it is an interleaved data packet
                                if (currentReadingState == ReadingState.NewCommand && buffer.Count == 0)
                                {
                                    currentReadingState = ReadingState.InterleavedData;
                                    needMoreChar = false;
                                }
                                else
                                    goto default;
                                break;
                            default:
                                buffer.Add((byte)currentByte);
                                break;
                        }
                    }
                }

                switch (currentReadingState)
                {
                    case ReadingState.NewCommand:
                        currentMessage = RtspMessage.GetRtspMessage(oneLine);
                        currentReadingState = ReadingState.Headers;
                        break;
                    case ReadingState.Headers:
                        string line = oneLine;
                        if (string.IsNullOrEmpty(line))
                        {
                            currentReadingState = ReadingState.Data;
                            ((RtspMessage)currentMessage).InitialiseDataFromContentLength();
                        }
                        else
                        {
                            ((RtspMessage)currentMessage).AddHeader(line);
                        }
                        break;
                    case ReadingState.Data:
                        if (currentMessage.Data.Length > 0)
                        {
                            // Read the remaning data
                            byteReaden += commandStream.Read(currentMessage.Data, byteReaden,
                                currentMessage.Data.Length - byteReaden);
//                            _logger.Debug(CultureInfo.InvariantCulture, "Readen {0} byte of data", byteReaden);
                        }
                        // if we haven't read all go there again else go to end. 
                        if (byteReaden >= currentMessage.Data.Length)
                            currentReadingState = ReadingState.End;
                        break;
                    case ReadingState.InterleavedData:
                        currentMessage = new RtspData();
                        ((RtspData)currentMessage).Channel = commandStream.ReadByte();
                        size = (commandStream.ReadByte() << 8) + commandStream.ReadByte();
                        currentMessage.Data = new byte[size];
                        currentReadingState = ReadingState.MoreInterleavedData;
                        break;
                    case ReadingState.MoreInterleavedData:
                        // apparently non blocking
                        byteReaden += commandStream.Read(currentMessage.Data, byteReaden, size - byteReaden);
                        if (byteReaden < size)
                            currentReadingState = ReadingState.MoreInterleavedData;
                        else
                            currentReadingState = ReadingState.End;
                        break;
                    default:
                        break;
                }
            }
            if (currentMessage != null)
                currentMessage.SourcePort = this;
            return currentMessage;
        }

//        /// <summary>
//        /// Begins the send data.
//        /// </summary>
//        /// <param name="aRtspData">A Rtsp data.</param>
//        /// <param name="asyncCallback">The async callback.</param>
//        /// <param name="aState">A state.</param>
//        public IAsyncResult BeginSendData(RtspData aRtspData, AsyncCallback asyncCallback, object state)
//        {
//            if (aRtspData == null)
//                throw new ArgumentNullException("aRtspData");
////            Contract.EndContractBlock();

//            return BeginSendData(aRtspData.Channel, aRtspData.Data, asyncCallback, state);
//        }

//        /// <summary>
//        /// Begins the send data.
//        /// </summary>
//        /// <param name="channel">The channel.</param>
//        /// <param name="frame">The frame.</param>
//        /// <param name="asyncCallback">The async callback.</param>
//        /// <param name="aState">A state.</param>
//        public IAsyncResult BeginSendData(int channel, byte[] frame, AsyncCallback asyncCallback, object state)
//        {
//            if (frame == null)
//                throw new ArgumentNullException("frame");
//            if (frame.Length > 0xFFFF)
//                throw new ArgumentException("frame too large", "frame");
////            Contract.EndContractBlock();

//            if (!_transport.Connected)
//            {
// //               _logger.Warn("Reconnect to a client, strange !!");
//                Reconnect();
//            }

//            byte[] data = new byte[4 + frame.Length]; // add 4 bytes for the header
//            data[0] = 36; // '$' character
//            data[1] = (byte)channel;
//            data[2] = (byte)((frame.Length & 0xFF00) >> 8);
//            data[3] = (byte)((frame.Length & 0x00FF));
//            System.Array.Copy(frame,0,data,4,frame.Length);
//            return _stream.BeginWrite(data, 0, data.Length, asyncCallback, state);
//        }

//        /// <summary>
//        /// Ends the send data.
//        /// </summary>
//        /// <param name="result">The result.</param>
//        public void EndSendData(IAsyncResult result)
//        {
//            try
//            {
//                _stream.EndWrite(result);
//            } catch (Exception e)
//            {
//                // Error, for example stream has already been Disposed
////                _logger.DebugException("Error during end send (can be ignored)", e);
//                result = null;
//            }
//        }

        /// <summary>
        /// Send data (Synchronous)
        /// </summary>
        /// <param name="channel">The channel.</param>
        /// <param name="frame">The frame.</param>
        public void SendData(int channel, byte[] frame)
        {
            if (frame == null)
                throw new ArgumentNullException("frame");
            if (frame.Length > 0xFFFF)
                throw new ArgumentException("frame too large", "frame");
//            Contract.EndContractBlock();

            if (!_transport.Connected)
            {
  //              _logger.Warn("Reconnect to a client, strange !!");
                Reconnect();
            }

            byte[] data = new byte[4 + frame.Length]; // add 4 bytes for the header
            data[0] = 36; // '$' character
            data[1] = (byte)channel;
            data[2] = (byte)((frame.Length & 0xFF00) >> 8);
            data[3] = (byte)((frame.Length & 0x00FF));
            System.Array.Copy(frame, 0, data, 4, frame.Length);
            _stream.Write(data, 0, data.Length);
        }


        #region IDisposable Membres

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                if (_stream != null)
                    _stream.Dispose();

            }
        }

        #endregion
    }
}
