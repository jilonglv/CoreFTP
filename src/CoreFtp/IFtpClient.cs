namespace CoreFtp
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Enum;
    using Infrastructure;

    public interface IFtpClient : IDisposable
    {
        bool IsConnected { get;  }

        bool IsEncrypted { get; }
        bool IsLogined { get; }

        bool IsAuthenticated { get; }

        string WorkingDirectory { get; }

        void Configure(FtpClientConfiguration configuration);

        Task LoginAsync();

        Task LogOutAsync();

        Task ChangeWorkingDirectoryAsync(string directory);

        Task CreateDirectoryAsync(string directory);

        Task RenameAsync(string from, string to);

        Task DeleteDirectoryAsync(string directory);

        Task<FtpResponse> SetClientName(string clientName);

        Task<Stream> OpenFileReadStreamAsync(string fileName);

        Task<Stream> OpenFileWriteStreamAsync(string fileName);

        Task CloseFileDataStreamAsync(CancellationToken ctsToken = default(CancellationToken));

        Task<IEnumerable<FtpNodeInformation>> ListAllAsync();

        Task<IEnumerable<FtpNodeInformation>> ListFilesAsync();

        Task<IEnumerable<FtpNodeInformation>> ListDirectoriesAsync();

        Task DeleteFileAsync(string fileName);

        Task SetTransferMode(FtpTransferMode transferMode, char secondType = '\0');

        Task<long> GetFileSizeAsync(string fileName);

        Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default(CancellationToken));

        Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default(CancellationToken));
    }
}
