using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using DiscordBot.Extensions;
using DiscordBot.Services;
using DiscordBot.Settings.Deserialized;
using DiscordBot.Utils;
using DiscordBot.Utils.Attributes;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace DiscordBot.Modules
{
    public class UserModule : ModuleBase
    {
        private List<string> _commandList;

        private static Settings.Deserialized.Settings _settings;
        private readonly CurrencyService _currencyService;
        private readonly DatabaseService _databaseService;
        private readonly PublisherService _publisherService;

        private readonly Rules _rules;
        private readonly UpdateService _updateService;
        private readonly UserService _userService;

        private readonly Random _random;

        public UserModule(IServiceProvider serviceProvider, Settings.Deserialized.Settings settings, Rules rules)
        {
            _databaseService = serviceProvider.GetService<DatabaseService>();
            _userService = serviceProvider.GetService<UserService>();
            _publisherService = serviceProvider.GetService<PublisherService>();
            _updateService = serviceProvider.GetService<UpdateService>();
            _currencyService = serviceProvider.GetService<CurrencyService>();
            _rules = rules;
            _settings = settings;

            _random = new Random();

            var commandHandlingService = serviceProvider.GetService<CommandHandlingService>();
            Task.Run(async () =>
            {
                var commands = await commandHandlingService.GetCommandList("UserModule", false, true, false);
                //TODO Work out a decent way to remove duplicates?
                _commandList = commands.MessageSplitToSize();
            });
        }

        [Command("Help"), Priority(100)]
        [Summary("Does what you see now.")]
        [Alias("command", "commands")]
        public async Task DisplayHelp()
        {
            if (Context.Channel.Id != _settings.BotCommandsChannel.Id)
            {
                try
                {
                    foreach (var message in _commandList)
                    {
                        await Context.User.SendMessageAsync(message);
                    }
                }
                catch (Exception)
                {
                    await ReplyAsync($"Your direct messages are disabled, please use <#{_settings.BotCommandsChannel.Id}> instead!").DeleteAfterSeconds(10);
                }
            }
            else
            {
                foreach (var message in _commandList)
                {
                    await ReplyAsync(message);
                }
            }
            await Context.Message.DeleteAsync();
        }
        #region Quote

        [Command("Quote"), HideFromHelp]
        public async Task QuoteMessageCommand(IMessageChannel channel, ulong messageId)
        {
            await QuoteMessage(messageId: messageId, channel: channel);
        }

        [Command("Quote"), Priority(10)]
        [Summary("Quote a message. Syntax : !quote messageid (#channel)")]
        public async Task QuoteMessageCommand(ulong messageId, ulong channel)
        {
            // Get channel, if channel doesn't exist, we try get channel from messageID
            IMessageChannel targetChannel = (IMessageChannel)await Context.Client.GetChannelAsync(channel) ?? (IMessageChannel)await Context.Client.GetChannelAsync(messageId);
            if (targetChannel == null)
            {
                await ReplyAsync("Channel or MessageID does not exist").DeleteAfterSeconds(seconds: 5);
                return;
            }

            if (targetChannel.Id == channel)
                await QuoteMessage(messageId, targetChannel);
            else
                await QuoteMessage(channel, targetChannel);
        }

        [Command("Quote"), HideFromHelp]
        [Summary("Quote a message. Syntax : !quote messageid (#channel)")]
        public async Task QuoteMessage(ulong messageId, IMessageChannel channel = null)
        {
            // If channel is null use Context.Channel, else use the provided channel
            channel ??= Context.Channel;
            var message = await channel.GetMessageAsync(messageId);
            if (message == null)
            {
                await Context.Message.DeleteAfterSeconds(seconds: 1);
                await ReplyAsync("No message with that id found.").DeleteAfterSeconds(seconds: 4);
                return;
            }
            if (message.Author.IsBot) // Can't imagine we need to quote the bots
            {
                await Context.Message.DeleteAfterSeconds(seconds: 2);
                return;
            }

            var messageLink = "https://discordapp.com/channels/" + Context.Guild.Id + "/" + channel.Id + "/" + messageId;

            var msgContent = message.Content;

            if (msgContent != null)
            {
                msgContent = msgContent.Truncate(1020);

                // Searches for embed links such as [Google](https://bing.com/)
                var regex = new Regex(@"\[([^\[\]\(\)]*)\]\((.*?)\)");

                var matches = regex.Matches(msgContent);

                foreach (var match in matches as IEnumerable<Match>)
                {
                    msgContent = msgContent.Replace(match.Value, $"\\{match.Value}");
                }
            }

            var msgAttachment = string.Empty;
            if (message.Attachments?.Count > 0) msgAttachment = "\t📸";
            var builder = new EmbedBuilder()
                          .WithColor(new Color(200, 128, 128))
                          .WithTimestamp(message.Timestamp)
                          .WithFooter(footer =>
                          {
                              footer
                                  .WithText($"Quoted by {Context.User.Username}#{Context.User.Discriminator} • From channel {message.Channel.Name}")
                                  .WithIconUrl(Context.User.GetAvatarUrl());
                          })
                          .WithAuthor(author =>
                          {
                              author
                                  .WithName(message.Author.Username)
                                  .WithIconUrl(message.Author.GetAvatarUrl());
                          });
            if (msgContent == string.Empty && msgAttachment != string.Empty) msgContent = "📸";

            msgContent += $"\n\n***[Linkback]({messageLink})***";
            builder.Description = msgContent;

            await ReplyAsync(embed: builder.Build());
            await Context.Message.DeleteAfterSeconds(1.0);
        }
        #endregion

        /* Not really a required feature of the bot?
        [Command("compile")]
        [Summary("Try to compile a snippet of C# code. Be sure to escape your strings. Syntax : !compile \"Your code\"")]
        [Alias("code", "compute", "assert")]
        public async Task CompileCode(params string[] code)
        {
            var codeComplete = Resources.PaizaCodeTemplate.Replace("{code}", string.Join(" ", code));

            var parameters = new Dictionary<string, string> {{"source_code", codeComplete}, {"language", "csharp"}, {"api_key", "guest"}};

            var content = new FormUrlEncodedContent(parameters);

            var message = await ReplyAsync(
                $"Please wait a moment, trying to compile your code interpreted as\n {codeComplete.AsCodeBlock()}");

            using (var client = new HttpClient())
            {
                var httpResponse = await client.PostAsync("https://api.paiza.io/runners/create", content);
                var response = JsonConvert.DeserializeObject<Dictionary<string, string>>(await httpResponse.Content.ReadAsStringAsync());

                var id = response["id"];
                string status;
                var startTime = DateTime.Now;
                const int maxTime = 30;

                do
                {
                    httpResponse = await client.GetAsync($"http://api.paiza.io/runners/get_details?id={id}&api_key=guest");
                    response = JsonConvert.DeserializeObject<Dictionary<string, string>>(await httpResponse.Content.ReadAsStringAsync());
                    status = response["status"];
                    await Task.Delay(300);
                } while (status != "completed" && (DateTime.Now - startTime).TotalSeconds < maxTime);

                string newMessage;

                if (status != "completed")
                {
                    newMessage = (message.Content + "The code didn't compile in time.").Truncate(1990);
                    await message.ModifyAsync(m => m.Content = newMessage);
                    return;
                }

                var buildStddout = response["build_stdout"];
                var stdout = response["stdout"];
                var stderr = response["stderr"];
                var buildStderr = response["build_stderr"];
                var result = response["build_result"];

                string fullMessage;
                if (result == "failure")
                {
                    fullMessage = message.Content + "The code resulted in a failure.\n";
                    fullMessage += buildStddout.Length > 0 ? buildStddout.AsCodeBlock() : string.Empty;
                    fullMessage += buildStderr.Length > 0 ? buildStderr.AsCodeBlock() : string.Empty;
                }
                else
                {
                    fullMessage = message.Content + "Result : ";
                    fullMessage += stdout.Length > 0 ? stdout.AsCodeBlock() : string.Empty;
                    fullMessage += stderr.Length > 0 ? stderr.AsCodeBlock() : string.Empty;
                }

                httpResponse = await client.PostAsync("https://hastebin.com/documents", new StringContent(fullMessage.Truncate(10000)));
                response = JsonConvert.DeserializeObject<Dictionary<string, string>>(await httpResponse.Content.ReadAsStringAsync());

                newMessage = ($"\nFull result : https://hastebin.com/{response["key"]}\n" + fullMessage).Truncate(1990) + "```";
                await message.ModifyAsync(m => m.Content = newMessage);
            }
        }
        */

        [Command("Ping"), Priority(98)]
        [Summary("Bot latency.")]
        [Alias("pong")]
        public async Task Ping()
        {
            var message = await ReplyAsync("Pong");
            var time = message.CreatedAt.Subtract(Context.Message.Timestamp);
            await message.ModifyAsync(m =>
                m.Content = $"Pong (**{time.TotalMilliseconds}** *ms* / gateway **{_userService.GetGatewayPing()}** *ms*)");
            await message.DeleteAfterTime(seconds: 10);

            await Context.Message.DeleteAfterTime(seconds: 5);
        }

        [Command("Members"), Priority(90)]
        [Summary("Current member count.")]
        [Alias("MemberCount")]
        public async Task MemberCount()
        {
            await ReplyAsync(
                $"We currently have {(await Context.Guild.GetUsersAsync()).Count - 1} members. Let's keep on growing as the strong community we are :muscle:");
        }

        [Command("ChristmasCompleted"), HideFromHelp]
        [Summary("Reward for christmas event.")]
        public async Task UserCompleted(string message)
        {
            //Make sure they're the santa bot
            if (Context.Message.Author.Id != 514979161144557600L) return;

            if (!long.TryParse(message, out var userId))
            {
                await ReplyAsync("Invalid user id");
                return;
            }

            const uint xpGain = 5000;
            var userXp = await _databaseService.Query().GetXp(userId.ToString());
            await _databaseService.Query().UpdateXp(userId.ToString(), userXp + xpGain);
            await Context.Message.DeleteAsync();
        }

        [Group("Role"), BotChannelOnly]
        public class RoleModule : ModuleBase
        {
            private readonly ILoggingService _logging;

            public RoleModule(ILoggingService logging)
            {
                _logging = logging;
            }

            [Command("Add")]
            [Summary("Add a role to yourself. Syntax : !role add role")]
            public async Task AddRoleUser(IRole role)
            {
                if (!_settings.UserAssignableRoles.Roles.Contains(role.Name))
                {
                    await ReplyAsync("This role is not assignable");
                    return;
                }

                var u = Context.User as IGuildUser;

                await u.AddRoleAsync(role);
                await ReplyAsync($"{u.Username} you now have the `{role.Name}` role.");
                await _logging.LogAction($"{Context.User.Username} has added {role} to themself.");
            }

            [Command("Remove")]
            [Summary("Remove a role from yourself. Syntax : !role remove role")]
            [Alias("delete")]
            public async Task RemoveRoleUser(IRole role)
            {
                if (!_settings.UserAssignableRoles.Roles.Contains(role.Name))
                {
                    await ReplyAsync("Role is not assigneable");
                    return;
                }

                var u = Context.User as IGuildUser;

                await u.RemoveRoleAsync(role);
                await ReplyAsync($"{u.Username} your `{role.Name}` role has been removed");
                await _logging.LogAction($"{Context.User.Username} has removed role {role} from themself.");
            }

            [Command("List")]
            [Summary("List of available roles. Syntax : !role list")]
            public async Task ListRole()
            {
                await ReplyAsync("**The following roles are available on this server** :\n" +
                                 "We offer multiple roles to show what you specialize in, whether it's professionally or as a hobby, so if there's something you're good at, assign the corresponding role! \n" +
                                 "You can assign as much roles as you want, but try to keep them for what you're good at :) \n");
                await ReplyAsync(
                    "```!role add/remove 2D-Artists - If you're good at drawing, painting, digital art, concept art or anything else that's flat. \n" +
                    "!role add/remove 3D-Artists - If you are a wizard with vertices or like to forge your models from mud. \n" +
                    "!role add/remove Animators - If you like to bring characters to life. \n" +
                    "!role add/remove Technical-Artists - If you write tools and shaders to bridge the gap between art and programming. \n" +
                    "!role add/remove Programmers - If you like typing away to make your dreams come true (or the code come to your dreams). \n" +
                    "!role add/remove Game-Designers - If you are good at designing games, mechanics and levels.\n" +
                    "!role add/remove Audio-Engineers - If you live life to the rhythm of your own music and sounds.\n" +
                    "!role add/remove Generalists - If you like to dabble in everything.\n" +
                    "!role add/remove Hobbyists - If you're using Unity as a hobby.\n" +
                    "!role add/remove Students - If you're currently studying in a game-dev related field. \n" +
                    "!role add/remove XR-Developers - If you're a VR, AR or MR sorcerer. \n" +
                    "!role add/remove Writers - If you like writing lore, scenarios, characters and stories. \n" +
                    "```");
                await ReplyAsync("```To get the publisher role type **!pinfo** and follow the instructions.```\n");
            }
        }
        #region Rules

        [Command("Rules"), Priority(1)]
        [Summary("Rules of current channel by DM.")]
        public async Task Rules()
        {
            await Rules(Context.Channel);
            await Context.Message.DeleteAsync();
        }

        [Command("Rules"), Priority(99)]
        [Summary("Rules of the mentioned channel by DM. !rules #channel")]
        [Alias("rule")]
        public async Task Rules(IMessageChannel channel)
        {
            var rule = _rules.Channel.First(x => x.Id == channel.Id);
            var dm = await Context.User.CreateDMChannelAsync();
            bool sentMessage = false;

            sentMessage = await dm.TrySendMessage($"{rule.Header}{(rule.Content.Length > 0 ? rule.Content : $"There is no special rule for {channel.Name} channel.\nPlease follow global rules (you can get them by typing `!globalrules`)")}");
            if (!sentMessage)
                await ReplyAsync($"Could not send rules, your DMs are disabled.").DeleteAfterSeconds(seconds: 10);
        }

        [Command("GlobalRules"), Priority(99)]
        [Summary("Global Rules by DM.")]
        public async Task GlobalRules(int seconds = 60)
        {
            var globalRules = _rules.Channel.First(x => x.Id == 0).Content;
            var dm = await Context.User.CreateDMChannelAsync();
            await Context.Message.DeleteAsync();
            if (!await dm.TrySendMessage(globalRules))
            {
                await ReplyAsync($"Could not send rules, your DMs are disabled.").DeleteAfterSeconds(seconds: 10);
            }
        }

        [Command("Welcome"), Priority(1)]
        [Summary("Condensed version of the rules and links to quality resources.")]
        public async Task ServerWelcome()
        {
            if (!await _userService.DMFormattedWelcome(Context.User as SocketGuildUser))
            {
                await ReplyAsync("Could not send welcome, your DM's are disabled.").DeleteAfterSeconds(seconds: 2);
            }
            await Context.Message.DeleteAfterSeconds(seconds: 4);
        }

        [Command("Channels"), Priority(92)]
        [Summary("Description of the channels by DM.")]
        public async Task ChannelsDescription()
        {
            //Display rules of this channel for x seconds
            var channelData = _rules.Channel;
            var sb = new StringBuilder();
            foreach (var c in channelData)
                sb.Append((await Context.Guild.GetTextChannelAsync(c.Id))?.Mention).Append(" - ").Append(c.Header).Append("\n");

            var dm = await Context.User.CreateDMChannelAsync();

            var messages = sb.ToString().MessageSplitToSize();
            await Context.Message.DeleteAsync();
            foreach (var message in messages)
            {
                if (!await dm.TrySendMessage(message))
                {
                    await ReplyAsync($"Could not send channel descriptions, your DMs are disabled.").DeleteAfterSeconds(seconds: 10);
                    break;
                }
            }
        }

        #endregion

        #region XP & Karma

        [Command("Karma"), Priority(95)]
        [Summary("Description of what Karma is.")]
        public async Task KarmaDescription(int seconds = 60)
        {
            await ReplyAsync($"{Context.User.Username}, Karma is tracked on your !profile which helps indicate how much you've helped others and provides a small increase in EXP gain.");
            await Context.Message.DeleteAfterSeconds(seconds: seconds);
        }

        [Command("Top"), Priority(6)]
        [Summary("Display top 10 users by level.")]
        [Alias("toplevel", "ranking")]
        public async Task TopLevel()
        {
            var users = await _databaseService.Query().GetTopLevel(10);
            var userList = users.Select(user => (ulong.Parse(user.UserID), user.Level)).ToList();

            var embed = await GenerateRankEmbedFromList(userList, "Level");
            await ReplyAsync(embed: embed).DeleteAfterTime(minutes: 1);
        }

        [Command("TopKarma"), Priority(5)]
        [Summary("Display top 10 users by karma.")]
        [Alias("karmarank", "rankingkarma", "topk")]
        public async Task TopKarma()
        {
            var users = await _databaseService.Query().GetTopKarma(10);
            var userList = users.Select(user => (ulong.Parse(user.UserID), user.Karma)).ToList();

            var embed = await GenerateRankEmbedFromList(userList, "Karma");
            await ReplyAsync(embed: embed).DeleteAfterTime(minutes: 1);
        }

        [Command("TopKarmaWeekly"), Priority(5)]
        [Summary("Display weekly top 10 users by karma.")]
        [Alias("karmarankweekly", "rankingkarmaweekly", "topkw")]
        public async Task TopKarmaWeekly()
        {
            var users = await _databaseService.Query().GetTopKarmaWeekly(10);
            var userList = users.Select(user => (ulong.Parse(user.UserID), user.KarmaWeekly)).ToList();

            var embed = await GenerateRankEmbedFromList(userList, "Weekly Karma");
            await ReplyAsync(embed: embed).DeleteAfterTime(minutes: 1);
        }

        [Command("TopKarmaMonthly"), Priority(5)]
        [Summary("Display monthly top 10 users by karma.")]
        [Alias("karmarankmonthly", "rankingkarmamonthly", "topkm")]
        public async Task TopKarmaMonthly()
        {
            var users = await _databaseService.Query().GetTopKarmaMonthly(10);
            var userList = users.Select(user => (ulong.Parse(user.UserID), user.KarmaMonthly)).ToList();

            var embed = await GenerateRankEmbedFromList(userList, "Monthly Karma");
            await ReplyAsync(embed: embed).DeleteAfterTime(minutes: 1);
        }

        [Command("TopKarmaYearly"), Priority(5)]
        [Summary("Display tearly top 10 users by karma.")]
        [Alias("karmaranktearly", "rankingkarmayearly", "topky")]
        public async Task TopKarmaYearly()
        {
            var users = await _databaseService.Query().GetTopKarmaYearly(10);
            var userList = users.Select(user => (ulong.Parse(user.UserID), user.KarmaYearly)).ToList();

            var embed = await GenerateRankEmbedFromList(userList, "Yearly Karma");
            await ReplyAsync(embed: embed).DeleteAfterTime(minutes: 1);
        }

        private async Task<Embed> GenerateRankEmbedFromList(List<(ulong userID, uint value)> data, string labelName)
        {
            var embedBuilder = new EmbedBuilder();
            embedBuilder.Title = $"Top 10 Users by {labelName}";
            embedBuilder.Footer = new EmbedFooterBuilder();
            embedBuilder.Footer.Text = $"The best of the best, by {labelName}.";

            var maxUsernameLength = data
            .Select(async x => await Context.Guild.GetUserAsync(x.userID))
            .Select(x => x.Result)
            .Max(x => (x?.Username ?? "Unknown User").Length);

            try
            {
                var str = "";
                for (var i = 0; i < data.Count; i++)
                {
                    var user = await Context.Guild.GetUserAsync(data[i].userID);
                    var username = user?.Username ?? "Unknown User"; // For cases where the user has left the guild
                    int rankPadding = (int)Math.Floor(Math.Log10(data.Count));

                    str += $"`{(i + 1).ToString().PadLeft(rankPadding + 1)}.` **`{username.PadRight(maxUsernameLength, '\u2000')}`** `{labelName}: {data[i].value}`\n";
                }
                embedBuilder.Description = str;

            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
            }

            return embedBuilder.Build();
        }

        [Command("Profile"), Priority(2)]
        [Summary("Display your profile card.")]
        public async Task DisplayProfile()
        {
            await DisplayProfile(Context.Message.Author);
        }

        [Command("Profile"), Priority(2)]
        [Summary("Display profile card of mentioned user. Syntax : !profile @user")]
        public async Task DisplayProfile(IUser user)
        {
            try
            {
                var profile = await Context.Channel.SendFileAsync(await _userService.GenerateProfileCard(user));

                await Context.Message.DeleteAfterSeconds(seconds: 1);
                await profile.DeleteAfterTime(minutes: 3);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
            }
        }

        [Command("JoinDate"), Priority(91)]
        [Summary("Display date you joined the server.")]
        public async Task JoinDate()
        {
            var userId = Context.User.Id;
            var joinDate = ((IGuildUser)Context.User).JoinedAt;
            await ReplyAsync($"{Context.User.Mention} you joined **{joinDate:dddd dd/MM/yyy HH:mm:ss}**");
            await Context.Message.DeleteAsync();
        }

        #endregion

        #region Codetips

        [Command("CodeTip"), Priority(20)]
        [Summary("Show code formatting example. Syntax : !codetip userToPing(optional)")]
        [Alias("codetips")]
        public async Task CodeTip(IUser user = null)
        {
            var message = user != null ? user.Mention + ", " : "";
            message += "When posting code, format it like so:" + Environment.NewLine;
            message += _userService.CodeFormattingExample;
            await Context.Message.DeleteAsync();
            await ReplyAsync(message).DeleteAfterSeconds(seconds: 60);
        }

        [Command("DisableCodeTips"), Priority(91)]
        [Summary("Stops code formatting reminders.")]
        public async Task DisableCodeTips()
        {
            await Context.Message.DeleteAsync();
            if (!_userService.CodeReminderCooldown.IsPermanent(Context.User.Id))
            {
                _userService.CodeReminderCooldown.SetPermanent(Context.User.Id, true);
                await ReplyAsync($"{Context.User.Username}, " + "You will no longer be reminded about correct code formatting.").DeleteAfterTime(20);
            }
        }

        #endregion

        #region Fun
        [Command("Slap"), Priority(21)]
        [Summary("Slap the specified user(s). Syntax : !slap @user1 [@user2 @user3...]")]
        public async Task SlapUser(params IUser[] users)
        {
            var sb = new StringBuilder();

            sb.Append("**").Append(Context.User.Username).Append("** Slaps ");
            foreach (var user in users) sb.Append(user.Mention).Append(" ");

            sb.Append("around a bit with a large ").Append(_settings.UserModuleSlapChoices[_random.Next() % _settings.UserModuleSlapChoices.Count]).Append(".");

            await Context.Channel.SendMessageAsync(sb.ToString());
            await Context.Message.DeleteAfterSeconds(seconds: 1);
        }

        [Command("CoinFlip"), Priority(22)]
        [Summary("Flip a coin and see the result.")]
        [Alias("flipcoin")]
        public async Task CoinFlip()
        {
            var coin = new[] { "Heads", "Tails" };

            await ReplyAsync($"**{Context.User.Username}** flipped a coin and got **{coin[_random.Next() % 2]}**!");
            await Context.Message.DeleteAfterSeconds(seconds: 1);
        }

        #endregion

        #region Publisher

        [Command("PInfo"), BotChannelOnly, Priority(11)]
        [Summary("Information on how to get publisher role.")]
        [Alias("publisherinfo")]
        public async Task PublisherInfo()
        {
            var builder = new EmbedBuilder()
                .WithTitle("Publisher Commands")
                .WithDescription("Use these commands to get the **Asset-Publisher** role.")
                .AddField("1️⃣  `!publisher <ID>`", "Example: `!publisher 12345`.\nReceive a code on the email associated with your publisher account.\nTo get your ID: assetstore.unity.com/publishers/**YourID**.")
                .AddField("2️⃣  `!verify <ID> <code>`", "Example: `!publisher 12345 6789`.\nVerify your ID with the code sent to your email.");
            var embed = builder.Build();

            await ReplyAsync(embed: embed);
            await Context.Message.DeleteAfterSeconds(seconds: 2);
        }

        [Command("Publisher"), BotChannelOnly, HideFromHelp]
        [Summary("Get the Asset-Publisher role by verifying who you are. Syntax: !publisher publisherID")]
        public async Task Publisher(uint publisherId)
        {
            if (((SocketGuildUser)Context.Message.Author).Roles.Any(x => x.Id == _settings.PublisherRoleId))
            {
                await ReplyAsync($"{Context.Message.Author.Mention} you already have the `Asset-Publisher` role.");
            }
            else if (_settings.Email == string.Empty)
            {
                await ReplyAsync("The `Asset-Publisher` role is currently disabled.");
            }
            else
            {
                var verify = await _publisherService.VerifyPublisher(publisherId, Context.User.Username);
                await ReplyAsync(verify.Item2);
            }
            await Context.Message.DeleteAfterSeconds(seconds: 1);
        }

        [Command("Verify"), BotChannelOnly, HideFromHelp]
        [Summary("Verify a publisher with the code received by email. Syntax : !verify publisherId code")]
        public async Task VerifyPackage(uint packageId, string code)
        {
            await Context.Message.DeleteAfterSeconds(seconds: 0);
            var verif = await _publisherService.ValidatePackageWithCode(Context.Message.Author, packageId, code);
            await ReplyAsync(verif);
        }

        #endregion

        #region Search
        [Command("Search"), Priority(25)]
        [Summary("Searches DuckDuckGo for results. Syntax: !search c# lambda help")]
        [Alias("s", "ddg")]
        public async Task SearchResults(params string[] messages)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var msg in messages)
                sb.Append(msg).Append(" ");
            await SearchResults(sb.ToString());
        }

        [Command("Search"), HideFromHelp]
        [Summary("Searches DuckDuckGo for web results. Syntax : !search \"query\" resNum site")]
        [Alias("s", "ddg")]
        public async Task SearchResults(string query, uint resNum = 3, string site = "")
        {
            // Cleaning inputs from user (maybe we can ban certain domains or keywords)
            resNum = resNum <= 5 ? resNum : 5;
            var searchQuery = "https://duckduckgo.com/html/?q=" + query.Replace(' ', '+');

            if (site != string.Empty) searchQuery += "+site:" + site;

            var doc = new HtmlWeb().Load(searchQuery);
            var counter = 1;

            EmbedBuilder embedBuilder = new EmbedBuilder();
            embedBuilder.Title = $"Q: {WebUtility.UrlDecode(query)}";
            string resultTitle = string.Empty;

            // XPath for DuckDuckGo as of 10/05/2018, if results stop showing up, check this first!
            // Still working (13/05/21)
            foreach (var row in doc.DocumentNode.SelectNodes("/html/body/div[1]/div[3]/div/div/div[*]/div/h2/a"))
            {
                if (counter > resNum) break;

                // Seems to be some weird additional data attached to links. Fix added (13/05/21)
                row.Attributes["href"].Value = row.Attributes["href"].Value.Replace("//duckduckgo.com/l/?uddg=", string.Empty);

                // Check if we are within the allowed number of results and if the result is valid (i.e. no evil ads)
                if (counter <= resNum && IsValidResult(row)) // && IsValidResult(row))
                {
                    var url = WebUtility.UrlDecode(row.Attributes["href"].Value); // .Replace("/l/?kh=-1&amp;uddg=", "")); <- no longer works (14/05/21)

                    // We count how many & there are, as links with multiple may be broken, so we include a ~ just to try give a bit more info if there is more than 1.
                    int andCount = url.Count(c => c == '&');
                    url = url.Substring(0, url.LastIndexOf('&'));

                    resultTitle += $"{counter}. {(row.InnerText.Length > 60 ? $"{row.InnerText[..60]}.." : row.InnerText)}" + $" [__Read More..__{(andCount > 1 ? "~" : string.Empty)}]({url})\n";

                    counter++;
                }
            }

            embedBuilder.AddField("Search Query", searchQuery);
            embedBuilder.AddField("Results", resultTitle, inline: false);

            embedBuilder.Color = new Color(81, 50, 169);
            embedBuilder.Footer = new EmbedFooterBuilder().WithText("Results sourced from DuckDuckGo.");

            var embed = embedBuilder.Build();
            await ReplyAsync(embed: embed);
        }

        // Utility function for avoiding evil ads from DuckDuckGo
        bool IsValidResult(HtmlNode node)
        {
            return (!node.Attributes["href"].Value.Contains("duckduckgo.com") &&
                    !node.Attributes["href"].Value.Contains("duck.co"));
        }

        [Command("Manual"), Priority(8)]
        [Summary("Searches Unity3D manual for results. Syntax : !manual \"query\"")]
        public async Task SearchManual(params string[] queries)
        {
            // Download Unity3D Documentation Database (lol)

            // Calculate the closest match to the input query
            var minimumScore = double.MaxValue;
            string[] mostSimilarPage = null;
            var pages = await _updateService.GetManualDatabase();
            var query = string.Join(" ", queries);
            foreach (var p in pages)
            {
                var curScore = CalculateScore(p[1], query);
                if (curScore < minimumScore)
                {
                    minimumScore = curScore;
                    mostSimilarPage = p;
                }
            }

            // If a page has been found (should be), return the message, else return information
            if (mostSimilarPage != null)
                await ReplyAsync($"** {mostSimilarPage[1]} **\nRead More: https://docs.unity3d.com/Manual/{mostSimilarPage[0]}.html");
            else
                await ReplyAsync("No Results Found.").DeleteAfterSeconds(seconds: 10);
        }

        [Command("Doc"), Priority(9)]
        [Summary("Searches Unity3D API for results. Syntax : !api \"query\"")]
        [Alias("ref", "reference", "api", "docs")]
        public async Task SearchApi(params string[] queries)
        {
            // Download Unity3D Documentation Database (lol)

            // Calculate the closest match to the input query
            var minimumScore = double.MaxValue;
            string[] mostSimilarPage = null;
            var pages = await _updateService.GetApiDatabase();
            var query = string.Join(" ", queries);
            foreach (var p in pages)
            {
                var curScore = CalculateScore(p[1], query);
                if (curScore < minimumScore)
                {
                    minimumScore = curScore;
                    mostSimilarPage = p;
                }
            }

            // If a page has been found (should be), return the message, else return information
            if (mostSimilarPage != null)
                await ReplyAsync(
                    $"** {mostSimilarPage[1]} **\nRead More: https://docs.unity3d.com/ScriptReference/{mostSimilarPage[0]}.html");
            else
                await ReplyAsync("No Results Found.").DeleteAfterSeconds(seconds: 10);
        }

        private double CalculateScore(string s1, string s2)
        {
            double curScore = 0;
            var i = 0;

            foreach (var q in s1.Split(' '))
            {
                foreach (var x in s2.Split(' '))
                {
                    i++;
                    if (x.Equals(q))
                        curScore -= 50;
                    else
                        curScore += x.CalculateLevenshteinDistance(q);
                }
            }

            curScore /= i;
            return curScore;
        }

        [Command("FAQ")]
        [Summary("Searches UDC FAQs. Syntax : !faq \"query\"")]
        public async Task SearchFaqs(params string[] queries)
        {
            var faqDataList = _updateService.GetFaqData();

            // Check if query is faq ID (e.g. "!faq 1")
            if (queries.Length == 1 && ParseNumber(queries[0]) > 0)
            {
                var id = ParseNumber(queries[0]) - 1;
                if (id < faqDataList.Count)
                    await ReplyAsync(embed: GetFaqEmbed(faqDataList[id]));
                else
                    await ReplyAsync("Invalid FAQ ID selected.");
            }
            // Check if query contains "list" command (i.e. "!faq list")
            else if (queries.Length > 0 && !(queries.Length == 1 && queries[0].Equals("list")))
            {
                // Calculate the closest match to the input query
                var minimumScore = double.MaxValue;
                FaqData mostSimilarFaq = null;
                var query = string.Join(" ", queries);

                // Go through each FAQ in the list and check the most similar
                foreach (var faq in faqDataList)
                {
                    foreach (var keyword in faq.Keywords)
                    {
                        var curScore = CalculateScore(keyword, query);
                        if (curScore < minimumScore)
                        {
                            minimumScore = curScore;
                            mostSimilarFaq = faq;
                        }
                    }
                }

                // If an FAQ has been found (should be), return the FAQ, else return information msg
                if (mostSimilarFaq != null)
                    await ReplyAsync(embed: GetFaqEmbed(mostSimilarFaq));
                else
                    await ReplyAsync("No FAQs Found.");
            }
            else
                // List all the FAQs available
                await ListFaqs(faqDataList);
        }

        private async Task ListFaqs(List<FaqData> faqs)
        {
            var sb = new StringBuilder(faqs.Count);
            var index = 1;
            var keywordSb = new StringBuilder();
            foreach (var faq in faqs)
            {
                sb.Append(FormatFaq(index, faq) + "\n");
                keywordSb.Append("[");
                for (var i = 0; i < faq.Keywords.Length; i++)
                {
                    keywordSb.Append(faq.Keywords[i]);
                    keywordSb.Append(i < faq.Keywords.Length - 1 ? ", " : "]\n\n");
                }

                index++;
                sb.Append(keywordSb);
                keywordSb.Clear();
            }

            await ReplyAsync(sb.ToString()).DeleteAfterTime(minutes: 3);
        }

        private Embed GetFaqEmbed(FaqData faq)
        {
            var builder = new EmbedBuilder()
                          .WithTitle($"{faq.Question}")
                          .WithDescription($"{faq.Answer}")
                          .WithColor(new Color(0x33CC00));
            return builder.Build();
        }

        private string FormatFaq(int id, FaqData faq) => $"{id}. **{faq.Question}** - {faq.Answer}";

        [Command("Wiki"), Priority(26)]
        [Summary("Searches Wikipedia. Syntax : !wiki \"query\"")]
        [Alias("wikipedia")]
        public async Task SearchWikipedia([Remainder] string query)
        {
            var article = await _updateService.DownloadWikipediaArticle(query);

            // If an article is found return it, else return error message
            if (article.url == null)
            {
                await ReplyAsync($"No Articles for \"{query}\" were found.");
                return;
            }

            await ReplyAsync(embed: GetWikipediaEmbed(article.name, article.extract, article.url));
        }

        private Embed GetWikipediaEmbed(string subject, string articleExtract, string articleUrl)
        {
            var builder = new EmbedBuilder()
                          .WithTitle($"Wikipedia | {subject}")
                          .WithDescription($"{articleExtract}")
                          .WithUrl(articleUrl)
                          .WithColor(new Color(0x33CC00));
            return builder.Build();
        }

        private int ParseNumber(string s)
        {
            int id;
            if (int.TryParse(s, out id)) return id;

            return -1;
        }

        #endregion

        #region Birthday

        [Command("Birthday"), HideFromHelp]
        [Summary("Display next member birthday.")]
        [Alias("bday")]
        public async Task Birthday()
        {
            // URL to cell C15/"Next birthday" cell from Corn's google sheet
            var nextBirthday =
                "https://docs.google.com/spreadsheets/d/10iGiKcrBl1fjoBNTzdtjEVYEgOfTveRXdI5cybRTnj4/gviz/tq?tqx=out:html&range=C15:C15";
            var doc = new HtmlWeb().Load(nextBirthday);

            // XPath to the table row
            var row = doc.DocumentNode.SelectSingleNode("/html/body/table/tr[2]/td");
            var tableText = WebUtility.HtmlDecode(row.InnerText);
            var message = $"**{tableText}**";

            await ReplyAsync(message).DeleteAfterTime(minutes: 3);
            await Context.Message.DeleteAfterTime(minutes: 3);
        }

        [Command("Birthday"), Priority(27)]
        [Summary("Display birthday of mentioned user. Syntax : !birthday @user")]
        [Alias("bday")]
        public async Task Birthday(IUser user)
        {
            var searchName = user.Username;
            // URL to columns B to D of Corn's google sheet
            var birthdayTable =
                "https://docs.google.com/spreadsheets/d/10iGiKcrBl1fjoBNTzdtjEVYEgOfTveRXdI5cybRTnj4/gviz/tq?tqx=out:html&gid=318080247&range=B:D";
            var doc = new HtmlWeb().Load(birthdayTable);
            var birthdate = default(DateTime);

            HtmlNode matchedNode = null;
            var matchedLength = int.MaxValue;

            // XPath to each table row
            foreach (var row in doc.DocumentNode.SelectNodes("/html/body/table/tr"))
            {
                // XPath to the name column (C)
                var nameNode = row.SelectSingleNode("td[2]");
                var name = nameNode.InnerText;
                if (name.ToLower().Contains(searchName.ToLower()))
                    // Check for a "Closer" match
                    if (name.Length < matchedLength)
                    {
                        matchedNode = row;
                        matchedLength = name.Length;
                        // Nothing will match "Better" so we may as well break out
                        if (name.Length == searchName.Length) break;
                    }
            }

            if (matchedNode != null)
            {
                // XPath to the date column (B)
                var dateNode = matchedNode.SelectSingleNode("td[1]");
                // XPath to the year column (D)
                var yearNode = matchedNode.SelectSingleNode("td[3]");

                var provider = CultureInfo.InvariantCulture;
                var wrongFormat = "M/d/yyyy";
                //string rightFormat = "dd-MMMM-yyyy";

                var dateString = dateNode.InnerText;
                if (!yearNode.InnerText.Contains("&nbsp;")) dateString = dateString + "/" + yearNode.InnerText;

                dateString = dateString.Trim();

                try
                {
                    // Converting the birthdate from the wrong format to the right format WITH year
                    birthdate = DateTime.ParseExact(dateString, wrongFormat, provider);
                }
                catch (FormatException)
                {
                    // Converting the birthdate from the wrong format to the right format WITHOUT year
                    birthdate = DateTime.ParseExact(dateString, "M/d", provider);
                }
            }

            // Business as usual
            if (birthdate == default)
            {
                await ReplyAsync(
                        $"Sorry, I couldn't find **{searchName}**'s birthday date. They can add it at https://docs.google.com/forms/d/e/1FAIpQLSfUglZtJ3pyMwhRk5jApYpvqT3EtKmLBXijCXYNwHY-v-lKxQ/viewform !")
                    .DeleteAfterSeconds(30);
            }
            else
            {
                var date = birthdate.ToUnixTimestamp();
                var message =
                    $"**{searchName}**'s birthdate: **<t:{date}:D>** " +
                    $"({(int)((DateTime.Now - birthdate).TotalDays / 365)}yo)";

                await ReplyAsync(message).DeleteAfterTime(minutes: 3);
            }

            await Context.Message.DeleteAfterTime(minutes: 3);
        }

        #endregion

        #region temperatures

        [Command("FtoC"), Priority(28)]
        [Summary("Converts a temperature in fahrenheit to celsius. Syntax : !ftoc temperature")]
        public async Task FahrenheitToCelsius(float f)
        {
            await ReplyAsync($"{Context.User.Mention} {f}°F is {MathUtility.FahrenheitToCelsius(f)}°C.");
        }

        [Command("CtoF"), Priority(28)]
        [Summary("Converts a temperature in celsius to fahrenheit. Syntax : !ftoc temperature")]
        public async Task CelsiusToFahrenheit(float c)
        {
            await ReplyAsync($"{Context.User.Mention}  {c}°C is {MathUtility.CelsiusToFahrenheit(c)}°F");
        }

        #endregion

        #region Translate

        [Command("Translate"), HideFromHelp]
        [Summary("Translate a message. Syntax : !translate messageId language")]
        public async Task Translate(ulong messageId, string language = "en")
        {
            await Translate((await Context.Channel.GetMessageAsync(messageId)).Content, language);
        }

        [Command("Translate"), HideFromHelp]
        [Summary("Translate a message. Syntax : !translate text language")]
        public async Task Translate(string text, string language = "en")
        {
            var msg = await ReplyAsync($"Here: <https://translate.google.com/#auto/{language}/{text.Replace(" ", "%20")}>");
            await Context.Message.DeleteAfterSeconds(seconds: 1);
            await msg.DeleteAfterSeconds(seconds: 20);
        }

        #endregion

        #region Currency

        [Command("Currency"), HideFromHelp]
        [Summary("Converts a currency. Syntax : !currency fromCurrency toCurrency")]
        [Alias("curr")]
        public async Task ConvertCurrency(string from, string to)
        {
            await ConvertCurrency(1, from, to);
        }

        [Command("Currency"), Priority(29)]
        [Summary("Converts a currency. Syntax : !currency amount fromCurrency toCurrency")]
        [Alias("curr")]
        public async Task ConvertCurrency(double amount, string from, string to)
        {
            from = from.ToUpper();
            to = to.ToUpper();

            double fromRate = -1;
            double toRate = -1;
            try
            {
                // Get USD to fromCurrency rate
                fromRate = await _currencyService.GetRate(from);
                // Get USD to toCurrency rate
                toRate = await _currencyService.GetRate(to);
            }
            catch
            {
                var embedBuilder = new EmbedBuilder()
                    .WithTitle("Currency API Down")
                    .WithDescription(
                        "Check [API Status](https://www.currencyconverterapi.com/server-status), and try again in a few minutes.")
                    .WithColor(Color.Red);
                await ReplyAsync(embed: embedBuilder.Build()).DeleteAfterSeconds(seconds: 10);
                return;
            }

            if (!await CurrencyFailedResponse((from, fromRate.Equals(-1)), (to, toRate.Equals(-1))))
            {
                await Context.Message.DeleteAfterSeconds(seconds: 1);
                return;
            }
            // Convert fromCurrency amount to USD to toCurrency
            var value = Math.Round(toRate / fromRate * amount, 2);
            await ReplyAsync($"{Context.User.Mention}  **{amount} {from}** = **{value} {to}**");
        }

        private async Task<bool> CurrencyFailedResponse((string name, bool invalid) from, (string name, bool invalid) to)
        {
            if (from.invalid && to.invalid)
            {
                await ReplyAsync($"{Context.User.Mention}, {from.name} and {to.name} are invalid currencies or incorrectly used.\nPlease use international currency code (example : **USD** for $, **EUR** for €, **PKR** for pakistani rupee).");
                return false;
            }
            if (from.invalid)
            {
                await ReplyAsync($"{Context.User.Mention}, {from.name} is an invalid currency.");
                return false;
            }
            if (to.invalid)
            {
                await ReplyAsync($"{Context.User.Mention}, {to.name} is an invalid currency.");
                return false;
            }

            return true;
        }

        #endregion

        #region AutoThread

        [Command("Autothread close")]
        [Alias("Autothread archive", "Att close", "Att archive")]
        [Summary("Archive an auto-thread and rename it automatically according to channel-specific settings.")]
        [RequireArchivableAutoThread]
        [RequireAutoThreadAuthor(Group = "AuthorOrMod")]
        [RequireModerator(Group = "AuthorOrMod")]
        public async Task CloseAutoThread()
        {
            var currentThread = Context.Message.Channel as SocketThreadChannel;
            var autoTheadConfig = _settings.AutoThreadChannels.Find(x => currentThread.ParentChannel.Id == x.Id);

            var newName = autoTheadConfig.GenerateTitleArchived(Context.User);
            if (currentThread.Name.Equals(newName)) return;
            await currentThread.ModifyAsync(x =>
            {
                x.Archived = true;
                x.Locked = true;
                x.Name = newName;
            });
        }

        [Command("Autothread delete")]
        [Alias("Att delete")]
        [Summary("Delete an auto-thread.")]
        [RequireDeletableAutoThread]
        [RequireAutoThreadAuthor(Group = "AuthorOrMod")]
        [RequireModerator(Group = "AuthorOrMod")]
        public async Task DeleteAutoThread()
        {
            var currentThread = Context.Message.Channel as SocketThreadChannel;
            var autoTheadConfig = _settings.AutoThreadChannels.Find(x => currentThread.ParentChannel.Id == x.Id);

            await currentThread.DeleteAsync();
        }
    }

    #endregion
}
