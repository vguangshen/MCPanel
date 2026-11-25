namespace MCPanel;

/// <summary>How saved proxy rules are treated when nginx.conf is (re)written.</summary>
internal enum NginxSavedRulesPolicy
{
    /// <summary>
    /// Installation: a saved configuration is kept only when it has several
    /// rules, and their ports are checked before writing; a single default rule
    /// gets a fresh free port.
    /// </summary>
    Install,

    /// <summary>
    /// Starting the service: any saved configuration is kept; a port conflict
    /// reported by <c>nginx -t</c> lists the rules' ports.
    /// </summary>
    Start
}

/// <summary>Writes nginx.conf and validates it with <c>nginx -t</c> (installer and runtime share this).</summary>
internal static class NginxConfigurator
{
    /// <summary>
    /// Writes the saved rules or, failing that, the first free candidate port,
    /// validates the result with <paramref name="testConfiguration"/>
    /// (<c>nginx -t</c>), saves the options and returns the main listen port.
    /// </summary>
    public static async Task<int> ConfigureAndTestAsync(
        string nginxRoot,
        Func<CancellationToken, Task> testConfiguration,
        NginxSavedRulesPolicy policy,
        CancellationToken cancellationToken)
    {
        var savedOptions = NginxRuntimeManager.LoadOptions();
        var minimumRules = policy == NginxSavedRulesPolicy.Install ? 2 : 1;
        if (savedOptions is not null && savedOptions.Rules.Count >= minimumRules)
        {
            return await ApplySavedRulesAsync(nginxRoot, NginxRuntimeManager.NormalizeOptions(savedOptions), testConfiguration, policy, cancellationToken);
        }

        Exception? lastError = null;
        foreach (var port in NginxRuntimeManager.BuildPortCandidates())
        {
            if (!SystemProbe.IsPortAvailable(port))
            {
                continue;
            }

            try
            {
                NginxRuntimeManager.CleanConfigFiles(nginxRoot);
                NginxRuntimeManager.WriteListenPort(nginxRoot, port);
                await testConfiguration(cancellationToken);
                NginxRuntimeManager.SaveOptions(port);
                return port;
            }
            catch (Exception ex) when (NginxRuntimeManager.IsPortBindFailure(ex.Message))
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 配置校验失败：{ex.Message}", ex);
            }
        }

        throw new InvalidOperationException($"Nginx 无法找到可用监听端口。请检查 {string.Join("、", NginxRuntimeManager.BuildPortCandidates())} 是否被占用。", lastError);
    }

    private static async Task<int> ApplySavedRulesAsync(
        string nginxRoot,
        NginxRuntimeOptions options,
        Func<CancellationToken, Task> testConfiguration,
        NginxSavedRulesPolicy policy,
        CancellationToken cancellationToken)
    {
        if (policy == NginxSavedRulesPolicy.Install)
        {
            var configuredPorts = NginxRuntimeManager.GetEffectiveListenPorts(options).ToArray();
            foreach (var port in configuredPorts.Length == 0 ? [options.ListenPort] : configuredPorts)
            {
                if (!SystemProbe.IsPortAvailable(port))
                {
                    throw new InvalidOperationException($"Nginx 多规则配置的端口 {port} 已被占用，请先在“管理”中调整端口后再安装。");
                }
            }
        }

        try
        {
            NginxRuntimeManager.CleanConfigFiles(nginxRoot);
            NginxRuntimeManager.WriteManagedConfig(nginxRoot, options);
            await testConfiguration(cancellationToken);
            NginxRuntimeManager.SaveOptions(options);
            return options.ListenPort;
        }
        catch (Exception ex) when (policy == NginxSavedRulesPolicy.Start && NginxRuntimeManager.IsPortBindFailure(ex.Message))
        {
            var ports = string.Join("、", options.EnabledRules.Select(rule => rule.ListenPort).Distinct().OrderBy(port => port));
            throw new InvalidOperationException($"Nginx 监听端口被占用，请在“管理”中调整端口后再启动。当前端口：{ports}。", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                policy == NginxSavedRulesPolicy.Install ? $"Nginx 多规则配置校验失败：{ex.Message}" : $"Nginx 配置校验失败：{ex.Message}",
                ex);
        }
    }

    /// <summary>True when nginx.exe from this root runs, as a process or as the MCPanel service.</summary>
    public static bool IsProcessOrServiceRunning(string nginxRoot) =>
        NginxRuntimeManager.IsRunningUnderRoot(nginxRoot) ||
        NginxWindowsServiceManager.IsRunningForRoot(nginxRoot);
}
