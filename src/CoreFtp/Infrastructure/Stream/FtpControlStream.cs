namespace CoreFtp.Infrastructure.Stream
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Components.DnsResolution;
    using Enum;
    using Extensions;

    public class FtpControlStream : Stream
    {
        protected readonly FtpClientConfiguration Configuration;
        protected readonly IDnsResolver dnsResolver;
        protected Socket Socket;
        protected Stream BaseStream;

        protected Stream NetworkStream => SslStream ?? BaseStream;
        protected SslStream SslStream { get; set; }
        protected int SocketPollInterval { get; } = 15000;
        protected DateTime LastActivity = DateTime.Now;
        public Encoding Encoding { get; set; } = Encoding.ASCII;
        public System.Net.EndPoint LocalEndPoint
        {
            get
            {
                return Socket?.LocalEndPoint;
            }
        }
        protected readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        protected readonly SemaphoreSlim receiveSemaphore = new SemaphoreSlim(1, 1);

        internal bool IsDataConnection { get; set; }

        internal void SetTimeouts(int milliseconds)
        {
            BaseStream.ReadTimeout = milliseconds;
            BaseStream.WriteTimeout = milliseconds;
        }

        internal void ResetTimeouts()
        {
            BaseStream.ReadTimeout = Configuration.TimeoutSeconds * 1000;
            BaseStream.WriteTimeout = Configuration.TimeoutSeconds * 1000;
        }

        public FtpControlStream(FtpClientConfiguration configuration, IDnsResolver dnsResolver)
        {
            LoggerHelper.Debug("Constructing new FtpSocketStream");
            Configuration = configuration;
            this.dnsResolver = dnsResolver;
        }

        public override bool CanRead => NetworkStream != null && NetworkStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => NetworkStream != null && NetworkStream.CanWrite;
        public override long Length => NetworkStream?.Length ?? 0;

        public override long Position
        {
            get { return NetworkStream?.Position ?? 0; }
            set { throw new InvalidOperationException(); }
        }

        public bool IsEncrypted => SslStream != null && SslStream.IsEncrypted;

        public bool IsConnected
        {
            get
            {
                try
                {
                    if (Socket == null || !Socket.Connected || (!CanRead || !CanWrite))
                    {
                        Disconnect();
                        return false;
                    }

                    if (LastActivity.HasIntervalExpired(DateTime.Now, SocketPollInterval))
                    {
                        LoggerHelper.Debug("Polling connection");
                        if (Socket.Poll(500000, SelectMode.SelectRead) && Socket.Available == 0)
                        {
                            Disconnect();
                            return false;
                        }
                    }
                }
                catch (SocketException socketException)
                {
                    Disconnect();
                    LoggerHelper.Error($"FtpSocketStream.IsConnected: Caught and discarded SocketException while testing for connectivity: {socketException}");
                    return false;
                }
                catch (IOException ioException)
                {
                    Disconnect();
                    LoggerHelper.Error($"FtpSocketStream.IsConnected: Caught and discarded IOException while testing for connectivity: {ioException}");
                    return false;
                }

                return true;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return NetworkStream?.Read(buffer, offset, count) ?? 0;
        }


        public override void Write(byte[] buffer, int offset, int count)
        {
            NetworkStream?.Write(buffer, offset, count);
        }

        public
#if !NET40
            override
#endif
             async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await NetworkStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new InvalidOperationException();
        }


        public override void Flush()
        {
            if (!IsConnected)
                throw new InvalidOperationException("The FtpSocketStream object is not connected.");

            NetworkStream?.Flush();
        }

        public override void SetLength(long value)
        {
            throw new InvalidOperationException();
        }

        public async Task ConnectAsync(CancellationToken token = default(CancellationToken))
        {
            await ConnectStreamAsync(token);

            if (!Configuration.ShouldEncrypt)
                return;

            if (!IsConnected || IsEncrypted)
                return;

            if (Configuration.EncryptionType == FtpEncryption.Implicit)
                await EncryptImplicitly(token);

            if (Configuration.EncryptionType == FtpEncryption.Explicit)
                await EncryptExplicitly(token);
        }


        protected async Task WriteLineAsync(string buf)
        {
            var data = Encoding.GetBytes($"{buf}\r\n");
            await WriteAsync(data, 0, data.Length, CancellationToken.None);
        }

        protected string ReadLine(Encoding encoding, CancellationToken token)
        {
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));

            var data = new List<byte>();
            var buffer = new byte[1];
            string line = null;

            token.ThrowIfCancellationRequested();

            while (Read(buffer, 0, buffer.Length) > 0)
            {
                token.ThrowIfCancellationRequested();
                data.Add(buffer[0]);
                if ((char)buffer[0] != '\n')
                    continue;
                line = encoding.GetString(data.ToArray()).Trim('\r', '\n');
                break;
            }

            return line;
        }

        private IEnumerable<string> ReadLines(CancellationToken token)
        {
            string line;
            while ((line = ReadLine(Encoding, token)) != null)
            {
                yield return line;
            }
        }


        public bool SocketDataAvailable()
        {
            return (Socket?.Available ?? 0) > 0;
        }

        public async Task<FtpResponse> SendCommandAsync(FtpCommand command, CancellationToken token = default(CancellationToken))
        {
            return await SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = command
            }, token);
        }

        public async Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default(CancellationToken))
        {
            string commandString = envelope.GetCommandString();
            return await SendCommandAsync(commandString, token);
        }

        public async Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default(CancellationToken))
        {
#if NET40
            await Task.Factory.StartNew(() => semaphore.Wait(token));
#else
            await semaphore.WaitAsync(token);
#endif

            try
            {
                if (SocketDataAvailable())
                {
                    var staleDataResult = await GetResponseAsync(token);
                    LoggerHelper.Warn($"Stale data on socket {staleDataResult.ResponseMessage}");
                }

                string commandToPrint = command.StartsWith(FtpCommand.PASS.ToString())
                    ? "PASS *****"
                    : command;

                LoggerHelper.Debug($"[FtpClient] Sending command: {commandToPrint}");
                await WriteLineAsync(command);

                var response = await GetResponseAsync(token);
                return response;
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<FtpResponse> GetResponseAsync(CancellationToken token = default(CancellationToken))
        {
            LoggerHelper.Trace("Getting Response");

            if (Encoding == null)
                throw new ArgumentNullException(nameof(Encoding));
#if NET40
            await Task.Factory.StartNew(() => receiveSemaphore.Wait(token));
#else
            await receiveSemaphore.WaitAsync(token);
#endif

            try
            {
                token.ThrowIfCancellationRequested();

                var response = new FtpResponse();
                var data = new List<string>();

                foreach (string line in ReadLines(token))
                {
                    token.ThrowIfCancellationRequested();
                    LoggerHelper.Debug(line);
                    data.Add(line);

                    Match match;

                    if (!(match = Regex.Match(line, "^(?<statusCode>[0-9]{3}) (?<message>.*)$")).Success)
                        continue;
                    LoggerHelper.Trace("Finished receiving message");
                    response.FtpStatusCode = match.Groups["statusCode"].Value.ToStatusCode();
                    response.ResponseMessage = match.Groups["message"].Value;
                    break;
                }
                response.Data = data.ToArray();
                return response;
            }
            finally
            {
                receiveSemaphore.Release();
            }
        }

        public async Task<Stream> OpenDataStreamAsync(string host, int port, CancellationToken token)
        {
            LoggerHelper.Debug("[FtpSocketStream] Opening datastream");
            var socketStream = new FtpControlStream(Configuration, dnsResolver) { IsDataConnection = true };
            await socketStream.ConnectStreamAsync(host, port, token);

            if (IsEncrypted)
            {
                await socketStream.ActivateEncryptionAsync();
            }
            return socketStream;
        }

        protected async Task ConnectStreamAsync(CancellationToken token)
        {
            await ConnectStreamAsync(Configuration.Host, Configuration.Port, token);
        }
        protected async Task ConnectStreamAsync(string host, int port, CancellationToken token)
        {
            try
            {
#if NET40
                await Task.Factory.StartNew(() =>semaphore.Wait(token));
#else
                await semaphore.WaitAsync(token);
#endif
                LoggerHelper.Debug($"Connecting stream on {host}:{port}");
                Socket = await ConnectSocketAsync(host, port, token);

                BaseStream = new NetworkStream(Socket);
                LastActivity = DateTime.Now;

                if (IsDataConnection)
                {
                    if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Explicit)
                    {
                        await ActivateEncryptionAsync();
                    }

                    return;
                }
                else
                {
                    if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Implicit)
                    {
                        await ActivateEncryptionAsync();
                    }
                }

                LoggerHelper.Debug("Waiting for welcome message");

                while (true)
                {
                    if (SocketDataAvailable())
                    {
                        await GetResponseAsync(token);
                        return;
                    }
#if NET40
                    await Task.Factory.StartNew(() => Thread.Sleep(10), token);
#else
                    await Task.Delay(10, token);
#endif
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        protected async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken token)
        {
            try
            {
                LoggerHelper.Debug("Connecting");
                var ipEndpoint = await dnsResolver.ResolveAsync(host, port, Configuration.IpVersion, token);

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ReceiveTimeout = Configuration.TimeoutSeconds * 1000
                };
                //                socket.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true );
                //                socket.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true );
                socket.Connect(ipEndpoint);
                socket.LingerState = new LingerOption(true, 0);
                return socket;
            }
            catch (Exception exception)
            {
                LoggerHelper.Error($"Could not to connect socket {host}:{port} - {exception.Message} :{exception.ToString()}");
                throw;
            }
        }
        public async Task<Stream> AcceptDataStreamAsync(System.Net.Sockets.TcpListener listener)
        {
            var socketStream = new FtpControlStream(Configuration, dnsResolver) { IsDataConnection = true };
            return await socketStream.AcceptStreamAsync(listener);
        }
        protected async Task<Stream> AcceptStreamAsync(System.Net.Sockets.TcpListener listener)
        {
            try
            {
                LoggerHelper.Debug($"Accept stream on {listener.LocalEndpoint.ToString()}");
                Socket = await listener.AcceptSocketAsync();

                BaseStream = new NetworkStream(Socket);
                LastActivity = DateTime.Now;

                if (IsDataConnection)
                {
                    if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Explicit)
                    {
                        await ActivateEncryptionServerAsync();
                    }
                }
                else
                {
                    if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Implicit)
                    {
                        await ActivateEncryptionServerAsync();
                    }
                }
                return this;
            }
            catch (Exception exception)
            {
                LoggerHelper.Error($"Could accept connect {listener.LocalEndpoint.ToString()} :{exception.ToString()}");
                throw;
            }
        }

        protected async Task EncryptImplicitly(CancellationToken token)
        {
            LoggerHelper.Debug("Encrypting implicitly");
            await ActivateEncryptionAsync();

            var response = await GetResponseAsync(token);
            if (!response.IsSuccess)
            {
                throw new IOException($"Could not securely connect to host {Configuration.Host}:{Configuration.Port}");
            }
        }

        protected async Task EncryptExplicitly(CancellationToken token)
        {
            LoggerHelper.Debug("Encrypting explicitly");
            var response = await SendCommandAsync("AUTH TLS", token);

            if (!response.IsSuccess)
                throw new InvalidOperationException();

            await ActivateEncryptionAsync();
        }

        protected async Task ActivateEncryptionAsync()
        {
            if (!IsConnected)
                throw new InvalidOperationException("The FtpSocketStream object is not connected.");

            if (BaseStream == null)
                throw new InvalidOperationException("The base network stream is null.");

            if (IsEncrypted)
                return;

            try
            {
                SslStream = new SslStream(BaseStream, true, (sender, certificate, chain, sslPolicyErrors) => OnValidateCertificate(certificate, chain, sslPolicyErrors));
#if NET40
                await SslStream.AuthenticateAsClientAsync(Configuration.Host);
#else
                await SslStream.AuthenticateAsClientAsync(Configuration.Host, Configuration.ClientCertificates, Configuration.SslProtocols, true);
#endif
            }
            catch (AuthenticationException e)
            {
                LoggerHelper.Error($"Could not activate encryption for the connection: {e.Message}");
                throw;
            }
        }
        protected async Task ActivateEncryptionServerAsync()
        {
            if (!IsConnected)
                throw new InvalidOperationException("The FtpSocketStream object is not connected.");

            if (BaseStream == null)
                throw new InvalidOperationException("The base network stream is null.");

            if (IsEncrypted)
                return;

            try
            {
                SslStream = new SslStream(BaseStream, true, (sender, certificate, chain, sslPolicyErrors) => OnValidateCertificate(certificate, chain, sslPolicyErrors));
#if NET40
                await SslStream.AuthenticateAsServerAsync(Configuration.ClientCertificates[0]);
#else
                await SslStream.AuthenticateAsServerAsync(Configuration.ClientCertificates[0], true, Configuration.SslProtocols, true);
#endif
            }
            catch (AuthenticationException e)
            {
                LoggerHelper.Error($"Could not activate encryption for the connection: {e.Message}");
                throw;
            }
        }


        private bool OnValidateCertificate(X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (Configuration.IgnoreCertificateErrors)
                return true;

            return errors == SslPolicyErrors.None;
        }

        public void Disconnect()
        {
            LoggerHelper.Trace("Disconnecting");
            try
            {
                BaseStream?.Dispose();
                SslStream?.Dispose();
                Socket?.Shutdown(SocketShutdown.Both);
            }
            catch (Exception exception)
            {
                LoggerHelper.Error($"Exception caught: {exception}");
            }
            finally
            {
                Socket = null;
                BaseStream = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            LoggerHelper.Trace(IsDataConnection ? "Disposing of data connection" : "Disposing of control connection");

            if (disposing)
            {
                Disconnect();
            }
            base.Dispose(disposing);
        }
    }
}
