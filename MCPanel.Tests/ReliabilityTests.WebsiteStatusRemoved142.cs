using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void WebsiteStatusMonitoringUiAndTimerAreRemoved()
    {
        var window = ReadRepositoryFile("MainWindow.xaml");
        var shell = ReadRepositoryFile("MainWindow.xaml.cs");
        var sites = ReadRepositoryFile("MainWindow.Websites.cs");
        var viewModel = ReadRepositoryFile("ViewModels/MainViewModel.Websites.cs");
        var templates = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");

        Assert.IsFalse(window.Contains("WebsiteStateFilter", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("刷新状态", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("Binding WebsiteOperationText", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("RefreshWebsiteStatesAsync(IsVisible && SitesPage.IsVisible", StringComparison.Ordinal));
        Assert.IsFalse(sites.Contains("RefreshWebsiteStates_Click", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("RefreshWebsiteStatesAsync", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("WebsiteStateFilter", StringComparison.Ordinal));
        Assert.IsFalse(templates.Contains("RuntimeStatusText", StringComparison.Ordinal));
        Assert.IsFalse(templates.Contains("RuntimeStatusBrush", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebsiteFilteringNoLongerDependsOnRuntimeState()
    {
        var code = ReadRepositoryFile("ViewModels/MainViewModel.cs");
        StringAssert.Contains(code, "MatchesWebsiteOptions(item.IsTomcatDeployment)");
        StringAssert.Contains(code, "MatchesWebsiteOptions(false)");
        Assert.IsFalse(code.Contains("MatchesWebsiteOptions(item.IsTomcatDeployment, item.CanBrowse)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("MatchesWebsiteOptions(false, item.IsRunning)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RemovedWebsiteProbeHasNoTemplateProxy()
    {
        var proxy = ReadRepositoryFile("MainWindowTemplates.xaml.cs");
        Assert.IsFalse(proxy.Contains("ProbeWebsite_Click", StringComparison.Ordinal));
    }
}