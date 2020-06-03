namespace CoreFtp.Infrastructure.Stream
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    public class FtpDataStream : Stream
    {
        private readonly Stream encapsulatedStream;
        private readonly FtpClient client;

        public override bool CanRead => encapsulatedStream.CanRead;
        public override bool CanSeek => encapsulatedStream.CanSeek;
        public override bool CanWrite => encapsulatedStream.CanWrite;
        public override long Length => encapsulatedStream.Length;

        public override long Position
        {
            get { return encapsulatedStream.Position; }
            set { encapsulatedStream.Position = value; }
        }


        public FtpDataStream(Stream encapsulatedStream, FtpClient client)
        {
            LoggerHelper.Debug("[FtpDataStream] Constructing");
            this.encapsulatedStream = encapsulatedStream;
            this.client = client;
        }

        protected override void Dispose(bool disposing)
        {
            LoggerHelper.Debug("[FtpDataStream] Disposing");
            base.Dispose(disposing);

            try
            {
                encapsulatedStream.Dispose();

                if (client.Configuration.DisconnectTimeoutMilliseconds.HasValue)
                {
                    client.ControlStream.SetTimeouts(client.Configuration.DisconnectTimeoutMilliseconds.Value);
                }
                client.CloseFileDataStreamAsync().Wait();
            }
            catch (Exception e)
            {
                LoggerHelper.Warn("Closing the data stream took longer than expected:"+ e.ToString());
            }
            finally
            {
                client.ControlStream.ResetTimeouts();
            }
        }

        public 
#if !NET40
            override
#endif
            async Task FlushAsync(CancellationToken cancellationToken)
        {
            LoggerHelper.Debug("[FtpDataStream] FlushAsync");
            await encapsulatedStream.FlushAsync(cancellationToken);
        }

        public 
#if !NET40
            override
#endif
            async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            LoggerHelper.Debug("[FtpDataStream] ReadAsync");
            return await encapsulatedStream.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public
#if !NET40
            override
#endif
            async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            LoggerHelper.Debug("[FtpDataStream] WriteAsync");
            await encapsulatedStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush()
        {
            LoggerHelper.Debug("[FtpDataStream] Flush");
            encapsulatedStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LoggerHelper.Debug("[FtpDataStream] Read");
            return encapsulatedStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            LoggerHelper.Debug("[FtpDataStream] Seek");
            return encapsulatedStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            LoggerHelper.Debug("[FtpDataStream] SetLength");
            encapsulatedStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            LoggerHelper.Debug("[FtpDataStream] Write");
            encapsulatedStream.Write(buffer, offset, count);
        }
    }
}
