namespace CoreFtp.Infrastructure.Extensions
{
    using System.Threading.Tasks;
    public class TaskExtension
    {
        public static Task<T> FromResultEx<T>(T v)
        {
#if NET40
            return Task.Factory.StartNew<T>(() => v);
#else
            return Task.FromResult(v);
#endif
        }
    }
}
