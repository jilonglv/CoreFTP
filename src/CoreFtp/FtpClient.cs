namespace CoreFtp
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Components.DirectoryListing;
    using Components.DnsResolution;
    using Enum;
    using Infrastructure;
    using Infrastructure.Extensions;
    using Infrastructure.Stream;

    public class FtpClient : IFtpClient
    {
        private IDirectoryProvider directoryProvider;
        private Stream dataStream;
        internal readonly SemaphoreSlim dataSocketSemaphore = new SemaphoreSlim(1, 1);
        public FtpClientConfiguration Configuration { get; private set; }

        internal IEnumerable<string> Features { get; private set; }
        internal FtpControlStream ControlStream { get; private set; }
        public bool IsConnected => ControlStream != null && ControlStream.IsConnected;
        public bool IsEncrypted => ControlStream != null && ControlStream.IsEncrypted;
        public bool IsAuthenticated { get; private set; }
        public string WorkingDirectory { get; private set; } = "/";
        public bool UsePassive { get; set; }

        public FtpClient() { }

        public FtpClient(FtpClientConfiguration configuration)
        {
            Configure(configuration);
        }

        public void Configure(FtpClientConfiguration configuration)
        {
            Configuration = configuration;

            if (configuration.Host == null)
                throw new ArgumentNullException(nameof(configuration.Host));

            if (Uri.IsWellFormedUriString(configuration.Host, UriKind.Absolute))
            {
                configuration.Host = new Uri(configuration.Host).Host;
            }


            ControlStream = new FtpControlStream(Configuration, new DnsResolver());
            Configuration.BaseDirectory = $"/{Configuration.BaseDirectory.TrimStart('/')}";
        }
        System.Net.IPAddress LocalEndPoint;
        /// <summary>
        ///     Attempts to log the user in to the FTP Server
        /// </summary>
        /// <returns></returns>
        public async Task LoginAsync()
        {
            if (IsConnected)
                await LogOutAsync();

            string username = Configuration.Username.IsNullOrWhiteSpace()
                ? Constants.ANONYMOUS_USER
                : Configuration.Username;

            await ControlStream.ConnectAsync();

            if (ControlStream.LocalEndPoint is System.Net.IPEndPoint)
            {
                LocalEndPoint = ((System.Net.IPEndPoint)ControlStream.LocalEndPoint).Address;
            }

            var usrResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.USER,
                Data = username
            });

            await BailIfResponseNotAsync(usrResponse, FtpStatusCode.SendUserCommand, FtpStatusCode.SendPasswordCommand, FtpStatusCode.LoggedInProceed);

            var passResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.PASS,
                Data = username != Constants.ANONYMOUS_USER ? Configuration.Password : string.Empty
            });

            await BailIfResponseNotAsync(passResponse, FtpStatusCode.LoggedInProceed);
            IsAuthenticated = true;
            UsePassive = Configuration.UsePassive;
            if (ControlStream.IsEncrypted)
            {
                await ControlStream.SendCommandAsync(new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.PBSZ,
                    Data = "0"
                });

                await ControlStream.SendCommandAsync(new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.PROT,
                    Data = "P"
                });
            }

            Features = await DetermineFeaturesAsync();
            directoryProvider = DetermineDirectoryProvider();
            await EnableUTF8IfPossible();
            await SetTransferMode(Configuration.Mode, Configuration.ModeSecondType);

            if (Configuration.BaseDirectory != "/")
            {
                await CreateDirectoryAsync(Configuration.BaseDirectory);
            }

            await ChangeWorkingDirectoryAsync(Configuration.BaseDirectory);
        }

        /// <summary>
        ///     Attemps to log the user out asynchronously, sends the QUIT command and terminates the command socket.
        /// </summary>
        public async Task LogOutAsync()
        {
            await IgnoreStaleData();
            if (!IsConnected)
                return;

            LoggerHelper.Trace("[FtpClient] Logging out");
            await ControlStream.SendCommandAsync(FtpCommand.QUIT);
            ControlStream.Disconnect();
            if (LocalEndPoint != null)
                LocalEndPoint = null;
            IsAuthenticated = false;
        }

        /// <summary>
        /// Changes the working directory to the given value for the current session
        /// </summary>
        /// <param name="directory"></param>
        /// <returns></returns>
        public async Task ChangeWorkingDirectoryAsync(string directory)
        {
            LoggerHelper.Trace($"[FtpClient] changing directory to {directory}");
            if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
                throw new ArgumentOutOfRangeException(nameof(directory), "Directory supplied was incorrect");

            EnsureLoggedIn();

            var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.CWD,
                Data = directory
            });

            if (!response.IsSuccess)
                throw new FtpException(response.ResponseMessage);

            var pwdResponse = await ControlStream.SendCommandAsync(FtpCommand.PWD);

            if (!response.IsSuccess)
                throw new FtpException(response.ResponseMessage);

            WorkingDirectory = pwdResponse.ResponseMessage.Split('"')[1];
        }

        /// <summary>
        /// Creates a directory on the FTP Server
        /// </summary>
        /// <param name="directory"></param>
        /// <returns></returns>
        public async Task CreateDirectoryAsync(string directory)
        {
            if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
                throw new ArgumentOutOfRangeException(nameof(directory), "Directory supplied was not valid");

            LoggerHelper.Debug($"[FtpClient] Creating directory {directory}");

            EnsureLoggedIn();

            await CreateDirectoryStructureRecursively(directory.Split('/'), directory.StartsWith("/"));
        }

        /// <summary>
        /// Renames a file on the FTP server
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <returns></returns>
        public async Task RenameAsync(string from, string to)
        {
            EnsureLoggedIn();
            LoggerHelper.Debug($"[FtpClient] Renaming from {from}, to {to}");
            var renameFromResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.RNFR,
                Data = from
            });

            if (renameFromResponse.FtpStatusCode != FtpStatusCode.FileCommandPending)
                throw new FtpException(renameFromResponse.ResponseMessage);

            var renameToResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.RNTO,
                Data = to
            });

            if (renameToResponse.FtpStatusCode != FtpStatusCode.FileActionOK && renameToResponse.FtpStatusCode != FtpStatusCode.ClosingData)
                throw new FtpException(renameFromResponse.ResponseMessage);
        }

        /// <summary>
        /// Deletes the given directory from the FTP server
        /// </summary>
        /// <param name="directory"></param>
        /// <returns></returns>
        public async Task DeleteDirectoryAsync(string directory)
        {
            if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
                throw new ArgumentOutOfRangeException(nameof(directory), "Directory supplied was not valid");

            if (directory == "/")
                return;

            LoggerHelper.Debug($"[FtpClient] Deleting directory {directory}");

            EnsureLoggedIn();

            var rmdResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.RMD,
                Data = directory
            });

            switch (rmdResponse.FtpStatusCode)
            {
                case FtpStatusCode.CommandOK:
                case FtpStatusCode.FileActionOK:
                    return;

                case FtpStatusCode.ActionNotTakenFileUnavailable:
                    await DeleteNonEmptyDirectory(directory);
                    return;

                default:
                    throw new FtpException(rmdResponse.ResponseMessage);
            }
        }

        /// <summary>
        /// Deletes the given directory from the FTP server
        /// </summary>
        /// <param name="directory"></param>
        /// <returns></returns>
        private async Task DeleteNonEmptyDirectory(string directory)
        {
            await ChangeWorkingDirectoryAsync(directory);

            var allNodes = await ListAllAsync();

            foreach (var file in allNodes.Where(x => x.NodeType == FtpNodeType.File))
            {
                await DeleteFileAsync(file.Name);
            }

            foreach (var dir in allNodes.Where(x => x.NodeType == FtpNodeType.Directory))
            {
                await DeleteDirectoryAsync(dir.Name);
            }

            await ChangeWorkingDirectoryAsync("..");
            await DeleteDirectoryAsync(directory);
        }

        /// <summary>
        /// Informs the FTP server of the client being used
        /// </summary>
        /// <param name="clientName"></param>
        /// <returns></returns>
        public async Task<FtpResponse> SetClientName(string clientName)
        {
            EnsureLoggedIn();
            LoggerHelper.Debug($"[FtpClient] Setting client name to {clientName}");

            return await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.CLNT,
                Data = clientName
            });
        }

        /// <summary>
        /// Provides a stream which contains the data of the given filename on the FTP server
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public async Task<Stream> OpenFileReadStreamAsync(string fileName)
        {
            LoggerHelper.Debug($"[FtpClient] Opening file read stream for {fileName}");

            return new FtpDataStream(await OpenFileStreamAsync(fileName, FtpCommand.RETR), this);
        }

        /// <summary>
        /// Provides a stream which can be written to
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public async Task<Stream> OpenFileWriteStreamAsync(string fileName)
        {
            string filePath = WorkingDirectory.CombineAsUriWith(fileName);
            LoggerHelper.Debug($"[FtpClient] Opening file read stream for {filePath}");
            var segments = filePath.Split('/')
                                   .Where(x => !x.IsNullOrWhiteSpace())
                                   .ToList();
            await CreateDirectoryStructureRecursively(segments.Take(segments.Count - 1).ToArray(), filePath.StartsWith("/"));
            return new FtpDataStream(await OpenFileStreamAsync(filePath, FtpCommand.STOR), this);
        }

        /// <summary>
        /// Closes the write stream and associated socket (if open), 
        /// </summary>
        /// <param name="ctsToken"></param>
        /// <returns></returns>
        public async Task CloseFileDataStreamAsync(CancellationToken ctsToken = default(CancellationToken))
        {
            LoggerHelper.Trace("[FtpClient] Closing write file stream");
            dataStream.Dispose();

            if (ControlStream != null)
                await ControlStream.GetResponseAsync(ctsToken);
        }

        /// <summary>
        /// Lists all files in the current working directory
        /// </summary>
        /// <returns></returns>
        public async Task<IEnumerable<FtpNodeInformation>> ListAllAsync()
        {
            try
            {
                EnsureLoggedIn();
                LoggerHelper.Debug($"[FtpClient] Listing files in {WorkingDirectory}");
                return await directoryProvider.ListAllAsync();
            }
            finally
            {
                await ControlStream.GetResponseAsync();
            }
        }

        /// <summary>
        /// Lists all files in the current working directory
        /// </summary>
        /// <returns></returns>
        public async Task<IEnumerable<FtpNodeInformation>> ListFilesAsync()
        {
            try
            {
                EnsureLoggedIn();
                LoggerHelper.Debug($"[FtpClient] Listing files in {WorkingDirectory}");
                return await directoryProvider.ListFilesAsync();
            }
            finally
            {
                await ControlStream.GetResponseAsync();
            }
        }

        /// <summary>
        /// Lists all directories in the current working directory
        /// </summary>
        /// <returns></returns>
        public async Task<IEnumerable<FtpNodeInformation>> ListDirectoriesAsync()
        {
            try
            {
                EnsureLoggedIn();
                LoggerHelper.Debug($"[FtpClient] Listing directories in {WorkingDirectory}");
                return await directoryProvider.ListDirectoriesAsync();
            }
            finally
            {
                await ControlStream.GetResponseAsync();
            }
        }


        /// <summary>
        /// Lists all directories in the current working directory
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public async Task DeleteFileAsync(string fileName)
        {
            EnsureLoggedIn();
            LoggerHelper.Debug($"[FtpClient] Deleting file {fileName}");
            var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.DELE,
                Data = fileName
            });

            if (!response.IsSuccess)
                throw new FtpException(response.ResponseMessage);
        }

        /// <summary>
        /// Determines the file size of the given file
        /// </summary>
        /// <param name="transferMode"></param>
        /// <param name="secondType"></param>
        /// <returns></returns>
        public async Task SetTransferMode(FtpTransferMode transferMode, char secondType = '\0')
        {
            EnsureLoggedIn();
            LoggerHelper.Trace($"[FtpClient] Setting transfer mode {transferMode}, {secondType}");
            var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.TYPE,
                Data = secondType != '\0'
                    ? $"{(char)transferMode} {secondType}"
                    : $"{(char)transferMode}"
            });

            if (!response.IsSuccess)
                throw new FtpException(response.ResponseMessage);
        }

        /// <summary>
        /// Determines the file size of the given file
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public async Task<long> GetFileSizeAsync(string fileName)
        {
            EnsureLoggedIn();
            LoggerHelper.Debug($"[FtpClient] Getting file size for {fileName}");
            var sizeResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.SIZE,
                Data = fileName
            });

            if (sizeResponse.FtpStatusCode != FtpStatusCode.FileStatus)
                throw new FtpException(sizeResponse.ResponseMessage);

            long fileSize = long.Parse(sizeResponse.ResponseMessage);
            return fileSize;
        }

        /// <summary>
        /// Determines the type of directory listing the FTP server will return, and set the appropriate parser
        /// </summary>
        /// <returns></returns>
        private IDirectoryProvider DetermineDirectoryProvider()
        {
            LoggerHelper.Trace("[FtpClient] Determining directory provider");
            if (this.UsesMlsd())
                return new MlsdDirectoryProvider(this, Configuration);

            return new ListDirectoryProvider(this, Configuration);
        }

        private async Task<IEnumerable<string>> DetermineFeaturesAsync()
        {
            EnsureLoggedIn();
            LoggerHelper.Trace("[FtpClient] Determining features");
            var response = await ControlStream.SendCommandAsync(FtpCommand.FEAT);

            if (response.FtpStatusCode == FtpStatusCode.CommandSyntaxError || response.FtpStatusCode == FtpStatusCode.CommandNotImplemented)
                return Enumerable.Empty<string>();

            var features = response.Data.Where(x => !x.StartsWith(((int)FtpStatusCode.SystemHelpReply).ToString()) && !x.IsNullOrWhiteSpace())
                                   .Select(x => x.Replace(Constants.CARRIAGE_RETURN, string.Empty).Trim())
                                   .ToList();

            return features;
        }

        /// <summary>
        /// Creates a directory structure recursively given a path
        /// </summary>
        /// <param name="directories"></param>
        /// <param name="isRootedPath"></param>
        /// <returns></returns>
        private async Task CreateDirectoryStructureRecursively(IEnumerable<string> directories, bool isRootedPath)
        {
            LoggerHelper.Debug($"[FtpClient] Creating directory structure recursively {string.Join("/", directories)}");
            string originalPath = WorkingDirectory;

            if (isRootedPath && directories.Any())
                await ChangeWorkingDirectoryAsync("/");

            if (!directories.Any())
                return;

            if (directories.Count() == 1)
            {
                await ControlStream.SendCommandAsync(new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.MKD,
                    Data = directories.First()
                });

                await ChangeWorkingDirectoryAsync(originalPath);
                return;
            }

            foreach (string directory in directories)
            {
                if (directory.IsNullOrWhiteSpace())
                    continue;

                var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.CWD,
                    Data = directory
                });

                if (response.FtpStatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                    continue;

                await ControlStream.SendCommandAsync(new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.MKD,
                    Data = directory
                });
                await ControlStream.SendCommandAsync(new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.CWD,
                    Data = directory
                });
            }

            await ChangeWorkingDirectoryAsync(originalPath);
        }


        /// <summary>
        /// Opens a filestream to the given filename
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="command"></param>
        /// <returns></returns>
        private async Task<Stream> OpenFileStreamAsync(string fileName, FtpCommand command)
        {
            EnsureLoggedIn();
            LoggerHelper.Debug($"[FtpClient] Opening filestream for {fileName}, {command}");
            dataStream = await ConnectDataStreamAsync();

            var retrResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = command,
                Data = fileName
            });

            if ((retrResponse.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) &&
                 (retrResponse.FtpStatusCode != FtpStatusCode.OpeningData) &&
                 (retrResponse.FtpStatusCode != FtpStatusCode.ClosingData))
                throw new FtpException(retrResponse.ResponseMessage);

            return dataStream;
        }

        /// <summary>
        /// Checks if the command socket is open and that an authenticated session is active
        /// </summary>
        private void EnsureLoggedIn()
        {
            if (!IsConnected || !IsAuthenticated)
                throw new FtpException("User must be logged in");
        }
        public bool IsLogined { get { return IsConnected && IsAuthenticated; } }

        /// <summary>
        /// Produces a data socket using Passive (PASV) or Extended Passive (EPSV) mode
        /// </summary>
        /// <returns></returns>
        internal async Task<Stream> ConnectDataStreamAsync()
        {
            if (UsePassive)
            {
                LoggerHelper.Trace("[FtpClient] Connecting to a data socket");

                var epsvResult = await ControlStream.SendCommandAsync(FtpCommand.EPSV);

                int? passivePortNumber;
                if (epsvResult.FtpStatusCode == FtpStatusCode.EnteringExtendedPassive)
                {
                    passivePortNumber = epsvResult.ResponseMessage.ExtractEpsvPortNumber();
                }
                else
                {
                    // EPSV failed - try regular PASV
                    var pasvResult = await ControlStream.SendCommandAsync(FtpCommand.PASV);
                    if (pasvResult.FtpStatusCode != FtpStatusCode.EnteringPassive)
                        throw new FtpException(pasvResult.ResponseMessage);

                    passivePortNumber = pasvResult.ResponseMessage.ExtractPasvPortNumber();
                }

                if (!passivePortNumber.HasValue)
                    throw new FtpException("Could not determine EPSV/PASV data port");

                return await ControlStream.OpenDataStreamAsync(Configuration.Host, passivePortNumber.Value, CancellationToken.None);
            }
            else
            {
                return await PortDataStreamAsync();
            }
        }
        /// <summary>
        /// 暂不支持EPRT
        /// 1:ipV4;2 ipV6
        /// EPRT |1|132.235.1.2|6275|
        /// EPRT |2|1080::8:800:200C:417A|5282|
        /// 
        /// PORT 192,168,191,11,206,97
        /// </summary>
        internal async Task<Stream> PortDataStreamAsync()
        {
            // 主动模式时，客户端必须告知服务器接收数据的端口号，PORT 命令格式为：PORT address
            //PORT 192,168,191,11,206,97
            // address参数的格式为i1、i2、i3、i4、p1、p2,其中i1、i2、i3、i4表示IP地址
            // 下面通过.字符串来组合这四个参数得到IP地址
            System.Net.Sockets.TcpListener dataListener;
            string portListener;
            string sendString = string.Empty;
            var localip = (System.Net.IPAddress)LocalEndPoint;
            Random random = new Random();
            int random1, random2;
            int port;
            string ip = string.Empty;
            while (true)
            {
                // 随机生成一个端口进行数据传输
                random1 = random.Next(5, 200);
                random2 = random.Next(0, 200);
                // 生成的端口号控制>1024的随机端口
                // 下面这个运算算法只是为了得到一个大于1024的端口值
                port = random1 << 8 | random2;
                try
                {
                    dataListener = new System.Net.Sockets.TcpListener(localip, port);
                    dataListener.Start();
                }
                catch
                {
                    continue;
                }
                ip = localip.ToString().Replace('.', ',');
                portListener = string.Format("{0},{1},{2}", ip, random1, random2);
                // 必须把端口号IP地址告诉客户端，客户端接收到响应命令后，
                // 再通过新的端口连接服务器的端口P，然后进行文件数据传输
                break;
            }
            var pasvResult = await ControlStream.SendCommandAsync(new FtpCommandEnvelope()
            {
                FtpCommand = FtpCommand.PORT,
                Data = portListener
            });
            return await ControlStream.AcceptDataStreamAsync(dataListener);
        }
        /// <summary>
        /// Throws an exception if the server response is not one of the given acceptable codes
        /// </summary>
        /// <param name="response"></param>
        /// <param name="codes"></param>
        /// <returns></returns>
        private async Task BailIfResponseNotAsync(FtpResponse response, params FtpStatusCode[] codes)
        {
            if (codes.Any(x => x == response.FtpStatusCode))
                return;

            LoggerHelper.Debug($"Bailing due to response codes being {response.FtpStatusCode}, which is not one of: [{string.Join(",", codes)}]");

            await LogOutAsync();
            throw new FtpException(response.ResponseMessage);
        }

        /// <summary>
        /// Determine if the FTP server supports UTF8 encoding, and set it to the default if possible
        /// </summary>
        /// <returns></returns>
        private async Task EnableUTF8IfPossible()
        {
            if (Equals(ControlStream.Encoding, Encoding.ASCII) && Features.Any(x => x == Constants.UTF8))
            {
                ControlStream.Encoding = Encoding.UTF8;
            }

            if (Equals(ControlStream.Encoding, Encoding.UTF8))
            {
                // If the server supports UTF8 it should already be enabled and this
                // command should not matter however there are conflicting drafts
                // about this so we'll just execute it to be safe. 
                await ControlStream.SendCommandAsync("OPTS UTF8 ON");
            }
        }

        public async Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default(CancellationToken))
        {
            return await ControlStream.SendCommandAsync(envelope, token);
        }

        public async Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default(CancellationToken))
        {
            return await ControlStream.SendCommandAsync(command, token);
        }

        /// <summary>
        /// Ignore any stale data we may have waiting on the stream
        /// </summary>
        /// <returns></returns>
        private async Task IgnoreStaleData()
        {
            if (IsConnected && ControlStream.SocketDataAvailable())
            {
                var staleData = await ControlStream.GetResponseAsync();
                LoggerHelper.Warn($"Stale data detected: {staleData.ResponseMessage}");
            }
        }

        public void Dispose()
        {
            LoggerHelper.Debug("Disposing of FtpClient");
            Task.WaitAny(LogOutAsync());
            ControlStream?.Dispose();
            dataSocketSemaphore?.Dispose();
        }
    }
}
