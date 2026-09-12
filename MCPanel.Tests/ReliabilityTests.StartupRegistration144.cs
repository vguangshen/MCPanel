using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void StartupRegistration_DefaultsToCurrentUserRunAndTrayMode()
    {
        var xaml = ReadRepositoryFile("App.xaml");
        var bootstrapper = ReadRepositoryFile("StartupRegistrationBootstrapper.cs");

        StringAssert.Contains(xaml, "Startup=\"App_StartupRegistration\"");
        StringAssert.Contains(bootstrapper, "Registry.CurrentUser");
        StringAssert.Contains(bootstrapper, "StartupDefaultApplied");
        StringAssert.Contains(bootstrapper, "settings.SetStartupEnabled(true)");
        StringAssert.Contains(
            PanelSettingsService.BuildStartupCommand(@"C:\MCPanel\MCPanel.exe"),
            ApplicationLaunchMode.TrayArgument);
    }

    [TestMethod]
    public void StartupRegistration_DefaultIsAppliedOnlyOnce()
    {
        Assert.IsTrue(StartupRegistrationBootstrapper.IsDefaultAlreadyApplied(1));
        Assert.IsTrue(StartupRegistrationBootstrapper.IsDefaultAlreadyApplied(1L));
        Assert.IsFalse(StartupRegistrationBootstrapper.IsDefaultAlreadyApplied(0));
        Assert.IsFalse(StartupRegistrationBootstrapper.IsDefaultAlreadyApplied(null));
    }

    [TestMethod]
    public void StartupRegistration_SkipsUpdaterButAllowsTrayUiLaunch()
    {
        Assert.IsTrue(App.IsNonInteractiveLaunch(new[] { "--apply-update", "plan.json" }));
        Assert.IsTrue(App.IsNonInteractiveLaunch(new[] { "--wait-update-plan", "plan.json" }));
        Assert.IsFalse(App.IsNonInteractiveLaunch(new[] { ApplicationLaunchMode.TrayArgument }));
        Assert.IsFalse(App.IsNonInteractiveLaunch(Array.Empty<string>()));
    }
}
