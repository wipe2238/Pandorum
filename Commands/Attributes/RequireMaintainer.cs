using System;
using System.Threading.Tasks;

using Discord;
using Discord.Commands;

using Microsoft.Extensions.DependencyInjection;

// Based on [RequireOwner]

namespace Pandorum
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public class RequireMaintainerAttribute : PreconditionAttribute
    {
        public override string ErrorMessage { get; set; }

        public override async Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context, CommandInfo command, IServiceProvider services)
        {
            switch (context.Client.TokenType)
            {
                case TokenType.Bot:
                    var application = await context.Client.GetApplicationInfoAsync().ConfigureAwait(false);
                    var configuration = Pandorum.Services.GetRequiredService<Configuration>();

                    if (context.User.Id != application.Owner.Id && !configuration.Maintainers.Contains(context.User.Id))
                        return PreconditionResult.FromError(ErrorMessage ?? "Command can only be run by the maintainer of the bot.");

                    return PreconditionResult.FromSuccess();

                default:
                    return PreconditionResult.FromError($"{nameof(RequireMaintainerAttribute)} is not supported by this {nameof(TokenType)}.");
            }
        }
    }
}
