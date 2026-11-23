
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void AccountApiPortEditorIsWiredToTransactionalRuntimeRestart()
    {
        var service = ReadAccountApiPortEditingSource("AccountApiManagerService.cs");
        var pageXaml = ReadAccountApiPortEditingSource("AccountApiPage.xaml");
        var pageCode = ReadAccountApiPortEditingSource("AccountApiPage.xaml.cs");
        var dialogXaml = ReadAccountApiPortEditingSource("PanelInputDialog.xaml");
        var dialogCode = ReadAccountApiPortEditingSource("PanelInputDialog.xaml.cs");

        StringAssert.Contains(service, "public async Task SetPortAsync(int port");
        StringAssert.Contains(service, "port is < 1 or > 65535");
        StringAssert.Contains(service, "\"AccountApi:Server\"");
        StringAssert.Contains(service, "var previousPort = configuration.Port;");
        StringAssert.Contains(service, "var wasRunning = EmbeddedAccountApiRuntime.IsRunning;");
        StringAssert.Contains(service, "EmbeddedAccountApiRuntime.Start(updatedConfiguration.ConfigPath);");
        StringAssert.Contains(service, "previousPort.ToString(System.Globalization.CultureInfo.InvariantCulture)");
        StringAssert.Contains(service, "已恢复原端口");

        StringAssert.Contains(pageXaml, "Content=\"编辑\"");
        StringAssert.Contains(pageXaml, "Click=\"EditEndpoint_Click\"");
        StringAssert.Contains(pageXaml, "IsEnabled=\"{Binding CanEditEndpoint}\"");
        StringAssert.Contains(pageCode, "PanelInputDialog.CreateAccountApiPortEditor(currentPort)");
        StringAssert.Contains(pageCode, "_service.SetPortAsync(port)");
        StringAssert.Contains(pageCode, "public bool CanEditEndpoint");

        StringAssert.Contains(dialogXaml, "x:Name=\"ContextBadgeText\"");
        StringAssert.Contains(dialogXaml, "x:Name=\"TextHint\"");
        StringAssert.Contains(dialogCode, "CreateAccountApiPortEditor(int currentPort)");
        StringAssert.Contains(dialogCode, "端口范围 1–65535");
        StringAssert.Contains(dialogCode, "port is < 1 or > 65535");
    }

    private static string ReadAccountApiPortEditingSource(string relativePath)
    {
        return File.ReadAllText(RepositorySource.Resolve(relativePath));
    }
}
