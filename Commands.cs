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
        private IServiceProvider BotServices;

        public Commands(DiscordSocketClient discord, CommandService commands, IServiceProvider services)
        {
            BotServices = services;

            commands.AddModulesAsync(Assembly.GetEntryAssembly(), BotServices);

            foreach(CommandInfo cmd in commands.Commands)
            {
                string info = "  !";

                if(!string.IsNullOrEmpty(cmd.Module.Group))
                    info += $"{cmd.Module.Group} ";

                info += cmd.Name;

                Pandorum.Log(LogSeverity.Info, nameof(Commands), info);
            }

            discord.MessageReceived += OnMessageReceived;
            commands.CommandExecuted += OnCommandExecuted;
        }

        private async Task OnMessageReceived(SocketMessage msg)
        {
            var message = msg as SocketUserMessage;
            if(message == null)
                return;

            // Ignore own messages
            if(message.Author.Id == Pandorum.Discord.CurrentUser.Id)
                return;

            // Ignore other bots messages
            if(message.Author.IsBot)
                return;

            int idx = 0;
            if(message.HasStringPrefix("!", ref idx))
            {
                var context = new SocketCommandContext(Pandorum.Discord, message);
                var result = await Pandorum.DiscordCommands.ExecuteAsync(context, idx, BotServices);
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