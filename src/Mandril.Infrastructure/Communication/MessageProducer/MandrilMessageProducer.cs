﻿using DSharpPlus;
using DSharpPlus.EventArgs;
using Mandril.Application;
using Mandril.Application.DTOs;
using Mandril.Application.DTOs.Messages;
using Mandril.Application.Mapping;
using MandrilBot.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TGF.CA.Infrastructure.Communication.Publisher.Integration;

namespace Mandril.Infrastructure.Communication.MessageProducer
{
    public class MandrilMessageProducer : IHostedService
    {
        private readonly IMandrilDiscordBot _mandrilDiscordBot;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        public MandrilMessageProducer(IMandrilDiscordBot aMandrilBot, IServiceScopeFactory aServiceScopeFactory)
        {
            _mandrilDiscordBot = aMandrilBot;
            _serviceScopeFactory = aServiceScopeFactory;
        }

        #region IHostedService
        public Task StartAsync(CancellationToken aCancellationToken)
        {
            _mandrilDiscordBot.GuildMemberUpdated += MandrilMessageProducer_GuildMemberUpdated;
            _mandrilDiscordBot.GuildRoleCreated += MandrilDiscordBot_GuildRoleCreated;
            _mandrilDiscordBot.GuildRoleDeleted += MandrilDiscordBot_GuildRoleDeleted;
            _mandrilDiscordBot.GuildRoleUpdated += MandrilDiscordBot_GuildRoleUpdated;
            _mandrilDiscordBot.GuildBanAdded += MandrilDiscordBot_GuildBanAdded;
            _mandrilDiscordBot.GuildBanRemoved += MandrilDiscordBot_GuildBanRemoved;
            return Task.CompletedTask;
        }

        private async Task MandrilDiscordBot_GuildBanRemoved(DiscordClient sender, GuildBanRemoveEventArgs args)
            => await SendMessage(new MemberBanUpdateEventDTO(args.Member.Id.ToString(), args.Guild.Id.ToString(), true), aRoutingKey: "mandril.members.sync");

        private async Task MandrilDiscordBot_GuildBanAdded(DiscordClient sender, GuildBanAddEventArgs args)
            => await SendMessage(new MemberBanUpdateEventDTO(args.Member.Id.ToString(), args.Guild.Id.ToString(), false), aRoutingKey: "mandril.members.sync");

        public Task StopAsync(CancellationToken aCancellationToken)
        {
            _mandrilDiscordBot.GuildMemberUpdated -= MandrilMessageProducer_GuildMemberUpdated;
            _mandrilDiscordBot.GuildRoleCreated -= MandrilDiscordBot_GuildRoleCreated;
            _mandrilDiscordBot.GuildRoleDeleted -= MandrilDiscordBot_GuildRoleDeleted;
            _mandrilDiscordBot.GuildRoleUpdated -= MandrilDiscordBot_GuildRoleUpdated;
            _mandrilDiscordBot.GuildBanAdded -= MandrilDiscordBot_GuildBanAdded;
            _mandrilDiscordBot.GuildBanRemoved -= MandrilDiscordBot_GuildBanRemoved;
            return Task.CompletedTask;
        }
        #endregion

        #region Event Handlers
        private async Task MandrilMessageProducer_GuildMemberUpdated(DiscordClient sender, GuildMemberUpdateEventArgs args)
        {
            await SendIfGuildMemberRoleUpdate(args);
            await SendIfGuildMemberDisplayNameUpdate(args);
            await SendIfGuildMemberAvatarUpdate(args);
        }

        private async Task MandrilDiscordBot_GuildRoleUpdated(DiscordClient sender, GuildRoleUpdateEventArgs args)
            => await SendMessage(new RoleUpdatedDTO(new DiscordRoleDTO(args.RoleAfter.Id.ToString(), args.RoleAfter.Name, (byte)args.RoleAfter.Position)), aRoutingKey: "mandril.roles.sync");

        private async Task MandrilDiscordBot_GuildRoleDeleted(DiscordClient sender, GuildRoleDeleteEventArgs args)
            => await SendMessage(new RoleDeletedDTO(args.Role.Id.ToString()), aRoutingKey: "mandril.roles.sync");

        private async Task MandrilDiscordBot_GuildRoleCreated(DiscordClient sender, GuildRoleCreateEventArgs args)
            => await SendMessage(new RoleCreatedDTO(new DiscordRoleDTO(args.Role.Id.ToString(), args.Role.Name, (byte)args.Role.Position)), aRoutingKey: "mandril.roles.sync");

        #endregion

        #region Helpers
        private async Task SendMessage(object aMessage, string? aRoutingKey = default)
        {
            using var lScope = _serviceScopeFactory.CreateScope();
            var lIntegrationMessagePublisher = lScope.ServiceProvider.GetRequiredService<IIntegrationMessagePublisher>();
            await lIntegrationMessagePublisher.Publish(aMessage, routingKey: aRoutingKey);
        }

        private async Task SendIfGuildMemberRoleUpdate(GuildMemberUpdateEventArgs aGuildMemberUpdateEventArgs)
        {
            var lAddedRoles = aGuildMemberUpdateEventArgs.RolesAfter.Except(aGuildMemberUpdateEventArgs.RolesBefore).ToList();
            var lRemovedRoles = aGuildMemberUpdateEventArgs.RolesBefore.Except(aGuildMemberUpdateEventArgs.RolesAfter).ToList();

            if (lAddedRoles.Any())
                await SendMessage(new MemberRoleAssignedDTO(aGuildMemberUpdateEventArgs.MemberAfter.Id.ToString(), lAddedRoles.Select(role => role.ToDto()).ToArray()), aRoutingKey: "mandril.members.sync");
            if (lRemovedRoles.Any())
                await SendMessage(new MemberRoleRevokedDTO(aGuildMemberUpdateEventArgs.MemberAfter.Id.ToString(), lRemovedRoles.Select(role => role.ToDto()).ToArray()), aRoutingKey: "mandril.members.sync");
        }

        private async Task SendIfGuildMemberDisplayNameUpdate(GuildMemberUpdateEventArgs aGuildMemberUpdateEventArgs)
        {
            //TO-DO: GSWB-46
            if (aGuildMemberUpdateEventArgs.NicknameAfter != aGuildMemberUpdateEventArgs.NicknameBefore
                || aGuildMemberUpdateEventArgs.UsernameAfter != aGuildMemberUpdateEventArgs.UsernameBefore)
                await SendMessage(new MemberRenameDTO(aGuildMemberUpdateEventArgs.MemberAfter.Id.ToString(), aGuildMemberUpdateEventArgs.MemberAfter.DisplayName), aRoutingKey: "mandril.members.sync");
        }

        private async Task SendIfGuildMemberAvatarUpdate(GuildMemberUpdateEventArgs aGuildMemberUpdateEventArgs)
        {
            //TO-DO: GSWB-28
            if (aGuildMemberUpdateEventArgs.GuildAvatarHashAfter != aGuildMemberUpdateEventArgs.GuildAvatarHashBefore
                || aGuildMemberUpdateEventArgs.AvatarHashAfter != aGuildMemberUpdateEventArgs.AvatarHashBefore)
                await SendMessage(new MemberAvatarUpdateDTO(aGuildMemberUpdateEventArgs.MemberAfter.Id.ToString(), aGuildMemberUpdateEventArgs.MemberAfter.GetGuildAvatarUrlOrDefault()), aRoutingKey: "mandril.members.sync");
        }

        #endregion

    }
}
