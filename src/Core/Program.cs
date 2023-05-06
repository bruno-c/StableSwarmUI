using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using StableUI.Backends;
using StableUI.Utils;
using StableUI.WebAPI;

namespace StableUI.Core;

/// <summary>Class that handles the core entry-point access to the program, and initialization of program layers.</summary>
public class Program
{
    /// <summary>Central store of available backends.</summary>
    public static BackendHandler Backends; // TODO: better location for central values

    /// <summary>Primary execution entry point.</summary>
    public static void Main(string[] args)
    {
        SpecialTools.Internationalize(); // Fix for MS's broken localization
        Logs.Init("=== StableUI Starting ===");
        try
        {
            Logs.Init("Parsing command line...");
            ParseCommandLineArgs(args);
            Logs.Init("Applying settings...");
            ApplyCoreSettings();
        }
        catch (InvalidDataException ex)
        {
            Logs.Error($"Command line arguments given are invalid: {ex.Message}");
            return;
        }
        Logs.Init("Loading backends...");
        Backends = new();
        Logs.Init("Prepping API...");
        BasicAPIFeatures.Register();
        foreach (string str in CommandLineFlags.Keys.Where(k => !CommandLineFlagsRead.Contains(k)))
        {
            Logs.Warning($"Unused command line flag '{str}'");
        }
        Logs.Init("Launching server...");
        WebServer.Launch();
    }

    #region settings pre-apply
    /// <summary>Pre-applies settings choices from command line (or other sources).</summary>
    public static void ApplyCoreSettings()
    {
        string environment = GetCommandLineFlag("environment", "production").ToLowerFast() switch
        {
            "dev" or "development" => "Development",
            "prod" or "production" => "Production",
            var mode => throw new InvalidDataException($"aspweb_mode value of '{mode}' is not valid")
        };
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
        string host = GetCommandLineFlag("host", "localhost");
        string port = GetCommandLineFlag("port", "7801");
        WebServer.HostURL = $"http://{host}:{port}";
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", WebServer.HostURL);
        string logLevel = GetCommandLineFlag("asp_loglevel", environment == "Development" ? "debug" : "warning");
        WebServer.LogLevel = Enum.Parse<LogLevel>(logLevel, true);
    }
    #endregion

    #region command line
    /// <summary>Parses command line argument inputs and splits them into <see cref="CommandLineFlags"/> and <see cref="CommandLineValueFlags"/>.</summary>
    public static void ParseCommandLineArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--"))
            {
                throw new InvalidDataException($"Error: Unknown command line argument '{arg}'");
            }
            string key = arg[2..].ToLower();
            string value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                value = args[++i];
            }
            if (CommandLineFlags.ContainsKey(key))
            {
                throw new InvalidDataException($"Error: Duplicate command line flag '{key}'");
            }
            CommandLineFlags[key] = value;
        }
    }

    /// <summary>Command line value-flags are contained here. Flags without value contain string 'true'. Don't read this directly, use <see cref="GetCommandLineFlag(string, string)"/>.</summary>
    public static Dictionary<string, string> CommandLineFlags = new();

    /// <summary>Helper to identify when command line flags go unused.</summary>
    public static HashSet<string> CommandLineFlagsRead = new();

    /// <summary>Get the command line flag for a given name, and default value.</summary>
    public static string GetCommandLineFlag(string key, string def)
    {
        CommandLineFlagsRead.Add(key);
        return CommandLineFlags.GetValueOrDefault(key, def);
    }
    #endregion
}