namespace CoreFtp.Components.DirectoryListing
{
    using System.Collections.ObjectModel;
    using System.Threading.Tasks;
    using Enum;
    using Infrastructure;
    using System.Linq;
    using Infrastructure.Extensions;
    using System.Collections.Generic;

    internal class MlsdDirectoryProvider : DirectoryProviderBase
    {
        public MlsdDirectoryProvider( FtpClient ftpClient, FtpClientConfiguration configuration )
        {
            this.ftpClient = ftpClient;
            this.configuration = configuration;
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
                await Task.Factory.StartNew(() =>ftpClient.dataSocketSemaphore.Wait());
#else
                await ftpClient.dataSocketSemaphore.WaitAsync();
#endif
                return await ListNodeTypeAsync();
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
                return await ListNodeTypeAsync( FtpNodeType.File );
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
                return await ListNodeTypeAsync( FtpNodeType.Directory );
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
        private async Task<IEnumerable<FtpNodeInformation>> ListNodeTypeAsync( FtpNodeType? ftpNodeType = null )
        {
            string nodeTypeString = !ftpNodeType.HasValue
                ? "all"
                : ftpNodeType.Value == FtpNodeType.File
                    ? "file"
                    : "dir";

            LoggerHelper.Debug( $"[MlsdDirectoryProvider] Listing {ftpNodeType}" );

            EnsureLoggedIn();

            try
            {
                stream = await ftpClient.ConnectDataStreamAsync();
                if ( stream == null )
                    throw new FtpException( "Could not establish a data connection" );

                var result = await ftpClient.ControlStream.SendCommandAsync( FtpCommand.MLSD );
                if ( ( result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen ) && ( result.FtpStatusCode != FtpStatusCode.OpeningData ) && ( result.FtpStatusCode != FtpStatusCode.ClosingData ) )
                    throw new FtpException( "Could not retrieve directory listing " + result.ResponseMessage );

                var directoryListing = RetrieveDirectoryListing().ToList();

                var nodes = ( from node in directoryListing
                              where !node.IsNullOrWhiteSpace()
                              where !ftpNodeType.HasValue || node.Contains( $"type={nodeTypeString}" )
                              select node.ToFtpNode() )
                    .ToList();


                return nodes.AsReadOnly();
            }
            finally
            {
                stream?.Dispose();
                stream = null;
            }
        }
    }
}