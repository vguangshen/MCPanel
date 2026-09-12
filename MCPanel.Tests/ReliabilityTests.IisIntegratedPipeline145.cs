using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void Iis145_AllFrameworkFamiliesUseIntegratedPipeline()
    {
        Assert.AreEqual("Integrated", ProductDeploymentService.ResolveIisPipelineMode("Framework2.0", "v2.0"));
        Assert.AreEqual("Integrated", ProductDeploymentService.ResolveIisPipelineMode("Framework3.5", "v2.0"));
        Assert.AreEqual("Integrated", ProductDeploymentService.ResolveIisPipelineMode("Framework4.0", "v4.0"));
        Assert.AreEqual("Integrated", ProductDeploymentService.ResolveIisPipelineMode("IIS", "v4.0"));
        Assert.AreEqual("Integrated", ProductDeploymentService.ResolveIisPipelineMode(null, "v4.0"));
        Assert.AreEqual("Integrated", ProductDeploymentService.ResolveIisPipelineMode("ASP.NET Core", string.Empty));
    }

    [TestMethod]
    public void Iis145_BindScriptExplicitlyWritesIntegratedPipeline()
    {
        var script = ProductDeploymentService.BuildIisBindScript(
  @"C:\Windows\System32\inetsrv\appcmd.exe",
  "MCPanel",
  @"C:\MCPanel\Runtime\IISRoot",
  "/YX030101",
  @"C:\MCPanel\Products\YX030101",
  "YX030101",
  "v2.0",
  "Integrated",
  enable32Bit: true,
  port: 8088);

        StringAssert.Contains(script, "$managedPipelineMode='Integrated'");
        StringAssert.Contains(script, "/managedPipelineMode:$managedPipelineMode");
    }
}