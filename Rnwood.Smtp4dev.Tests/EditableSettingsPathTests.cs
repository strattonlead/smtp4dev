using System;
using System.IO;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Rnwood.Smtp4dev.Server.Settings;
using Rnwood.Smtp4dev.Service;
using Rnwood.Smtp4dev.Tests.TestHelpers;
using Xunit;

namespace Rnwood.Smtp4dev.Tests
{
    /// <summary>
    /// The settings file is read from the data dir and written to whatever
    /// <see cref="HostingEnvironmentHelper.GetEditableSettingsFilePath"/> returns. If those two
    /// disagree the server accepts a settings write, reports success, and then never applies it,
    /// because the file it wrote is not the file its reload watcher is watching.
    ///
    /// They used to disagree whenever --baseappdatapath or --nousersettings was given: the helper
    /// read the options through IOptionsMonitor, which nothing configures, so it always saw a
    /// default instance with no path set.
    /// </summary>
    public class EditableSettingsPathTests
    {
        private static readonly string DefaultDataDir =
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "smtp4dev");

        [Fact]
        public void TheExplicitDataPathIsWhereSettingsAreWritten()
        {
            string path = Helper(new CommandLineOptions { BaseAppDataPath = "C:\\temp\\dl" })
                .GetEditableSettingsFilePath();

            path.Should().Be(Path.Join("C:\\temp\\dl", "appsettings.json"));
        }

        [Fact]
        public void TheWritePathIsTheDirectoryTheSettingsAreReadFrom()
        {
            var options = new CommandLineOptions { BaseAppDataPath = "C:\\temp\\dl" };

            Path.GetDirectoryName(Helper(options).GetEditableSettingsFilePath())
                .Should().Be(DirectoryHelper.GetDataDir(options));
        }

        [Fact]
        public void WithoutAnExplicitPathSettingsGoToAppData()
        {
            Helper(new CommandLineOptions()).GetEditableSettingsFilePath()
                .Should().Be(Path.Join(DefaultDataDir, "appsettings.json"));
        }

        [Fact]
        public void ThereIsNoEditableFileWhenUserSettingsAreOff()
        {
            Helper(new CommandLineOptions { NoUserSettings = true })
                .GetEditableSettingsFilePath().Should().BeNull();
        }

        [Fact]
        public void SettingsAreNotEditableWhenUserSettingsAreOff()
        {
            Helper(new CommandLineOptions { NoUserSettings = true })
                .SettingsAreEditable.Should().BeFalse();
        }

        private static HostingEnvironmentHelper Helper(CommandLineOptions commandLineOptions) =>
            new HostingEnvironmentHelper(
                new FakeHostEnvironment(),
                new TestOptionsMonitor<ServerOptions>(new ServerOptions()),
                commandLineOptions);

        private class FakeHostEnvironment : IHostEnvironment
        {
            public string ApplicationName { get; set; } = "smtp4dev";
            public IFileProvider ContentRootFileProvider { get; set; }
            public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
            public string EnvironmentName { get; set; } = "Production";
        }
    }
}
