﻿using Microsoft.Net.Http.Server.Socket;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Formatting;
using System.Text.Utf8;
using System.Text.Http;
using System.Threading;

namespace System.Net.Http.Buffered
{
    // this is a small holder for a buffer and the pool that allocated it
    // it's used to pass buffers around that can be deallocated into the same pool they were taken from
    // if the pool was static, we would not need this type. But I am not sure if a static pool is good enough.
    public struct HttpServerBuffer
    {
        public byte[] _buffer;
        public int _count;
        ManagedBufferPool<byte> _pool;

        public HttpServerBuffer(byte[] buffer, int count, ManagedBufferPool<byte> pool = null)
        {
            _buffer = buffer;
            _count = count;
            _pool = pool;
        }

        public void Return()
        {
            if (_pool != null)
            {
                _pool.ReturnBuffer(ref _buffer);
                _count = 0;
            }
        }
    }

    public abstract class HttpServer
    {
        protected static Utf8String HttpNewline = new Utf8String(new byte[] { 13, 10 });

        protected volatile bool _isCancelled = false;
        TcpServer _listener;
        public Log Log { get; protected set; }

        const int RequestBufferSize = 2048;

        public ManagedBufferPool<byte> _buffers = new ManagedBufferPool<byte>(RequestBufferSize);

        protected HttpServer(Log log, ushort port, byte address1, byte address2, byte address3, byte address4)
        {
            Log = log;
            _listener = new TcpServer(port, address1, address2, address3, address4);
        }

        public void StartAsync()
        {
            Thread thread = new Thread(new ParameterizedThreadStart((parameter) => {
                var httpServer = parameter as HttpServer;
                httpServer.Start();
            }));
            thread.Start(this);
        }

        public void Stop()
        {
            _isCancelled = true;
            Log.LogVerbose("Server Terminated");
        }

        void Start()
        {
            try
            {
                while (!_isCancelled)
                {
                    TcpConnection socket = _listener.Accept();
                    ProcessRequest(socket);
                }
                _listener.Stop();
            }
            catch (Exception e)
            {
                Log.LogError(e.Message);
                Log.LogVerbose(e.ToString());
                Stop();
            }
        }

        protected virtual void ProcessRequest(TcpConnection socket)
        {
            Log.LogVerbose("Processing Request");
            
            var buffer = _buffers.RentBuffer(RequestBufferSize);
            var received = socket.Receive(buffer);

            if(received == 0)
            {
                socket.Close();
                return;
            }

            var receivedBytes = buffer.Slice(0, received);

            if (Log.IsVerbose)
            {
                var text = Encoding.UTF8.GetString(receivedBytes.CreateArray());
                Console.WriteLine(text);
            }

            var request = HttpRequest.Parse(receivedBytes);

            if (Log.IsVerbose)
            {
                Log.LogMessage(Log.Level.Verbose, "\tMethod:       {0}", request.RequestLine.Method);
                Log.LogMessage(Log.Level.Verbose, "\tRequest-URI:  {0}", request.RequestLine.RequestUri.ToString());
                Log.LogMessage(Log.Level.Verbose, "\tHTTP-Version: {0}", request.RequestLine.Version);

                Log.LogMessage(Log.Level.Verbose, "\tHttp Headers:");
                foreach (var httpHeader in request.Headers)
                {
                    Log.LogMessage(Log.Level.Verbose, "\t\tName: {0}, Value: {1}", httpHeader.Key, httpHeader.Value);
                }

                LogRestOfRequest(request.Body);
            }

            HttpServerBuffer responseBytes = CreateResponse(request);
                     
            _buffers.ReturnBuffer(ref buffer);

            // send response
            var segment = responseBytes;        
            
            socket.Send(segment._buffer, segment._count);

            socket.Close();

            responseBytes.Return();
            if (Log.IsVerbose)
            {
                Log.LogMessage(Log.Level.Verbose, "Request Processed", DateTime.UtcNow.Ticks);
            }
        }

        void LogRestOfRequest(Span<byte> buffer)
        {
            HttpRequestReader reader = new HttpRequestReader();
            reader.Buffer = buffer;
            while (true)
            {
                var header = reader.ReadHeader();
                if (header.Length == 0) break;
                Log.LogMessage(Log.Level.Verbose, "\tHeader: {0}", header.ToString());
            }
            var messageBody = reader.Buffer;
            Log.LogMessage(Log.Level.Verbose, "\tBody bytecount: {0}", messageBody.Length);
        }

        protected virtual HttpServerBuffer CreateResponseFor400(Span<byte> receivedBytes) // Bad Request
        {
            var formatter = new BufferFormatter(1024, FormattingData.InvariantUtf8);
            WriteCommonHeaders(formatter, "1.1", "400", "Bad Request", false);
            formatter.Append(HttpNewline);
            return new HttpServerBuffer(formatter.Buffer, formatter.CommitedByteCount, _buffers);
        }

        protected virtual HttpServerBuffer CreateResponseFor404(HttpRequestLine requestLine) // Not Found
        {
            Log.LogMessage(Log.Level.Warning, "Request {0}, Response: 404 Not Found", requestLine);

            var formatter = new BufferFormatter(1024, FormattingData.InvariantUtf8);
            WriteCommonHeaders(formatter, "1.1", "404", "Not Found", false);
            formatter.Append(HttpNewline);
            return new HttpServerBuffer(formatter.Buffer, formatter.CommitedByteCount, _buffers);
        }

        protected static void WriteCommonHeaders(
            BufferFormatter formatter,
            string version,
            string statuCode,
            string reasonCode,
            bool keepAlive)
        {
            var currentTime = DateTime.UtcNow;
            formatter.WriteHttpStatusLine(
                new Utf8String(version), 
                new Utf8String(statuCode), 
                new Utf8String(reasonCode));
            formatter.WriteHttpHeader(new Utf8String("Date"), new Utf8String(currentTime.ToString("R")));
            formatter.WriteHttpHeader(new Utf8String("Server"), new Utf8String(".NET Core Sample Serve"));
            formatter.WriteHttpHeader(new Utf8String("Last-Modified"), new Utf8String(currentTime.ToString("R")));
            formatter.WriteHttpHeader(new Utf8String("Content-Type"), new Utf8String("text/html; charset=UTF-8"));
            
            if (!keepAlive)
            {
                formatter.WriteHttpHeader(new Utf8String("Connection"), new Utf8String("close"));
            }
        }

        protected abstract HttpServerBuffer CreateResponse(HttpRequest request);
    }
}
