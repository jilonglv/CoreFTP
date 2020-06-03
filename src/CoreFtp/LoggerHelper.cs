using System;
using System.Collections.Generic;
using System.Text;

namespace CoreFtp
{
    public class LoggerHelper
    {
        public static bool IsDebugEnable { get; set; } = false;
        public static bool IsInfoEnable { get; set; } = false;
        public static bool IsWarnEnable { get; set; } = false;
        public static bool IsTraceEnable { get; set; } = false;
        public static bool IsErrorEnable { get; set; } = false;
        public static void Debug(string msg)
        {
            if (IsDebugEnable)
                WriteLog(LogType.Debug, msg);
        }
        public static void Info(string msg)
        {
            if (IsInfoEnable)
                WriteLog(LogType.Info, msg);
        }
        public static void Warn(string msg)
        {
            if (IsWarnEnable)
                WriteLog(LogType.Warning, msg);
        }
        public static void Trace(string msg)
        {
            if (IsTraceEnable)
                WriteLog(LogType.Trace, msg);
        }
        public static void Error(string msg)
        {
            if (IsErrorEnable)
                WriteLog(LogType.Error, msg);
        }
        public static void Write(string msg)
        {
            Console.WriteLine(msg);
        }
        public static void WriteLog(LogType logType, string msg)
        {
            Write($"{logType.ToString()} : {msg}");
        }
    }


    /// <summary>
    /// logger type
    /// </summary>
    public enum LogType
    {
        /// <summary>
        /// debug
        /// </summary>
        Debug,

        /// <summary>
        /// information
        /// </summary>
        Info,

        /// <summary>
        /// warining
        /// </summary>
        Warning,

        /// <summary>
        /// unexcepted error
        /// </summary>
        Error,
        /// <summary>
        /// trace
        /// </summary>
        Trace
    }
}
