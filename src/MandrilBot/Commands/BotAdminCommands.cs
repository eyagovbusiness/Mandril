﻿using AngleSharp;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;
using MandrilBot.Configuration;
using MandrilBot.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;
using MandrilBot.BackgroundServices.NewMemberManager;
using System;

namespace MandrilBot.Commands
{
    /// <summary>
    /// Class with definition of the Discord bot commands that can be used to interact with the bot from Discord only allowed to member with the defined Admin role.
    /// </summary>
    internal class BotAdminCommands : BaseCommandModule
    {
        private readonly IMandrilDiscordBot _mandrilDiscordBot;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ulong _botAdminRoleId;

        public BotAdminCommands(IMandrilDiscordBot aMandrilDiscordBot, IServiceScopeFactory aServiceScopeFactory, IConfiguration aConfiguration)
        {
            _mandrilDiscordBot = aMandrilDiscordBot;
            _serviceScopeFactory = aServiceScopeFactory;
            _botAdminRoleId = aConfiguration.GetValue<ulong>("BotAdminRoleId");
        }

        private bool HasAdminRole(DiscordMember aDiscordMember)
            => aDiscordMember.Roles.Any(x => x.Id == _botAdminRoleId);

        /// <summary>
        /// This command makes the bot to reply a message with the list of the current members with the NoMediaRole 
        /// and the time when they joined the guild.
        /// </summary>
        /// <param name="aCommandContext"></param>
        /// <returns></returns>
        [Command("get-newjoined")]
        public async Task GetNewJoined(CommandContext aCommandContext)
        {
            try
            {
                if (!HasAdminRole(aCommandContext.Member))
                    return;

                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var lNewMemberManagerService = scope.ServiceProvider.GetRequiredService<INewMemberManagementService>();
                    var lNoMediaDays = lNewMemberManagerService.GetNoMediaDays();
                    var lNewMemberList = await lNewMemberManagerService.GetNewDiscordMemberList((DiscordMember member) => true);
                    var lMessageContent = string.Join(Environment.NewLine, lNewMemberList.Select((member, index) => $"{++index} - **{member.Nickname ?? member.DisplayName}** joined {GetPastTimeSince(member.JoinedAt)}, created {GetPastTimeSince(member.CreationTimestamp)}. {GetNewMemberEmojiInfo(member, lNoMediaDays)}"));
                    await aCommandContext.Channel.SendMessageAsync(lMessageContent);
                }
            }
            catch (BadRequestException)
            {
                await aCommandContext.Channel.SendMessageAsync("Something went wrong!");
            }

        }

        /// <summary>
        /// This command disables temporarily the auto-ban of new joined bots.
        /// </summary>
        /// <param name="aCommandContext"></param>
        /// <returns></returns>
        [Command("open-bots")]
        public async Task OpenBots(CommandContext aCommandContext)
        {
            try
            {
                if (!HasAdminRole(aCommandContext.Member))
                    return;

                await aCommandContext.Channel.SendMessageAsync("New bots will be allowed to join the server during the next 5 minutes...");
                if(await _mandrilDiscordBot.AllowTemporarilyJoinNewBots(5))
                    await aCommandContext.Channel.SendMessageAsync("New bots are no longer allowed join the server.");
                else 
                    await aCommandContext.Channel.SendMessageAsync("New bots were already allowed join the server at this time.");

            }
            catch (BadRequestException)
            {
                await aCommandContext.Channel.SendMessageAsync("Something went wrong!");
            }

        }

        #region Helpers
        /// <summary>
        /// Get a fancy string representing the time that passed since the provided <see cref="DateTimeOffset"/>.
        /// </summary>
        /// <param name="aDateTimeOffset"></param>
        /// <returns></returns>
        private string GetPastTimeSince(DateTimeOffset aDateTimeOffset)
        {
            TimeSpan lTimePast = DateTimeOffset.Now - aDateTimeOffset;

            string lResult = lTimePast.TotalDays switch
            {
                < 1 => $"{(int)lTimePast.TotalHours} hours ago",
                < 2 => "yesterday",
                < 30 => $"{(int)lTimePast.TotalDays} days ago",
                < 365 => $"{(int)(lTimePast.TotalDays / 30)} months, {(int)(lTimePast.TotalDays % 30)} days ago",
                _ => $"{(int)(lTimePast.TotalDays / 365)} years, {(int)((lTimePast.TotalDays % 365) / 30)} months, {(int)((lTimePast.TotalDays % 365) % 30)} days ago"
            };

            return lResult;
        }

        /// <summary>
        /// Gets an string representing information about the new member status with emojis.
        /// </summary>
        /// <param name="aDiscordMember"></param>
        /// <param name="aNoMediaDays"></param>
        /// <returns></returns>
        private string GetNewMemberEmojiInfo(DiscordMember aDiscordMember, int aNoMediaDays)
        {
            string lRes = string.Empty;
            var lTimeNow = DateTimeOffset.Now;
            //if the new member account was created only two weeks ago or less.
            if ((lTimeNow - aDiscordMember.CreationTimestamp).TotalDays <= 14)
                lRes += ":warning:";
            //if the new member will accquire soon the MediaRole
            var lTotalJoinedDays = (lTimeNow - aDiscordMember.JoinedAt).TotalDays;
            if (lTotalJoinedDays >= Convert.ToInt32(aNoMediaDays * 0.8))
            {
                var lDays = aNoMediaDays - lTotalJoinedDays;
                if (lDays < 1)
                {
                    var lHours = (int)(lDays * 24);
                    var lHoursString = lHours < 1 ? "less than 1" : lHours.ToString();
                    lRes += $":arrow_double_up:({lHoursString}h)";
                }

                else
                    lRes += $":arrow_double_up:({(int)(aNoMediaDays - lTotalJoinedDays)}d)";
            }
            return lRes;
        }

        #endregion

    }
}