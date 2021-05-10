﻿using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using DiscordBot.Extensions;

// ReSharper disable all UnusedMember.Local
namespace DiscordBot.Modules
{
    public class TicketModule : ModuleBase {
        private Settings.Deserialized.Settings _settings;

        public TicketModule (Settings.Deserialized.Settings settings) {
            _settings = settings;
        }

        /// <summary>
        /// Creates a private channel only accessable by the mods, admins, and the user who used the command.
        ///
        /// One command, no args, simple.
        /// </summary>
        [Command("complain"), Alias("complains", "complaint"), Summary("Opens a private channel to complain. Syntax : !complain")]
        public async Task Complaint()
        {
            await Context.Message.DeleteAsync();

            var categoryExist = (await Context.Guild.GetCategoriesAsync()).Any(category => category.Id == _settings.ComplaintCategoryId);

            var hash = Context.User.Id.ToString().GetSHA256().Substring(0, 8);
            var channelName = ParseToDiscordChannel($"{_settings.ComplaintChannelPrefix}-{hash}");

            var channels = await Context.Guild.GetChannelsAsync();
            // Check if channel with same name already exist in the Complaint Category (if it exists).
            if (channels.Any(channel => channel.Name == channelName && (!categoryExist || ((INestedChannel) channel).CategoryId == _settings.ComplaintCategoryId)))
            {
                await ReplyAsync($"{Context.User.Mention}, you already have an open complaint! Please use that channel!")
                    .DeleteAfterSeconds(15);
                return;
            }

            var newChannel = await Context.Guild.CreateTextChannelAsync(channelName, x => {
                if (categoryExist) x.CategoryId = _settings.ComplaintCategoryId;
            });

            var userPerms = new OverwritePermissions(viewChannel: PermValue.Allow);
            await newChannel.AddPermissionOverwriteAsync(Context.Guild.EveryoneRole, new OverwritePermissions(viewChannel: PermValue.Deny));
            await newChannel.AddPermissionOverwriteAsync(Context.User, userPerms);
            await newChannel.AddPermissionOverwriteAsync(Context.Guild.Roles.First(r => r.Id == _settings.ModeratorRoleId), userPerms);
            await newChannel.AddPermissionOverwriteAsync(Context.Client.CurrentUser, userPerms);

            await newChannel.SendMessageAsync(
                $"The content of this conversation will stay strictly between you {Context.User.Mention} and the staff.\n" +
                "Please stay civil, any insults or offensive language could see you punished.\n" +
                "Do not ping anyone and wait until a staff member is free to examine your complaint.");
            await newChannel.SendMessageAsync($"A staff member will be able to close this chat by doing !close.");

            /*await newChannel.SendMessageAsync(
                $"{Context.User.Mention}, this is your chat to voice your complaint to the staff members. When everything is finished between you and the staff, please do !close!");*/
        }

        /// <summary>
        /// Archives the ticket.
        /// </summary>
        [Command("close"), Alias("end", "done", "bye", "archive"), Summary("Closes the ticket")]
        [RequireModerator]
        public async Task Close()
        {
            await Context.Message.DeleteAsync();

            if (!Context.Channel.Name.StartsWith(_settings.ComplaintChannelPrefix.ToLower())) return;

            var categoryExist = (await Context.Guild.GetCategoriesAsync()).Any(category => category.Id == _settings.ClosedComplaintCategoryId);

            var currentChannel = await Context.Guild.GetChannelAsync(Context.Channel.Id);

            // Remove the override permissions for the user who opened the complaint.
            foreach (var a in currentChannel.PermissionOverwrites) {
                if (a.TargetType != PermissionTarget.User) continue;

                var user = await Context.Guild.GetUserAsync(a.TargetId);
                await currentChannel.RemovePermissionOverwriteAsync(user);
            }

            await currentChannel.ModifyAsync(x => {
                if (categoryExist) x.CategoryId = _settings.ClosedComplaintCategoryId;
                x.Name = _settings.ClosedComplaintChannelPrefix + x.Name;
            });

        }

        /// <summary>
        /// Delete the ticket.
        /// </summary>
        [Command("delete"), Summary("Deletes the ticket")]
        [RequireAdmin]
        private async Task Delete()
        {
            await Context.Message.DeleteAsync();

            if (Context.Channel.Name.StartsWith(_settings.ComplaintChannelPrefix.ToLower()) ||
                Context.Channel.Name.StartsWith(_settings.ClosedComplaintChannelPrefix.ToLower()))
            {
                await Context.Guild.GetChannelAsync(Context.Channel.Id).Result.DeleteAsync();
            }
        }

        private string ParseToDiscordChannel(string channelName) => channelName.ToLower().Replace(" ", "-");
    }
}
