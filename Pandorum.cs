using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Microsoft.Extensions.DependencyInjection;

// https://developers.google.com/calendar/quickstart/dotnet?pli=1#step_1_turn_on_the //

namespace Pandorum
{
    public class Cache
    {
        private JObject Data;
        private readonly string FileName;

        public Cache(string filename)
        {
            Data = JObject.Parse("{}");
            FileName = filename;
        }

        public bool Load()
        {
            if(File.Exists(FileName))
            {
                Data = JObject.Parse(File.ReadAllText(FileName));
                return true;
            }
            else
            {
                Data = JObject.Parse("{}");
                return false;
            }
        }

        public void Save()
        {
            File.WriteAllText(FileName, Data.ToString(), System.Text.Encoding.UTF8);
        }

        public JToken this[string key]
        {
            get
            {
                if(key.Contains("."))
                    return Data.SelectToken(key);
                else
                    return Data[key];
            }
            set
            {
                Data[key] = value;
            }
        }
    }

    public class Configuration
    {
        public class ConfigurationCalendar
        {
            public bool   Enabled = false;

            // ID of calendar used by !calendar commands
            // Note that all calendars with name starting with "Pandorum" (case insensitive) are checked for incoming events
            public string Id = "";

            public UInt64 DebugChannel = 0;
        }

        public class ConfigurationCommands
        {
            public bool Enabled = false;
        }

        public string Token;
        public List<UInt64> Maintainers = new List<UInt64>();

        public ConfigurationCalendar Calendar = new ConfigurationCalendar();
        public ConfigurationCommands Commands = new ConfigurationCommands();
    }

    public class Logger
    {
        public Logger(DiscordSocketClient discord, CommandService commands)
        {
            discord.Log += OnLog;
            commands.Log += OnLog;
        }

        public Task OnLog(LogMessage message)
        {
            Print(message);

            return Task.CompletedTask;
        }

        public static void Print(LogMessage message)
        {
            DateTime now = DateTime.Now;

            string msg = $"[{now.ToString("HH:mm:ss")}][{message.Severity.ToString(),-8}][{message.Source,-12}]";
            if(!string.IsNullOrEmpty(message.Message))
                msg += $" {message.Message}";
            Console.WriteLine(msg);

            if(message.Exception != null)
                Console.WriteLine(message.Exception.ToString());
        }
    }

    class Pandorum
    {
        private static Pandorum Self;

        public static Cache               Cache           => Self.Services.GetRequiredService<Cache>();
        public static Calendar            Calendar        => Self.Services.GetRequiredService<Calendar>();
        public static Commands            Commands        => Self.Services.GetRequiredService<Commands>();
        public static Configuration       Configuration   => Self.Services.GetRequiredService<Configuration>();
        public static DiscordSocketClient Discord         => Self.Services.GetRequiredService<DiscordSocketClient>();
        public static CommandService      DiscordCommands => Self.Services.GetRequiredService<CommandService>();

        //

        public bool Dry = false;
        public bool Passive = false;

        private ServiceProvider Services;

        private static void Main(params string[] args)
        {
            var cultureInfo = new CultureInfo("en-US");

            CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
            CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

            Self = new Pandorum();

            foreach(string arg in args)
            {
                if(arg == "--dry")
                    Self.Dry = true;
                else if(arg == "--passive")
                    Self.Passive = true;
            }

            Self.OpenTheBox().GetAwaiter().GetResult();
        }

        public static void Log(LogSeverity severity, string source, string message)
        {
            Logger.Print(new LogMessage(severity, source, message));
        }

        //

        private async Task OpenTheBox()
        {
            Log(LogSeverity.Info, nameof(Pandorum), "Opening the box...");

            string jsonFile = "Config/Pandorum.json";
            Log(LogSeverity.Info, nameof(Pandorum), "Init configuration...");

            Configuration configuration = new Configuration();
            if(File.Exists(jsonFile))
                configuration = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(jsonFile));
            else
                Log(LogSeverity.Warning, nameof(Pandorum), $"{jsonFile} not found");

            jsonFile = "Config/Pandorum.Cache.json";
            Log(LogSeverity.Info, nameof(Pandorum), "Init cache...");

            Cache cache = new Cache(jsonFile);
            if(!cache.Load())
                Log(LogSeverity.Warning, nameof(Pandorum), $"{jsonFile} not found");

            Log(LogSeverity.Info, nameof(Pandorum), "Init events...");
            Console.CancelKeyPress += new ConsoleCancelEventHandler(OnConsoleCancelKeyPress);

            Log(LogSeverity.Info, nameof(Pandorum), "Init services...");
            Services = ConfigureServices(configuration, cache, new ServiceCollection());

            // Initialize services

            Services.GetRequiredService<Logger>();

            Discord.Connected += OnDiscordConnected;

            if(Configuration.Calendar.Enabled)
            {
                Log(LogSeverity.Info, nameof(Pandorum), "Init calendar...");
                Services.GetRequiredService<Calendar>();
            }

            if(!Passive && Configuration.Commands.Enabled)
            {
                Log(LogSeverity.Info, nameof(Pandorum), "Init commands...");
                Services.GetRequiredService<Commands>();
            }

            cache.Save();

            if(Dry)
            {
                Log(LogSeverity.Info, nameof(Pandorum), "Dry run -- exiting");
                return;
            }

            if(string.IsNullOrEmpty(Configuration.Token))
            {
                Log(LogSeverity.Error, nameof(Pandorum), "Discord token not found -- exiting");
                return;
            }

            Log(LogSeverity.Info, nameof(Pandorum), "Init Discord...");
            await Discord.LoginAsync(TokenType.Bot, Configuration.Token);
            await Discord.StartAsync();

            await Task.Delay(-1);
        }

        private ServiceProvider ConfigureServices(Configuration configuration, Cache cache, IServiceCollection services)
        {
           services
            .AddSingleton(configuration)
            .AddSingleton(cache)
            .AddSingleton<Logger>()
            .AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Debug,
                MessageCacheSize = 100
            }))
            .AddSingleton(new CommandService(new CommandServiceConfig
            {
                LogLevel = LogSeverity.Debug,
                DefaultRunMode = RunMode.Async
            }))
            .AddSingleton<Commands>()
            .AddSingleton<Calendar>()
            ;

            return services.BuildServiceProvider();
        }

        //

        private void OnConsoleCancelKeyPress(object sender, ConsoleCancelEventArgs args)
        {
            Log(LogSeverity.Info, nameof(Pandorum), "Shutting down...");

            var discord = Services.GetService<DiscordSocketClient>();
            if(discord != null)
                discord.SetStatusAsync(UserStatus.DoNotDisturb);

            var calendar = Services.GetService<Calendar>();
            if(calendar != null)
                calendar.StopWorker();
        }

        private Task OnDiscordConnected()
        {
            Services.GetRequiredService<DiscordSocketClient>().SetGameAsync("loot", null, ActivityType.Watching);

            return Task.CompletedTask;
        }
    }
}
