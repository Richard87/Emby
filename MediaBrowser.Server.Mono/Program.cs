using MediaBrowser.Model.Logging;
using MediaBrowser.Server.Mono.Native;
using MediaBrowser.Server.Startup.Common;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emby.Drawing;
using Emby.Server.Implementations;
using Emby.Server.Implementations.EnvironmentInfo;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Logging;
using Emby.Server.Implementations.Networking;
using MediaBrowser.Controller;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.System;
using Mono.Unix.Native;
using ILogger = MediaBrowser.Model.Logging.ILogger;
using X509Certificate = System.Security.Cryptography.X509Certificates.X509Certificate;

namespace MediaBrowser.Server.Mono
{
    public class MainClass
    {
        private static ILogger _logger;
        private static IFileSystem FileSystem;
        private static IServerApplicationPaths _appPaths;
        private static ILogManager _logManager;

        private static readonly TaskCompletionSource<bool> ApplicationTaskCompletionSource = new TaskCompletionSource<bool>();
        private static bool _restartOnShutdown;

        public static void Main(string[] args)
        {
            var applicationPath = Assembly.GetEntryAssembly().Location;

            SetSqliteProvider();

            var options = new StartupOptions(Environment.GetCommandLineArgs());

            // Allow this to be specified on the command line.
            var customProgramDataPath = options.GetOption("-programdata");

            var appPaths = CreateApplicationPaths(applicationPath, customProgramDataPath);
            _appPaths = appPaths;

            using (var logManager = new SimpleLogManager(appPaths.LogDirectoryPath, "server"))
            {
                _logManager = logManager;

                logManager.ReloadLogger(LogSeverity.Info);
                logManager.AddConsoleOutput();

                var logger = _logger = logManager.GetLogger("Main");

                ApplicationHost.LogEnvironmentInfo(logger, appPaths, true);

                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                RunApplication(appPaths, logManager, options);

                _logger.Info("Disposing app host");

                if (_restartOnShutdown)
                {
                    StartNewInstance(options);
                }
            }
        }

        private static void SetSqliteProvider()
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        }

        private static ServerApplicationPaths CreateApplicationPaths(string applicationPath, string programDataPath)
        {
            if (string.IsNullOrEmpty(programDataPath))
            {
                programDataPath = ApplicationPathHelper.GetProgramDataPath(applicationPath);
            }

            var appFolderPath = Path.GetDirectoryName(applicationPath);

            return new ServerApplicationPaths(programDataPath, appFolderPath, Path.GetDirectoryName(applicationPath));
        }

        private static void RunApplication(ServerApplicationPaths appPaths, ILogManager logManager, StartupOptions options)
        {
            // Allow all https requests
            ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(delegate { return true; });

            var environmentInfo = GetEnvironmentInfo();

            var fileSystem = new MonoFileSystem(logManager.GetLogger("FileSystem"), environmentInfo, appPaths.TempDirectory);

            FileSystem = fileSystem;

            using (var appHost = new MonoAppHost(appPaths,
                logManager,
                options,
                fileSystem,
                new PowerManagement(),
                "emby.mono.zip",
                environmentInfo,
                new NullImageEncoder(),
                new SystemEvents(logManager.GetLogger("SystemEvents")),
                new NetworkManager(logManager.GetLogger("NetworkManager"))))
            {
                if (options.ContainsOption("-v"))
                {
                    Console.WriteLine(appHost.ApplicationVersion.ToString());
                    return;
                }

                Console.WriteLine("appHost.Init");

                var initProgress = new Progress<double>();

                var task = appHost.Init(initProgress);

                Task.WaitAll(task);

                appHost.ImageProcessor.ImageEncoder = ImageEncoderHelper.GetImageEncoder(_logger, logManager, fileSystem, options, () => appHost.HttpClient, appPaths, environmentInfo);

                Console.WriteLine("Running startup tasks");

                task = appHost.RunStartupTasks();
                Task.WaitAll(task);

                task = ApplicationTaskCompletionSource.Task;

                Task.WaitAll(task);
            }
        }

        private static MonoEnvironmentInfo GetEnvironmentInfo()
        {
            var info = new MonoEnvironmentInfo();

            var uname = GetUnixName();

            var sysName = uname.sysname ?? string.Empty;

            if (string.Equals(sysName, "Darwin", StringComparison.OrdinalIgnoreCase))
            {
                info.OperatingSystem = Model.System.OperatingSystem.OSX;
            }
            else if (string.Equals(sysName, "Linux", StringComparison.OrdinalIgnoreCase))
            {
                info.OperatingSystem = Model.System.OperatingSystem.Linux;
            }
            else if (string.Equals(sysName, "BSD", StringComparison.OrdinalIgnoreCase))
            {
                info.OperatingSystem = Model.System.OperatingSystem.BSD;
            }

            var archX86 = new Regex("(i|I)[3-6]86");

            if (archX86.IsMatch(uname.machine))
            {
                info.SystemArchitecture = Architecture.X86;
            }
            else if (string.Equals(uname.machine, "x86_64", StringComparison.OrdinalIgnoreCase))
            {
                info.SystemArchitecture = Architecture.X64;
            }
            else if (uname.machine.StartsWith("arm", StringComparison.OrdinalIgnoreCase))
            {
                info.SystemArchitecture = Architecture.Arm;
            }
            else if (System.Environment.Is64BitOperatingSystem)
            {
                info.SystemArchitecture = Architecture.X64;
            }
            else
            {
                info.SystemArchitecture = Architecture.X86;
            }

            return info;
        }

        private static Uname _unixName;

        private static Uname GetUnixName()
        {
            if (_unixName == null)
            {
                var uname = new Uname();
                try
                {
                    Utsname utsname;
                    var callResult = Syscall.uname(out utsname);
                    if (callResult == 0)
                    {
                        uname.sysname = utsname.sysname ?? string.Empty;
                        uname.machine = utsname.machine ?? string.Empty;
                    }

                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error getting unix name", ex);
                }
                _unixName = uname;
            }
            return _unixName;
        }

        public class Uname
        {
            public string sysname = string.Empty;
            public string machine = string.Empty;
        }

        /// <summary>
        /// Handles the UnhandledException event of the CurrentDomain control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="UnhandledExceptionEventArgs"/> instance containing the event data.</param>
        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = (Exception)e.ExceptionObject;

            new UnhandledExceptionWriter(_appPaths, _logger, _logManager, FileSystem, new ConsoleLogger()).Log(exception);

            if (!Debugger.IsAttached)
            {
                var message = LogHelper.GetLogMessage(exception).ToString();

                if (message.IndexOf("InotifyWatcher", StringComparison.OrdinalIgnoreCase) == -1 &&
                    message.IndexOf("_IOCompletionCallback", StringComparison.OrdinalIgnoreCase) == -1)
                {
                    Environment.Exit(System.Runtime.InteropServices.Marshal.GetHRForException(exception));
                }
            }
        }

        public static void Shutdown()
        {
            ApplicationTaskCompletionSource.SetResult(true);
        }

        public static void Restart()
        {
            _restartOnShutdown = true;

            Shutdown();
        }

        private static void StartNewInstance(StartupOptions startupOptions)
        {
            _logger.Info("Starting new instance");

            string module = startupOptions.GetOption("-restartpath");
            string commandLineArgsString = startupOptions.GetOption("-restartargs") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(module))
            {
                module = Environment.GetCommandLineArgs().First();
            }
            if (!startupOptions.ContainsOption("-restartargs"))
            {
                var args = Environment.GetCommandLineArgs()
                    .Skip(1)
                    .Select(NormalizeCommandLineArgument)
                    .ToArray();

                commandLineArgsString = string.Join(" ", args);
            }

            _logger.Info("Executable: {0}", module);
            _logger.Info("Arguments: {0}", commandLineArgsString);

            Process.Start(module, commandLineArgsString);
        }

        private static string NormalizeCommandLineArgument(string arg)
        {
            if (arg.IndexOf(" ", StringComparison.OrdinalIgnoreCase) == -1)
            {
                return arg;
            }

            return "\"" + arg + "\"";
        }
    }

    class NoCheckCertificatePolicy : ICertificatePolicy
    {
        public bool CheckValidationResult(ServicePoint srvPoint, X509Certificate certificate, WebRequest request, int certificateProblem)
        {
            return true;
        }
    }

    public class MonoEnvironmentInfo : EnvironmentInfo
    {
        public override string GetUserId()
        {
            return Syscall.getuid().ToString(CultureInfo.InvariantCulture);
        }
    }
}
