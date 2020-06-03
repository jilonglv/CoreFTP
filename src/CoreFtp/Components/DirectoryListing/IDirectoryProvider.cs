namespace CoreFtp.Components.DirectoryListing
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Infrastructure;

    internal interface IDirectoryProvider
    {
        /// <summary>
        /// Lists all nodes in the current working directory
        /// </summary>
        /// <returns></returns>
        Task<IEnumerable<FtpNodeInformation>> ListAllAsync();

        /// <summary>
        /// Lists all files in the current working directory
        /// </summary>
        /// <returns></returns>
        Task<IEnumerable<FtpNodeInformation>> ListFilesAsync();

        /// <summary>
        /// Lists directories beneath the current working directory
        /// </summary>
        /// <returns></returns>
        Task<IEnumerable<FtpNodeInformation>> ListDirectoriesAsync();
    }
}