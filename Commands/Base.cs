using System;
using System.Threading.Tasks;

using Discord;
using Discord.Commands;
using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;

namespace Pandorum
{
    public class CommandsBase : ModuleBase<SocketCommandContext>
    {
        [Command("echo")]
        public async Task CmdEcho(string text)
        {
            await ReplyAsync(text);
        }
    }
}
