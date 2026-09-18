using GHSpend.App.Native;
using GHSpend.App.Platform;

namespace GHSpend.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool smoke = args.Contains("--smoke-test", StringComparer.Ordinal);
        bool demo = smoke || args.Contains("--demo", StringComparer.Ordinal);
        string[] bootstrapArgs = args.Where(a => a is not "--smoke-test" and not "--demo").ToArray();
        try
        {
            Bootstrap.Warning += message => Win32.MessageBox(0, message, "GHSpend installation warning", Win32.MB_ICONWARNING);
            if (demo && !bootstrapArgs.Contains("--portable", StringComparer.Ordinal))
                throw new ArgumentException("Demo and smoke modes require --portable --data-dir <isolated-directory>.");
            using var runtime = Bootstrap.Start(bootstrapArgs);
            if (runtime is null) return 0;
            Diagnostics.Initialize(runtime.DataDirectory);
            runtime.Diagnostic += ex => Diagnostics.Record($"Instance coordination failed ({ex.GetType().Name}).");
            var common = new Win32.INITCOMMONCONTROLSEX { dwSize = 8, dwICC = 0x4000 | 0x20 };
            if (Win32.InitCommonControlsEx(ref common) == 0)
                throw new InvalidOperationException("Windows could not initialize common controls.");
            using IApplicationController controller = demo ? new DemoController(runtime.DataDirectory) :
                new ApplicationController(runtime.DataDirectory, runtime.IsPortable);
            using var window = new OverviewWindow(controller);
            runtime.RegisterActivationCallback(() => window.Post(() => window.ShowAccount()));
            window.Start();
            runtime.SignalReady();
            if (!args.Contains("--startup", StringComparer.Ordinal)) window.Show();
            int smokeExit = 0;
            if (smoke)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                    window.Post(() =>
                    {
                        try
                        {
                            window.SmokeCheck();
                            SmokeCredentials();
                            File.WriteAllText(Path.Combine(runtime.DataDirectory, "native-smoke-result.txt"),
                                "PASS: native window, controls, selection, progress, graph, settings, onboarding, " +
                                "Shell notification submission, Credential Manager round-trip, message dispatch.\n" +
                                "No real OAuth, account data, installation, or startup writes performed.\n");
                        }
                        catch (Exception ex)
                        {
                            smokeExit = 1;
                            File.WriteAllText(Path.Combine(runtime.DataDirectory, "native-smoke-result.txt"),
                                $"FAIL: {ex.GetType().Name}: {ex.Message}\n");
                        }
                        finally { window.Dispose(); Win32.PostQuitMessage(smokeExit); }
                    });
                });
            }
            NativeWindow.RunLoop();
            return smokeExit;
        }
        catch (Exception ex)
        {
            Diagnostics.Record($"Application startup failed ({ex.GetType().Name}).");
            // Bootstrap errors are locally produced and contain no authentication payloads.
            Win32.MessageBox(0, ex.Message, "GHSpend could not start", Win32.MB_ICONERROR);
            return 1;
        }
    }

    private static void SmokeCredentials()
    {
        var vault = new CredentialVault();
        string target = "GHSpend/test/" + Guid.NewGuid().ToString("N");
        try
        {
            vault.Write(target, "{\"version\":1,\"test\":\"synthetic-not-a-token\"}");
            if (vault.Read(target) != "{\"version\":1,\"test\":\"synthetic-not-a-token\"}")
                throw new InvalidOperationException("Credential Manager round-trip mismatch.");
        }
        finally { vault.Delete(target); }
        if (vault.Read(target) is not null) throw new InvalidOperationException("Test credential cleanup failed.");
    }
}
