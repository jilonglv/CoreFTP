namespace CoreFtp.Components.DirectoryListing
{
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Threading.Tasks;
    using Enum;
    using Infrastructure;
    using System.Linq;
    using Parser;

    internal class ListDirectoryProvider : DirectoryProviderBase
    {
        private readonly List<IListDirectoryParser> directoryParsers;

        public ListDirectoryProvider( FtpClient ftpClient, FtpClientConfiguration configuration )
        {
            this.ftpClient = ftpClient;
            this.configuration = configuration;

            directoryParsers = new List<IListDirectoryParser>
            {
                new UnixDirectoryParser(),
                new DosDirectoryParser(),
            };
        }

        private void EnsureLoggedIn()
        {
            if ( !ftpClient.IsConnected || !ftpClient.IsAuthenticated )
                throw new FtpException( "User must be logged in" );
        }

        public override async Task<IEnumerable<FtpNodeInformation>> ListAllAsync()
        {
            try
            {
#if NET40
                await Task.Factory.StartNew(() => ftpClient.dataSocketSemaphore.Wait());
#else
                await ftpClient.dataSocketSemaphore.WaitAsync();
#endif
                return await ListNodesAsync();
            }
            finally
            {
                ftpClient.dataSocketSemaphore.Release();
            }
        }

        public override async Task<IEnumerable<FtpNodeInformation>> ListFilesAsync()
        {
            try
            {
#if NET40
                await Task.Factory.StartNew(() => ftpClient.dataSocketSemaphore.Wait());
#else
                await ftpClient.dataSocketSemaphore.WaitAsync();
#endif
                return await ListNodesAsync( FtpNodeType.File );
            }
            finally
            {
                ftpClient.dataSocketSemaphore.Release();
            }
        }

        public override async Task<IEnumerable<FtpNodeInformation>> ListDirectoriesAsync()
        {
            try
            {
#if NET40
                await Task.Factory.StartNew(() => ftpClient.dataSocketSemaphore.Wait());
#else
                await ftpClient.dataSocketSemaphore.WaitAsync();
#endif
                return await ListNodesAsync( FtpNodeType.Directory );
            }
            finally
            {
                ftpClient.dataSocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Lists all nodes (files and directories) in the current working directory
        /// </summary>
        /// <param name="ftpNodeType"></param>
        /// <returns></returns>
        private async Task<IEnumerable<FtpNodeInformation>> ListNodesAsync( FtpNodeType? ftpNodeType = null )
        {
            EnsureLoggedIn();
            LoggerHelper.Debug( $"[ListDirectoryProvider] Listing {ftpNodeType}" );

            try
            {
                stream = await ftpClient.ConnectDataStreamAsync();

                var result = await ftpClient.ControlStream.SendCommandAsync( new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.LIST
                } );

                if ( ( result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen ) && ( result.FtpStatusCode != FtpStatusCode.OpeningData ) )
                    throw new FtpException( "Could not retrieve directory listing " + result.ResponseMessage );

                var directoryListing = RetrieveDirectoryListing();

                var nodes = ParseLines( directoryListing.ToList())
                    .Where( x => !ftpNodeType.HasValue || x.NodeType == ftpNodeType )
                    .ToList();

                return nodes.AsReadOnly();
            }
            finally
            {
                stream.Dispose();
            }
        }

        private IEnumerable<FtpNodeInformation> ParseLines(IEnumerable<string> lines )
        {
            if ( !lines.Any() )
                yield break;

            var parser = directoryParsers.FirstOrDefault(x => x.Test(lines.First()));
            //var parser = directoryParsers.FirstOrDefault(x => x.Test(lines[0]));

            if ( parser == null )
                yield break;

            foreach ( string line in lines )
            {
                var parsed = parser.Parse( line );

                if ( parsed != null )
                    yield return parsed;
            }
        }
    }
}