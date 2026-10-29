using System.Diagnostics;

namespace GHCPSpendTray.Core;

public static class AppInstrumentation
{
    public const string SourceName = "GHCPSpendTray";
    public static readonly ActivitySource Activities = new(SourceName);
}
