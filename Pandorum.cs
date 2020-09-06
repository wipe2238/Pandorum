using System;
using System.Collections.Generic;
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
    public class Configuration
    {
        public class ConfigurationCalendar
        {
            public bool   Enabled;

            public UInt64 DebugChannel;
        }

        public class ConfigurationCommands
        {
            public bool Enabled;
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
        public bool Dry = false;
        public bool Passive = false;

        public static ServiceProvider Services;

        private static void Main(params string[] args)
        {
            Pandorum self = new Pandorum();

            foreach(string arg in args)
            {
                if(arg == "--dry")
                    self.Dry = true;
                else if(arg == "--passive")
                    self.Passive = true;
            }

            self.OpenTheBox().GetAwaiter().GetResult();
        }

        // Static wrappers, for cleaner code

        public static void Log(LogSeverity severity, string source, string message)
        {
            Logger.Print(new LogMessage(severity, source, message));
        }

        //

        private async Task OpenTheBox()
        {
            Log(LogSeverity.Info, nameof(Pandorum), "Opening the box...");

            string configFile = "Config/Pandorum.json";
            Log(LogSeverity.Info, nameof(Pandorum), "Init configuration...");

            Configuration configuration = new Configuration();
            if(File.Exists(configFile))
                configuration = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(configFile));

            Log(LogSeverity.Info, nameof(Pandorum), "Init events...");
            Console.CancelKeyPress += new ConsoleCancelEventHandler(OnConsoleCancelKeyPress);

            Log(LogSeverity.Info, nameof(Pandorum), "Init services...");
            Services = ConfigureServices(configuration, new ServiceCollection());

            // Initialize services

            Services.GetRequiredService<Logger>();

            var discord = Services.GetRequiredService<DiscordSocketClient>();
            discord.Connected += OnDiscordConnected;

            if(Services.GetRequiredService<Configuration>().Calendar.Enabled)
            {
                Log(LogSeverity.Info, nameof(Pandorum), "Init calendar...");
                Services.GetRequiredService<Calendar>();
            }

            if(!Passive && Services.GetRequiredService<Configuration>().Commands.Enabled)
            {
                Log(LogSeverity.Info, nameof(Pandorum), "Init commands...");
                Services.GetRequiredService<Commands>();
            }

            if(Dry)
                return;

            Log(LogSeverity.Info, nameof(Pandorum), "Init Discord...");
            await discord.LoginAsync(TokenType.Bot, Services.GetRequiredService<Configuration>().Token);
            await discord.StartAsync();

            await Task.Delay(-1);
        }

        private async Task BackWork()
        {
            void DoBackWork()
            {
                while(true)
                {
                    System.Threading.Thread.Sleep(150);
                    Console.WriteLine("::Background::");
                }
            }

            var task = new Task(() => DoBackWork());
            task.Start();
            await task; 
        }

        private ServiceProvider ConfigureServices(Configuration configuration, IServiceCollection services)
        {
           services
            .AddSingleton(configuration)
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

        private void OnConsoleCancelKeyPress(object sender, ConsoleCancelEventArgs args)
        {
            Log(LogSeverity.Info, nameof(Pandorum), "Shutting down...");

            var calendar = Services.GetService<Calendar>();
            if(calendar != null)
                calendar.StopWorker();
        }

        private Task OnDiscordConnected()
        {
            Services.GetRequiredService<DiscordSocketClient>().SetGameAsync("loot", null, ActivityType.Watching);

            return Task.CompletedTask;
        }

        //
    }
}
