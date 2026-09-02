namespace MCPanel;

internal sealed record ProductEnvironmentPreflightResult(IReadOnlyList<EnvironmentKind> Missing)
{
    public bool CanInstall => Missing.Count == 0;

    public string BuildMessage()
    {
        var names = Missing.Select(ProductEnvironmentPreflight.DisplayName);
        return $"安装此产品前需要先安装：{string.Join("、", names)}。请先进入左侧“环境”页面完成安装后再重试。";
    }
}

internal static class ProductEnvironmentPreflight
{
    public static ProductEnvironmentPreflightResult Check(ProductItem product, EnvironmentRuntimeService runtimeService)
    {
        var missing = GetRequiredKinds(product)
            .Where(kind => !runtimeService.GetState(kind).IsInstalled)
            .ToArray();
        return new ProductEnvironmentPreflightResult(missing);
    }

    internal static IReadOnlyList<EnvironmentKind> GetRequiredKinds(ProductItem product)
    {
        var runtimeText = $"{product.RunEnvironment} {product.DevLanguage}";
        var databaseText = product.SqlEnvironment ?? string.Empty;
        var isJava = ContainsAny(runtimeText, "tomcat", "java", "jsp", "servlet");
        var isDotNet = ContainsAny(runtimeText, "iis", "asp", ".net", "c#", "vb.net", "visual basic", "framework");
        var required = new List<EnvironmentKind>();

        if (isJava)
        {
            required.Add(EnvironmentKind.Tomcat);
        }
        else if (isDotNet)
        {
            required.Add(EnvironmentKind.Iis);
        }

        if (ContainsAny(databaseText, "mysql", "maria"))
        {
            required.Add(EnvironmentKind.MySql);
        }
        else if (ContainsAny(databaseText, "sql server", "sqlserver", "mssql"))
        {
            required.Add(EnvironmentKind.SqlServer);
        }
        else if (string.IsNullOrWhiteSpace(databaseText))
        {
            // The original Store provisions the matching database together with
            // a Java/.NET product when the supplier metadata omits the DB field.
            if (isJava)
            {
                required.Add(EnvironmentKind.MySql);
            }
            else if (isDotNet)
            {
                required.Add(EnvironmentKind.SqlServer);
            }
        }

        return required.Distinct().ToArray();
    }

    internal static string DisplayName(EnvironmentKind kind) => kind switch
    {
        EnvironmentKind.Iis => "Web Server / IIS",
        EnvironmentKind.Tomcat => "Tomcat Server",
        EnvironmentKind.MySql => "MySQL",
        EnvironmentKind.SqlServer => "SQL Server",
        EnvironmentKind.Nginx => "Nginx",
        _ => kind.ToString()
    };

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
