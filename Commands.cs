using System;
using System.Reflection;
using System.Threading.Tasks;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;

namespace Pandorum
{
    public class Commands
    {
        public Commands()
        {
            var commands = Pandorum.Services.GetRequiredService<CommandService>();
            commands.AddModulesAsync(Assembly.GetEntryAssembly(), Pandorum.Services);

            foreach(CommandInfo cmd in commands.Commands)
            {
                string info = "  !";

                if(!string.IsNullOrEmpty(cmd.Module.Group))
                    info += $"{cmd.Module.Group} ";

                info += cmd.Name;

                Pandorum.Log(LogSeverity.Info, nameof(Commands), info);
            }

            Pandorum.Services.GetRequiredService<DiscordSocketClient>().MessageReceived += OnMessageReceived;
            Pandorum.Services.GetRequiredService<CommandService>().CommandExecuted += OnCommandExecuted;
        }

        private async Task OnMessageReceived(SocketMessage msg)
        {
            var message = msg as SocketUserMessage;
            if(message == null)
                return;

            var discord = Pandorum.Services.GetRequiredService<DiscordSocketClient>();

            // Ignore own messages
            if(message.Author.Id == discord.CurrentUser.Id)
                return;

            // Ignore other bots messages
            if(message.Author.IsBot)
                return;

            int idx = 0;
            if(message.HasStringPrefix("!", ref idx))
            {
                var context = new SocketCommandContext(discord, message);
                var result = await Pandorum.Services.GetRequiredService<CommandService>().ExecuteAsync(context, idx, Pandorum.Services);

                //if (!result.IsSuccess)
                //    Console.WriteLine($"Command error : {result.ToString()} : {message.Content}");
            }
        }

        private Task OnCommandExecuted(Optional<CommandInfo> command, ICommandContext context, IResult result)
        {
            if (!string.IsNullOrEmpty(result?.ErrorReason))
            {
                Pandorum.Log(LogSeverity.Warning, nameof(Commands), result.ErrorReason);
            }

            return Task.CompletedTask;
        }
    }
}