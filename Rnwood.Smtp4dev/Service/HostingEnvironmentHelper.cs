using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rnwood.Smtp4dev.Server.Settings;

namespace Rnwood.Smtp4dev.Service
{
    public interface IHostingEnvironmentHelper
    {
        string GetEditableSettingsFilePath();

        string GetDefaultSettingsFilePath();

        bool IsRunningInContainer();

        bool SettingsAreEditable { get; }
    }

    public class HostingEnvironmentHelper : IHostingEnvironmentHelper
    {
        private readonly IHostEnvironment hostEnvironment;

        // The command line is parsed once in Program before the host is built and registered as a
        // singleton. Nothing configures IOptionsMonitor<CommandLineOptions>, so asking for one
        // hands back a default instance with every option unset - see FORK-CHANGES.md FP13.
        private readonly CommandLineOptions commandLineOptions;
        private readonly IOptionsMonitor<ServerOptions> serverOptions;

        public HostingEnvironmentHelper(IHostEnvironment hostEnvironment, IOptionsMonitor<ServerOptions> serverOptions, CommandLineOptions commandLineOptions)
        {
            this.hostEnvironment = hostEnvironment;
            this.commandLineOptions = commandLineOptions;
            this.serverOptions = serverOptions;
        }

        /// <summary>
        /// Check if this process is running on Windows in an in process instance in IIS
        /// </summary>
        /// <returns>True if Windows and in an in process instance on IIS, false otherwise</returns>
        internal static bool IsRunningInProcessIIS()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            var processName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().ProcessName);
            return (processName.Contains("w3wp", StringComparison.OrdinalIgnoreCase) ||
                    processName.Contains("iisexpress", StringComparison.OrdinalIgnoreCase));
        }

        public bool IsRunningInContainer()
        {
            return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        }

        public bool SettingsAreEditable
        {
            get
            {
                if (serverOptions.CurrentValue.LockSettings)
                {
                    return false;
                }

                string editableSettingsFile = GetEditableSettingsFilePath();
                if (string.IsNullOrEmpty(editableSettingsFile))
                {
                    return false;
                }

                if (File.Exists(editableSettingsFile))
                {
                    try
                    {
                        //Test file can be opened in write mode
                        File.OpenWrite(editableSettingsFile).Close();
                        return true;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                }

                //Settings file does not exist yet. Test access to write a file to the parent dir
                string settingsFolder = Path.GetDirectoryName(editableSettingsFile);
                if (!Directory.Exists(settingsFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(settingsFolder);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }

                string testFileName;
                do
                {
                    testFileName = Path.Combine(settingsFolder, Guid.NewGuid().ToString());
                } while (File.Exists(testFileName));

                try
                {
                    File.OpenWrite(testFileName).Close();
                    File.Delete(testFileName);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }


        public string GetDefaultSettingsFilePath()
        {
            return Path.Join(hostEnvironment.ContentRootPath, "appsettings.json");
        }

        /// <summary>
        /// Get path to appsettings.json to which settings changed at runtime should be saved.
        /// For IIS this is inside the runtime directory.
        /// </summary>
        /// <returns>appsettings.json filePath</returns>
        public string GetEditableSettingsFilePath()
        {
            string dataDir;

            if (!string.IsNullOrEmpty(commandLineOptions.BaseAppDataPath))
            {
                // Explicit path always takes precedence
                dataDir = commandLineOptions.BaseAppDataPath;
            }
            else if (commandLineOptions.NoUserSettings)
            {
                return null;
            }
            else if (IsRunningInProcessIIS())
            {
                dataDir = Path.Join(hostEnvironment.ContentRootPath, "smtp4dev");
            }
            else
            {
                // Must stay in step with DirectoryHelper.GetDataDir, which is the directory the
                // settings file is read and watched from.
                dataDir = DirectoryHelper.GetDataDir(commandLineOptions);
            }
            return Path.Join(dataDir, "appsettings.json");
        }
    }
}