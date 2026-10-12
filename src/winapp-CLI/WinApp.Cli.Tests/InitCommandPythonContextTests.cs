// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class InitCommandPythonContextTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IWorkspaceSetupService, SuccessfulWorkspaceSetup>();
        return services;
    }

    [TestMethod]
    [DataRow("--setup-sdks=stable", "sdk")]
    [DataRow("--setup-sdks=none", "none")]
    [DataRow("--config-only", "config-only")]
    [DataRow("--ignore-config", "unmanaged")]
    public async Task Init_ReportsTheNativeSelectedWorkspaceAndMode(string option, string mode)
    {
        var project = _tempDirectory.CreateSubdirectory("native selected project");
        var config = _tempDirectory.CreateSubdirectory("sdk config");
        var report = Path.Join(_tempDirectory.FullName, "python-init.json");
        var originalCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        var originalReport = Environment.GetEnvironmentVariable("WINAPP_PY_INIT_RESULT");
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "python");
            Environment.SetEnvironmentVariable("WINAPP_PY_INIT_RESULT", report);
            var command = GetRequiredService<InitCommand>();
            var exitCode = await ParseAndInvokeWithCaptureAsync(command,
                [project.FullName, "--config-dir", config.FullName, "--use-defaults", option]);

            Assert.AreEqual(0, exitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var root = document.RootElement;
            Assert.AreEqual(1, root.GetProperty("schema").GetInt32());
            Assert.AreEqual(project.FullName, root.GetProperty("project").GetString());
            Assert.AreEqual(config.FullName, root.GetProperty("config_directory").GetString());
            Assert.AreEqual(mode, root.GetProperty("mode").GetString());
            Assert.AreEqual(Path.Join(project.FullName, ".winapp"), root.GetProperty("metadata_directory").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", originalCaller);
            Environment.SetEnvironmentVariable("WINAPP_PY_INIT_RESULT", originalReport);
        }
    }

    [TestMethod]
    public async Task Init_OtherCallersDoNotWriteThePythonReport()
    {
        var report = Path.Join(_tempDirectory.FullName, "python-init.json");
        var originalCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        var originalReport = Environment.GetEnvironmentVariable("WINAPP_PY_INIT_RESULT");
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "npm");
            Environment.SetEnvironmentVariable("WINAPP_PY_INIT_RESULT", report);
            var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<InitCommand>(),
                [_tempDirectory.FullName, "--use-defaults", "--setup-sdks", "none"]);
            Assert.AreEqual(0, exitCode);
            Assert.IsFalse(File.Exists(report));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", originalCaller);
            Environment.SetEnvironmentVariable("WINAPP_PY_INIT_RESULT", originalReport);
        }
    }

    [TestMethod]
    public async Task Init_ReportWriteFailureIsExplicit()
    {
        var originalCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        var originalReport = Environment.GetEnvironmentVariable("WINAPP_PY_INIT_RESULT");
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "python");
            Environment.SetEnvironmentVariable("WINAPP_PY_INIT_RESULT", _tempDirectory.FullName);
            var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<InitCommand>(),
                [_tempDirectory.FullName, "--use-defaults", "--setup-sdks", "none"]);
            Assert.AreEqual(1, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", originalCaller);
            Environment.SetEnvironmentVariable("WINAPP_PY_INIT_RESULT", originalReport);
        }
    }

    private sealed class SuccessfulWorkspaceSetup : IWorkspaceSetupService
    {
        public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
        {
            options.MetadataDirectory = new DirectoryInfo(Path.Join(options.BaseDirectory.FullName, ".winapp"));
            return Task.FromResult(0);
        }
    }
}
