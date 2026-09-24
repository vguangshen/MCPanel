# IIS / Web Server lifecycle compatibility

The environment card controls the **machine-wide IIS role**, not just one site.
Uninstall removes URL Rewrite and disables IIS; existing site configuration and
other software using IIS can be affected. Back up IIS configuration and sites
before testing uninstall on a shared server.

| Windows | IIS path used by MCPanel | Prerequisite |
| --- | --- | --- |
| Server 2008 SP2 (IIS 7.0), Server 2008 R2 SP1 (IIS 7.5), Windows 7 SP1 | `pkgmgr /iu` and `/uu` for IIS and WAS; register ASP.NET 4 with `aspnet_regiis -ir` after installation | .NET Framework 4.6.2, Windows PowerShell 2.0 or later, compatible URL Rewrite MSI; graphical Windows installation. Server 2008 SP2 Server Core cannot run .NET Framework 4.6.2. Windows PowerShell 2.0 may need installation on Server 2008 SP2. |
| Server 2012 / 2012 R2 and newer, Windows 8 / 8.1 / 10 / 11 | DISM PowerShell optional features for installation; `Uninstall-WindowsFeature Web-Server` on Server, DISM PowerShell on desktop | Required Windows feature payload and compatible URL Rewrite MSI; IIS Manager needs its management feature. |

`IIS-ASPNET45` is a Windows component only on Windows 8 / Server 2012 and
newer. The older IIS 7 path registers the installed .NET Framework 4 runtime
separately; ASP.NET 2/3.5 role services are not installed there unless configured
independently with their own prerequisites. Both paths check for `appcmd.exe`, the URL Rewrite module, and
running WAS and W3SVC before reporting installation success. Start and stop
use `NET START W3SVC` / `NET STOP W3SVC`; restart stops and starts W3SVC in
sequence. Uninstall stops WAS and its dependents with `NET STOP WAS /y` before
removing the role. All three service buttons verify W3SVC reaches the expected
state. A pending
install or uninstall remains visible as **继续安装** or **继续卸载** after restart.
MCPanel launches the native Windows PowerShell executable (using `Sysnative`
from a 32-bit process on 64-bit Windows) so IIS tools and feature commands use
the OS architecture rather than the application's process architecture.

The default `Environment.Iis.UrlRewriteUrl` points at a package hosted by the
product's legacy service. Its MSI version, signature and x86/x64 architecture
cannot be inferred from the configuration URL. Check the actual installer on
each target OS, or configure the official matching URL Rewrite 2.1 MSI before
claiming that the whole installation is compatible.
The current release workflow includes x64 native dependencies. A 32-bit Server
2008 installation needs a separately validated x86 build and dependencies;
the framework's own x86 support does not make the distributed package x86-compatible.

## Verification on real machines

Run this matrix on disposable, fully updated VMs for each claimed OS version;
static script tests and a current Windows CI runner cannot prove Windows Server
2008 behavior. The PR Windows workflow compiles the project and runs IIS script
generation checks; it does not change system services. Record the OS edition, service pack, OS architecture, installed
.NET Framework / PowerShell versions, MSI signer and architecture, and the
result of each step:

1. Start without IIS. Install Web Server; if a restart is requested, reboot
   and select **继续安装**. Verify W3SVC and WAS are running, `appcmd.exe` exists,
   `appcmd list modules` includes `RewriteModule`, and an ASP.NET 4 test site
   responds. Check the install log for omitted Windows features.
2. Stop, start, then restart Web Server. Verify W3SVC transitions to Stopped,
   Running and Running and a local test site responds again. Test failure with
   a blocked service or an occupied HTTP port; the UI must show an error.
3. Back up IIS state, then uninstall. If Windows requests a reboot, reboot and
   select **继续卸载**. Verify the IIS role is disabled and URL Rewrite has been
   removed, without treating an unrelated Windows Update reboot flag as
   evidence of a successful uninstall.
4. Install again on the same VM and repeat the service actions. Check that the
   IIS status and the continuation buttons match the machine state after every
   relaunch of MCPanel.

Microsoft references:

- [IIS 7 command line installation and `pkgmgr` options](https://learn.microsoft.com/en-us/iis/install/installing-iis-7/installing-iis-from-the-command-line)
- [IIS 7.5 on Server 2008 R2, including `pkgmgr` IIS / WAS uninstall](https://learn.microsoft.com/en-us/iis/install/installing-iis-7/install-and-configure-iis-on-server-core)
- [IIS 8.5 installation and DISM / PowerShell](https://learn.microsoft.com/en-us/iis/install/installing-iis-85/installing-iis-85-on-windows-server-2012-r2)
- [Microsoft IIS service restart guidance: `NET STOP` / `NET START`](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/iis/www-administration-management/using-iisreset-restart-iis-result-error)
- [IIS web server service commands and WAS dependencies](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/jj635851%28v%3Dws.11%29)
- [ASP.NET 4.5 Windows component boundary](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/aspnet/configuration/install-aspnet-45-windows-8-server-2012)
- [.NET Framework operating system requirements](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)
- [Windows PowerShell 2.0 availability on Server 2008 SP2](https://devblogs.microsoft.com/powershell/tag/powershell-2-0-download/)
- [WOW64 file system redirection and `Sysnative`](https://learn.microsoft.com/en-us/windows/win32/winprog64/file-system-redirector)
- [URL Rewrite official download](https://www.iis.net/downloads/microsoft/url-rewrite)
