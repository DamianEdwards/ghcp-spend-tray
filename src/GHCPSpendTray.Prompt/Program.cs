using System.Text.Json;

namespace GHCPSpendTray.Prompt;

public static class Program
{
    public static int Main(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0 || arguments[0] is "--help" or "help")
            {
                Console.WriteLine("ghcp-spend-prompt prompt [--hostname HOST] [--cache-dir DIR] [--refresh-minutes 60] [--request-timeout 15] [--gh-executable gh]");
                Console.WriteLine("ghcp-spend-prompt demo green|yellow|red|in_progress|not_connected");
                Console.WriteLine("ghcp-spend-prompt live-check [options] (opt-in HTTP; no consumption values printed)");
                Console.WriteLine("Protocol: GHCP-SPEND/1<TAB>spend<TAB>forecast<TAB>forecast-state<TAB>connection-state");
                return 0;
            }
            if (arguments[0] == "demo" && arguments.Length == 2)
            {
                Console.WriteLine(PromptState.Demo(arguments[1]).ToProtocol());
                return 0;
            }
            if (arguments[0] == "demo-theme" && arguments.Length == 3)
            {
                WriteDemoTheme(arguments[1], arguments[2]);
                return 0;
            }
            if (arguments[0] == "refresh" && arguments.Length >= 3)
            {
                ValidateKey(arguments[1], 64);
                ValidateKey(arguments[2], 32);
                new PromptService(PromptOptions.Parse(arguments, 3)).Refresh(arguments[1], arguments[2]);
                return 0;
            }
            if (arguments[0] == "live-check")
            {
                PromptOptions options = PromptOptions.Parse(arguments);
                var service = new PromptService(options);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                PromptState state;
                do
                {
                    state = service.ReadPrompt(Console.Error.WriteLine);
                    if (state.ConnectionState != "in_progress") break;
                    Thread.Sleep(100);
                } while (timer.Elapsed < options.WorkerLifetime + TimeSpan.FromSeconds(5));
                bool available = state.ConnectionState == "connected" && state.Spend != "unavailable";
                Console.WriteLine(available ? "PASS: live host-specific consumption available; values withheld." :
                    "FAIL: live consumption unavailable. Check existing gh authentication and endpoint support.");
                return available ? 0 : 1;
            }
            if (arguments[0] != "prompt") throw new ArgumentException("Unknown helper operation.");
            Console.WriteLine(new PromptService(PromptOptions.Parse(arguments))
                .ReadPrompt(Console.Error.WriteLine).ToProtocol());
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or
            IOException or InvalidDataException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Worker pipes may already be closed; refresh errors never enter prompt stdout.
            if (arguments.FirstOrDefault() != "refresh")
            {
                Console.Error.WriteLine("Copilot helper failed. Check options, the dedicated cache directory and installation.");
                if (arguments.FirstOrDefault() == "prompt") Console.WriteLine(PromptState.Unavailable.ToProtocol());
            }
            return 1;
        }
    }

    private static void ValidateKey(string value, int length)
    {
        if (value.Length != length || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Invalid internal worker identifier.");
    }

    private static void WriteDemoTheme(string segmentPath, string output)
    {
        using var segment = JsonDocument.Parse(File.ReadAllBytes(segmentPath));
        using var stream = File.Create(output);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", 4);
        writer.WriteBoolean("final_space", true);
        writer.WriteStartArray("blocks");
        writer.WriteStartObject();
        writer.WriteString("type", "prompt");
        writer.WriteString("alignment", "left");
        writer.WriteStartArray("segments");
        segment.RootElement.WriteTo(writer);
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("style", "plain");
        writer.WriteString("foreground", "#89B4FA");
        writer.WriteString("template", "> ");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
