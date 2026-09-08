using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MarchCenter.AccountApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpSvn;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void MySqlNewPasswordMatchesOriginalStoreDefault()
    {
        var credentials = MySqlCredentialStore.CreateNew();

        Assert.AreEqual("mike", credentials.Password);
        Assert.AreEqual(3380, credentials.Port);

        var script = EnvironmentInstaller.BuildMySqlScript(
            @"D:\MCPanel\MySQL",
            @"D:\MCPanel\MySQL\bin\mysqld.exe",
            @"D:\MCPanel\MySQL\bin\mysql.exe",
            credentials.Password);
        StringAssert.Contains(script, "-P3380");
        StringAssert.Contains(script, "port=3380");

        var existingInstallationScript = EnvironmentInstaller.BuildMySqlScript(
            @"D:\MCPanel\MySQL",
            @"D:\MCPanel\MySQL\bin\mysqld.exe",
            @"D:\MCPanel\MySQL\bin\mysql.exe",
            credentials.Password,
            3306);
        StringAssert.Contains(existingInstallationScript, "-P3306");
        StringAssert.Contains(existingInstallationScript, "port=3306");
    }

    [TestMethod]
    public void ProductXmlIdentityIsReappliedAfterSupplierUpdate()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var config = Path.Combine(root, "config.xml");
            File.WriteAllText(config, "<?xml version=\"1.0\" encoding=\"utf-8\"?><ROOT><SystemSoft><SoftVersionID>OLD</SoftVersionID></SystemSoft><Keep>value</Keep></ROOT>");

            Assert.IsTrue(ProductConfigurationService.TryApplyXmlIdentity(root, "DS3107", out var updatedFile));
            Assert.AreEqual(config, updatedFile);
            var document = XDocument.Load(config);
            Assert.AreEqual("DS3107", document.Descendants("SoftVersionID").Single().Value);
            Assert.AreEqual("value", document.Descendants("Keep").Single().Value);
            Assert.IsFalse(File.ReadAllText(config).Contains("encoding=\"utf-16\"", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void JavaProductConfigurationUpdatesAllOriginalStoreKeysWithoutDuplicates()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var appRoot = Path.Combine(root, "application");
            Directory.CreateDirectory(appRoot);
            var config = Path.Combine(appRoot, "systemConfig.yml");
            File.WriteAllText(config,
                "custom.keep=yes\n" +
                "global.system.VersionID=OLD\n" +
                "global.system.MySQLPassword=OLDPASSWORD\n");
            var credentials = new MySqlDefaultCredentials("127.0.0.1", 3307, "root", "mike");

            ProductConfigurationService.ApplyJavaConfiguration(root, appRoot, "YX030301", "高级", "MySQL5.6", credentials);
            ProductConfigurationService.ApplyJavaConfiguration(root, appRoot, "YX030301", "高级", "MySQL5.6", credentials);

            var text = File.ReadAllText(config);
            StringAssert.Contains(text, "custom.keep=yes");
            StringAssert.Contains(text, "global.system.VersionName=高级");
            StringAssert.Contains(text, "global.system.VersionID=YX030301");
            StringAssert.Contains(text, "global.system.MySQLambient=MySQL5.6");
            StringAssert.Contains(text, "global.system.MySQLPort=3307");
            StringAssert.Contains(text, "global.system.MySQLUserName=root");
            StringAssert.Contains(text, "global.system.MySQLPassword=mike");
            Assert.AreEqual(1, Regex.Matches(text, "global\\.system\\.VersionID=").Count);
            Assert.AreEqual(1, Regex.Matches(text, "global\\.system\\.MySQLPassword=").Count);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ProductPrerequisitesMatchOriginalJavaAndDotNetStacks()
    {
        var java = new ProductItem("JAVA1", "Java 产品", "实训", string.Empty, ProductSource.Online)
        {
            RunEnvironment = "Tomcat 8",
            DevLanguage = "Java"
        };
        var dotnet = new ProductItem("NET1", ".NET 产品", "实训", string.Empty, ProductSource.Online)
        {
            RunEnvironment = "IIS / .NET Framework",
            SqlEnvironment = "SQL Server 2022",
            DevLanguage = "C#"
        };

        CollectionAssert.AreEqual(
            new[] { EnvironmentKind.Tomcat, EnvironmentKind.MySql },
            ProductEnvironmentPreflight.GetRequiredKinds(java).ToArray());
        CollectionAssert.AreEqual(
            new[] { EnvironmentKind.Iis, EnvironmentKind.SqlServer },
            ProductEnvironmentPreflight.GetRequiredKinds(dotnet).ToArray());
    }

    [TestMethod]
    public void TomcatStartupCommandRestoresProductsWithoutOpeningMainWindow()
    {
        var command = TomcatProductStartupManager.BuildStartupCommand(@"D:\MCPanel\MCPanel.exe");

        Assert.AreEqual("\"D:\\MCPanel\\MCPanel.exe\" --restore-tomcat-products", command);
        Assert.IsTrue(TomcatProductStartupManager.IsRestoreRequest(new[] { "--restore-tomcat-products" }));
    }

    [TestMethod]
    public void PanelStartupCommandUsesLazyTrayMode()
    {
        var command = PanelSettingsService.BuildStartupCommand(@"D:\MCPanel\MCPanel.exe");

        Assert.AreEqual("\"D:\\MCPanel\\MCPanel.exe\" --tray", command);
        Assert.IsTrue(ApplicationLaunchMode.IsTrayStartupRequest(new[] { "--TRAY" }));
        Assert.IsFalse(ApplicationLaunchMode.IsTrayStartupRequest(Array.Empty<string>()));
        Assert.IsFalse(ApplicationLaunchMode.IsTrayStartupRequest(new[] { "--tray=1" }));
    }

    [TestMethod]
    public void NginxManagedWebsiteConfigIncludesSslRedirectAndBandwidth()
    {
        var options = new NginxRuntimeOptions
        {
            ListenPort = 80,
            Rules =
            [
                new NginxProxyRule
                {
                    Name = "产品域名 TEST",
                    ListenPort = 80,
                    ServerName = "example.test www.example.test",
                    LocationPath = "/",
                    ProxyTarget = "http://127.0.0.1:9000/TEST/",
                    SslEnabled = true,
                    HttpsPort = 443,
                    SslCertificatePath = @"D:\MCPanel\Nginx\conf\certificate.pem",
                    SslCertificateKeyPath = @"D:\MCPanel\Nginx\conf\private-key.pem",
                    RedirectHttpToHttps = true,
                    MaxRateKbps = 2048
                }
            ]
        };

        var config = NginxRuntimeManager.BuildManagedConfig(options);

        StringAssert.Contains(config, "listen       80;");
        StringAssert.Contains(config, "return 301 https://$host$request_uri;");
        StringAssert.Contains(config, "listen       443 ssl;");
        StringAssert.Contains(config, "ssl_protocols        TLSv1.2;");
        StringAssert.Contains(config, "limit_rate 2048k;");
        StringAssert.Contains(config, "proxy_pass http://127.0.0.1:9000/TEST/;");
        Assert.AreEqual("http://127.0.0.1:9000/TEST/", NginxRuntimeManager.NormalizeProxyTarget("http://127.0.0.1:9000/TEST/"));
        CollectionAssert.AreEqual(new[] { 80, 443 }, NginxRuntimeManager.GetEffectiveListenPorts(options).ToArray());
    }

    [TestMethod]
    public void NginxServiceOwnershipRequiresACompleteRootToken()
    {
        Assert.IsTrue(NginxWindowsServiceManager.ImagePathContainsRoot(
            "\"D:\\MCPanel\\MCPanel.exe\" --mcpanel-nginx-service \"D:\\MCPanel\\Nginx\\nginx-1.14.2\"",
            @"D:\MCPanel\Nginx\nginx-1.14.2"));
        Assert.IsFalse(NginxWindowsServiceManager.ImagePathContainsRoot(
            "\"D:\\MCPanel\\MCPanel.exe\" --mcpanel-nginx-service \"D:\\MCPanel\\Nginx\\nginx-1.14.20\"",
            @"D:\MCPanel\Nginx\nginx-1.14.2"));
    }

    [TestMethod]
    public void NginxDefaultsUseLegacyPort72()
    {
        Assert.AreEqual(72, NginxRuntimeManager.DefaultListenPort);
        Assert.AreEqual(72, new NginxRuntimeOptions().ListenPort);
        Assert.AreEqual(72, new NginxProxyRule().ListenPort);
        Assert.AreEqual(72, NginxRuntimeManager.CreateDefaultRule().ListenPort);

        var config = NginxRuntimeManager.BuildManagedConfig(new NginxRuntimeOptions());
        StringAssert.Contains(config, "listen       72;");
    }

    [TestMethod]
    public void NginxRuntimeProbeReadsManualListenPortChangesAndIncludedFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var conf = Path.Combine(root, "conf");
            var included = Path.Combine(conf, "conf.d");
            Directory.CreateDirectory(included);
            File.WriteAllText(
                Path.Combine(conf, "nginx.conf"),
                "events {}\nhttp { listen 127.0.0.1:8123; include conf.d/*.conf; }",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(included, "manual.conf"),
                "server { listen 9443 ssl; }",
                Encoding.UTF8);

            CollectionAssert.AreEqual(
                new[] { 8123, 9443 },
                NginxRuntimeManager.ReadConfiguredListenPorts(root).ToArray());
            CollectionAssert.AreEqual(
                new[] { 8123, 9443 },
                NginxRuntimeManager.ParseConfiguredListenPorts("# listen 80;\nlisten 8123; listen [::]:9443 ssl;").ToArray());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void NativeComponentsUseAsciiCompatibilityRootsForChineseInstallPaths()
    {
        var chineseRoot = Path.Combine(Path.GetTempPath(), "中文", "MCPanel");
        var asciiRoot = Path.Combine(Path.GetTempPath(), "MCPanel");

        var chineseNginx = PathCompatibility.GetNativeComponentRoot(chineseRoot, "Nginx");
        var chineseMySql = PathCompatibility.GetNativeComponentRoot(chineseRoot, "MySQL");

        Assert.IsFalse(PathCompatibility.ContainsNonAscii(chineseNginx));
        Assert.IsFalse(PathCompatibility.ContainsNonAscii(chineseMySql));
        Assert.AreEqual(Path.Combine(asciiRoot, "Nginx"), PathCompatibility.GetNativeComponentRoot(asciiRoot, "Nginx"));
    }

    [TestMethod]
    public void MySqlInstallScriptCleansFailedServiceAndWritesNativeErrorLog()
    {
        var script = EnvironmentInstaller.BuildMySqlScript(
            @"D:\MCPanel\MySQL",
            @"D:\MCPanel\MySQL\bin\mysqld.exe",
            @"D:\MCPanel\MySQL\bin\mysql.exe",
            "test-password");

        StringAssert.Contains(script, "$registeredService = $false");
        StringAssert.Contains(script, "$serviceWasPresent = [bool](Get-Service -Name 'MySQL80'");
        StringAssert.Contains(script, "if ($serviceWasPresent) { Fail '安装前仍检测到 MySQL80 服务");
        StringAssert.Contains(script, "sc.exe delete MySQL80");
        StringAssert.Contains(script, "log-error=D:/MCPanel/MySQL/data/mysql-itmc.err");
        StringAssert.Contains(script, @"Ver\s+(8|9)\.|mysqld\s+(8|9)\.");
        StringAssert.Contains(script, "ALTER USER 'root'@'localhost' IDENTIFIED BY");
        StringAssert.Contains(script, "SET PASSWORD FOR 'root'@'localhost' = PASSWORD");
        StringAssert.Contains(script, "--init-file=$initFile");
        StringAssert.Contains(script, "--bind-address=127.0.0.1");
        StringAssert.Contains(script, "bind-address=127.0.0.1");
        StringAssert.Contains(script, "Remove-Item -LiteralPath $initFile");
        StringAssert.Contains(script, "SHUTDOWN;");
        Assert.IsFalse(script.Contains("--skip-grant-tables", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("skip-networking=0", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("mysql_native_password", StringComparison.OrdinalIgnoreCase));

        var serviceAction = EnvironmentRuntimeService.BuildMySqlServiceActionScript(
            "start",
            "mysql-action.log",
            "mysql-action.result");
        StringAssert.Contains(serviceAction, "--init-file=$initFile");
        StringAssert.Contains(serviceAction, "SET PASSWORD FOR 'root'@'localhost' = PASSWORD");
        StringAssert.Contains(serviceAction, "SHUTDOWN;");
        StringAssert.Contains(serviceAction, "$mysqlRoots | Where-Object");
        StringAssert.Contains(serviceAction, "Name -in @('mysqld.exe', 'mysqld-itmc.exe')");
        Assert.IsFalse(serviceAction.Contains("$mysqlSearchRoots |", StringComparison.Ordinal));
        Assert.IsFalse(serviceAction.Contains("--skip-grant-tables", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CustomHttpsRedirectDoesNotDuplicateQueryString()
    {
        var rule = CustomWebsiteService.BuildHttpsRedirectRule(8443);
        var action = rule.Element("action");

        Assert.IsNotNull(action);
        Assert.AreEqual("https://{C:1}:8443{REQUEST_URI}", action!.Attribute("url")?.Value);
        Assert.AreEqual("false", action.Attribute("appendQueryString")?.Value);
    }

    [TestMethod]
    public void AccountApiFormatsIpv6ListenerAndEndpointHosts()
    {
        Assert.AreEqual("+", AccountApiHost.FormatListenerHost("::"));
        Assert.AreEqual("[::1]", AccountApiHost.FormatListenerHost("::1"));
        Assert.AreEqual("[::1]", AccountApiConfiguration.FormatUriHost("[::1]"));

        var configuration = new AccountApiConfiguration { BindAddress = "::1", Port = 8088 };
        Assert.AreEqual("http://[::1]:8088", configuration.Endpoint);
    }

    [TestMethod]
    public void TomcatProbeReadsHttpConnectorsAndIgnoresAjp()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var conf = Path.Combine(root, "conf");
            Directory.CreateDirectory(conf);
            File.WriteAllText(Path.Combine(conf, "server.xml"),
                "<Server><Service><Connector port=\"8080\" protocol=\"HTTP/1.1\" /><Connector port=\"8009\" protocol=\"AJP/1.3\" /></Service></Server>");

            CollectionAssert.AreEqual(new[] { 8080 }, TomcatRuntimeProbe.ReadHttpPorts(root).ToArray());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void TomcatProductRecoveryNeverTreatsConnectorZeroAsAPersistedPort()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var conf = Path.Combine(root, "conf");
            Directory.CreateDirectory(conf);
            var serverXml = Path.Combine(conf, "server.xml");
            File.WriteAllText(
                serverXml,
                "<Server><Service name=\"Catalina0\"><Connector port=\"0\" protocol=\"HTTP/1.1\" /></Service></Server>");

            Assert.IsNull(
                ProductDeploymentService.ReadFirstTomcatHttpPort(serverXml),
                "port=0 会让 Tomcat 随机选端口，不能作为 MCPanel 产品固定端口恢复。" );
            Assert.IsFalse(ProductDeploymentService.IsValidTomcatProductPort(0));
            Assert.IsTrue(ProductDeploymentService.IsValidTomcatProductPort(9000));
            Assert.IsTrue(ProductDeploymentService.IsValidTomcatProductPort(10000));
            Assert.IsTrue(
                TomcatProductInstanceManager.ProductStartupTimeout > TimeSpan.FromSeconds(80),
                "云电脑日志中的 Java 应用启动耗时约 77 秒，部署等待时间必须覆盖慢速首次建库。" );
            Assert.IsTrue(
                TomcatRuntimeProbe.DefaultStartupTimeout > TimeSpan.FromSeconds(80),
                "环境页启动全部 Tomcat 应用时也必须使用适合云电脑的等待时间。" );
            Assert.AreEqual(
                9000,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(9000, 9000, new[] { 9005 }),
                "旧生成实例不能覆盖主配置和持久化端口。" );
            Assert.AreEqual(
                9001,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(0, 9001, new[] { 9005 }),
                "主 server.xml 应优先修复缺失的持久化端口。" );
            Assert.AreEqual(
                9005,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(0, null, new[] { 0, 9005 }),
                "只有两层权威状态都无效时才能采用生成实例端口。" );
            Assert.AreEqual(
                0,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(8080, 8080, new[] { 8080 }),
                "共享 Tomcat 的 8080 不能被误当成产品独立端口。" );

            File.WriteAllText(
                serverXml,
                "<Server><Service name=\"Catalina\"><Connector port=\"8080\" protocol=\"HTTP/1.1\" /></Service></Server>");
            Assert.IsNull(
                ProductDeploymentService.ReadFirstTomcatHttpPort(serverXml),
                "生成实例只能恢复产品专用端口池中的端口。" );

            File.WriteAllText(
                serverXml,
                "<Server><Service name=\"Catalina9000\"><Connector port=\"9000\" protocol=\"HTTP/1.1\" /></Service></Server>");
            Assert.AreEqual(9000, ProductDeploymentService.ReadFirstTomcatHttpPort(serverXml));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void TomcatProductLaunchBindsTheGeneratedCatalinaBaseWithoutSupplierBatchScripts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var tomcatHome = Path.Combine(root, "Tomcat Home");
            var instanceRoot = Path.Combine(root, "Runtime", "TomcatProductRuns", "JAVA-1");
            var bin = Path.Combine(tomcatHome, "bin");
            var javaBin = Path.Combine(tomcatHome, "jre", "jre", "bin");
            var conf = Path.Combine(instanceRoot, "conf");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(javaBin);
            Directory.CreateDirectory(conf);
            Directory.CreateDirectory(Path.Combine(instanceRoot, "temp"));
            File.WriteAllText(Path.Combine(bin, "bootstrap.jar"), "test");
            File.WriteAllText(Path.Combine(bin, "tomcat-juli.jar"), "test");
            File.WriteAllText(Path.Combine(javaBin, "java.exe"), "test");
            File.WriteAllText(Path.Combine(conf, "logging.properties"), string.Empty);
            var jvmProperties = Path.Combine(conf, "jvm.properties");
            File.WriteAllText(
                jvmProperties,
                "-Xms256m -Xmx512m -XX:ErrorFile=\"D:/shared-tomcat/logs/hs_err_pid%p.log\"\nTest title");
            EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(
                instanceRoot,
                useInstanceLocalErrorFile: true);

            var startInfo = TomcatProductInstanceManager.BuildTomcatJavaStartInfo(
                tomcatHome,
                instanceRoot,
                redirectOutput: true);

            Assert.AreEqual(Path.GetFullPath(Path.Combine(javaBin, "java.exe")), startInfo.FileName);
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.IsTrue(startInfo.RedirectStandardOutput);
            Assert.IsTrue(startInfo.RedirectStandardError);
            StringAssert.Contains(startInfo.Arguments, "-Dcatalina.base=");
            StringAssert.Contains(startInfo.Arguments, Path.GetFullPath(instanceRoot));
            StringAssert.Contains(startInfo.Arguments, "-Dcatalina.home=");
            StringAssert.Contains(startInfo.Arguments, "org.apache.catalina.startup.Bootstrap");
            var normalizedJvm = File.ReadAllText(jvmProperties);
            StringAssert.Contains(
                normalizedJvm,
                Path.Combine(instanceRoot, "logs", "hs_err_pid%p.log").Replace("\\", "/"));
            Assert.IsFalse(
                normalizedJvm.Contains("shared-tomcat", StringComparison.OrdinalIgnoreCase),
                "产品实例的 JVM 崩溃日志不能继续写入共享 Tomcat 目录。" );
            Assert.IsFalse(
                startInfo.Arguments.Contains("catalina.bat", StringComparison.OrdinalIgnoreCase),
                "供应商修改过的 catalina.bat 不能再覆盖产品实例的 CATALINA_BASE。" );
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void TomcatStopFallbackParsesListeningJavaProcessIdsFromNetstat()
    {
        const string netstat = """
            TCP    0.0.0.0:9000       0.0.0.0:0       LISTENING       18968
            TCP    [::]:9000          [::]:0          LISTENING       18968
            TCP    0.0.0.0:8080       0.0.0.0:0       ESTABLISHED     1234
            TCP    0.0.0.0:7000       0.0.0.0:0       LISTENING       2222
            """;

        CollectionAssert.AreEqual(
            new[] { 18968 },
            TomcatProductInstanceManager.ParseNetstatListeningProcessIds(netstat, new[] { 9000 }).ToArray());
        CollectionAssert.AreEqual(
            new[] { 2222 },
            TomcatProductInstanceManager.ParseNetstatListeningProcessIds(netstat, new[] { 7000 }).ToArray());
        Assert.AreEqual(
            0,
            TomcatProductInstanceManager.ParseNetstatListeningProcessIds(netstat, new[] { 8080 }).Count);
    }

    [TestMethod]
    public void TomcatProbeRequiresEveryConfiguredHttpPortToListen()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        var second = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            first.Start();
            second.Start();

            var ports = new[]
            {
                ((IPEndPoint)first.LocalEndpoint).Port,
                ((IPEndPoint)second.LocalEndpoint).Port
            };

            Assert.IsTrue(TomcatRuntimeProbe.ArePortsListening(ports));

            second.Stop();

            Assert.IsFalse(TomcatRuntimeProbe.ArePortsListening(ports));
        }
        finally
        {
            first.Stop();
            second.Stop();
        }
    }

    [TestMethod]
    public void CustomIisWebsiteInputAndSingleElevationScriptAreDeterministic()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var definition = new CustomWebsiteDefinition
            {
                Id = "site1",
                Name = "TrainingSite",
                PhysicalPath = Path.Combine(root, "site"),
                HttpPort = 8081,
                Domains = new System.Collections.Generic.List<string> { "training.example.test" },
                ManagedRuntimeVersion = "v4.0",
                MaxBandwidthKbps = 1024,
                MimeMappings = CustomWebsiteService.ParseMimeMappings(".webp=image/webp\n.json=application/json")
            };

            CustomWebsiteService.Validate(definition, null);
            const string certificatePassword = "DoNotPersist-Secret-123";
            var script = CustomWebsiteService.BuildConfigureScript(definition, null, certificatePassword, Path.Combine(root, "result"));

            Assert.AreEqual(1, Regex.Matches(script, "Import-Module WebAdministration").Count);
            StringAssert.Contains(script, "New-Website");
            StringAssert.Contains(script, "maxBandwidth -Value 1048576");
            StringAssert.Contains(script, "ProtectedData]::Unprotect");
            Assert.IsFalse(script.Contains(certificatePassword, StringComparison.Ordinal));
            Assert.AreEqual("image/webp", definition.MimeMappings[".webp"]);
            Assert.AreEqual("http://training.example.test:8081/", CustomWebsiteService.BuildUrl(definition));

            var configureScript = Path.Combine(root, "configure.ps1");
            var parserScript = Path.Combine(root, "parse.ps1");
            File.WriteAllText(configureScript, script, Encoding.UTF8);
            File.WriteAllText(parserScript,
                "param([string]$Target)\n$tokens=$null; $errors=$null\n" +
                "[System.Management.Automation.Language.Parser]::ParseFile($Target,[ref]$tokens,[ref]$errors) | Out-Null\n" +
                "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }\nexit 0\n",
                Encoding.UTF8);
            using var parser = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{parserScript}\" -Target \"{configureScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            Assert.IsNotNull(parser);
            parser!.WaitForExit();
            Assert.AreEqual(0, parser.ExitCode, parser.StandardError.ReadToEnd());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }
}
