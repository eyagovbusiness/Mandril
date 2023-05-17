﻿using AngleSharp.Common;
using DSharpPlus.Entities;
using MandrilBot.Configuration;
using MandrilBot.Controllers;
using MandrilBot.News.Interfaces;
using MandrilBot.News.Messages;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TGF.Common.Extensions;
using TGF.Common.Net.Http;

namespace MandrilBot.News.SlaveServices
{
    internal readonly struct RsiServiceStatus
    {
        public const string Operational = "Operational";
        public const string DegradedPerformance = "Degraded Performance";
        public const string PartialOutage = "Partial Outage";
        public const string MajorOutage = "Major Outage";
        public const string UnderMaintenance = "Under Maintenance";
    }
    internal readonly struct IncidentStatus
    {
        public const string Resolved = "Resolved";
        public const string Unresolved = "Unresolved";
    }

    /// <summary>
    /// Service that will get the last news from the StarCitizen comm-link resource by reading the HTML and notifying the differences on Discord periodically.
    /// (Has to be like since there is not any RSS available for this resource)
    /// </summary>
    internal class RSIStatusNewsService : DiscordBotNewsServiceBase<RSIStatusNewsMessage>, INewsWebTracker<RSIStatusNewsMessage>
    {
        private readonly BotNewsConfig _botNewsConfig;
        private readonly ReadOnlyCollection<string> _rsiKnownServices= new ReadOnlyCollection<string>(new string[]{ "Platform", "Persistent Universe", "Electronic Access" });
        private string mLastGeneralStatusNotified;
        public RSIStatusNewsService(IHttpClientFactory aHttpClientFactory, BotNewsConfig aBotNewsConfig)
        {
            _botNewsConfig = aBotNewsConfig;
            mNewsTopicConfig = aBotNewsConfig.RSIStatus;
            mTimedHttpClientProvider = new TimedHttpClientProvider(
                aHttpClientFactory, 
                new TimeSpan(1, 0, 0), 
                aBaseAddress: GetBaseAddressWithSubdomainString(_botNewsConfig.BaseResourceAddress , mNewsTopicConfig.ResourcePath)
                );
        }

        #region Overrides

        public override async Task InitAsync(IChannelsController aDiscordChannelsControllerService)
        {
            await base.InitAsync(aDiscordChannelsControllerService);
            mLastMessageList = await GetLastMessageListAsync();
        }

        public override async Task TickExecute(CancellationToken aCancellationToken)
        {
            var lUpdateMessageList = await GetUpdatesAsync();
            if (lUpdateMessageList.IsNullOrEmpty()) return;

            await lUpdateMessageList.ParallelForEachAsync(
                MandrilDiscordBot._maxDegreeOfParallelism,
                update => SendMessage(update),
                aCancellationToken
                );
        }

        public override HealthCheckResult GetHealthCheck(CancellationToken aCancellationToken = default)
        {
            var lElapsedSecondsSinceTheLastGet = (DateTimeOffset.Now - mLastGetElapsedTime).Seconds;
            return lElapsedSecondsSinceTheLastGet > mMaxGetElapsedTime
                ? HealthCheckResult.Degraded(string.Format("The RSIStatusNewsService's health is degraded. It was not possible to get the news resource, the last successful get was at {0}", mLastGetElapsedTime))
                : HealthCheckResult.Healthy(string.Format("The RSIStatusNewsService is healthy. Last news get was {0} seconds ago.", lElapsedSecondsSinceTheLastGet));
        }

        #endregion

        #region INewsService

        public async Task<List<RSIStatusNewsMessage>> GetLastMessageListAsync()
        {
            var lHTMLdocument = await DiscordBotNewsExtensions.GetHTMLAsync(mTimedHttpClientProvider.GetHttpClient());
            var disct = lHTMLdocument.ToDictionary();
            var lElementList = lHTMLdocument?.QuerySelector("ul.timeline");

            List<RSIStatusNewsMessage> lCurrentContentList = new();
            if (lElementList == null)//If could not get the news resource return empty discord message list
                return lCurrentContentList; //mLastGetElapsedTime will not be updated and healtcheck will update health if proceeds
            mLastGetElapsedTime = DateTimeOffset.Now;

            var lDate = lElementList.Children.Select(z => z.TagName).ToArray();
            var lDictionaryData = lElementList.Children.Select(y => y.ToDictionary()).Take(10).ToList();//Take only the newest 10 
            lDictionaryData.Skip(1).ForEach(sourceDictionary =>
            {
                /// [0]=date, [1]=IncidentStatus, [2]=IncidentTitle, [3]=AffectedServices, [lastServIndex +1]=ServicesStatus, [lastServIndex +2]=IncidentCreationDate, the rest = IncidentUpdates
                var lContent = sourceDictionary["TextContent"]
                                .Split('\n')
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Select(x => x.Trim())
                                .ToArray();

                if(lContent.Length >2)//empty incidents have lenght 2
                    lCurrentContentList.Add(GetMessageFromContentStringList(lContent));
            });

            return lCurrentContentList;
        }

        public async Task<List<RSIStatusNewsMessage>> GetUpdatesAsync()
        {
            var lContentList = await GetLastMessageListAsync();
            var lRes = lContentList.Except(mLastMessageList, new RSIStatusNewsMessageComparer())?
                .ToList();

            if (lRes.Count > 0)
                mLastMessageList = lContentList;

            return lRes;
        }

        public async Task SendMessage(RSIStatusNewsMessage aRSIStatusNewsMessage)
        {
            var lCurrentGeneralStatus = GetGeneralStatus();
            var lBaseAddress = mTimedHttpClientProvider.GetHttpClient().BaseAddress;
            await mNewsChannel.SendMessageAsync(new DiscordMessageBuilder()
            {
                Embed = new DiscordEmbedBuilder()
                {
                    Author = new DiscordEmbedBuilder.EmbedAuthor()
                    {
                        Name = "RSI Status",
                        Url = lBaseAddress.ToString(),
                        IconUrl = "https://spng.pngfind.com/pngs/s/90-903191_star-citizen-logo-png-download-transparent-png.png",
                    },
                    //Thumbnail = new DiscordEmbedBuilder.EmbedThumbnail() { Url = aRSIStatusNewsMessage.ImageLink },
                    Title = $"{aRSIStatusNewsMessage.IncidentTitle} - {lCurrentGeneralStatus}",
                    Description = $"{aRSIStatusNewsMessage.AffectedServices}"
                                   + Environment.NewLine + $"{aRSIStatusNewsMessage.IncidentDescription}",
                    Url = lBaseAddress.ToString(),
                    Color = GetStatusMessageColor(lCurrentGeneralStatus)
                }
            });

            await UpdateChannelName(lCurrentGeneralStatus);

        }

        #endregion

        #region Private

        /// <summary>
        /// Updates the RSI status news channel if the current general status has changed from the last notified one and set the last notified one(<see cref="mLastGeneralStatusNotified"/>, otherwise do nothing.)
        /// </summary>
        /// <param name="lCurrentGeneralStatus"></param>
        /// <returns></returns>
        private async Task UpdateChannelName(string lCurrentGeneralStatus)
        {
            if (mLastGeneralStatusNotified != lCurrentGeneralStatus)
            {
                await mNewsChannel.ModifyAsync(channel => channel.Name = GetNewsChannelName() + GetServiceStatusColorString(lCurrentGeneralStatus));
                mLastGeneralStatusNotified = lCurrentGeneralStatus;
            }
        }

        /// <summary>
        /// Gets the color of the message based on the provided <see cref="aRSIServiceStatusString"/> argument.
        /// </summary>
        /// <param name="aRSIServiceStatusString"><see cref="string"/> representing the current status of a given RSI service.</param>
        /// <returns>Associated <see cref="DiscordColor"/>.</returns>
        private static DiscordColor GetStatusMessageColor(string aRSIServiceStatusString)
            => aRSIServiceStatusString switch
            {
                RsiServiceStatus.Operational => DiscordColor.Green,
                RsiServiceStatus.DegradedPerformance => DiscordColor.Purple,
                RsiServiceStatus.PartialOutage => DiscordColor.Orange,
                RsiServiceStatus.MajorOutage => DiscordColor.Red,
                _ => DiscordColor.White,//white for "Under Maintenance" or other unknown statuses
            };

        /// <summary>
        /// Gets the color to use on renaming the Status news channel based on the provided <see cref="aRSIServiceStatusString"/> argument.
        /// </summary>
        /// <param name="aRSIServiceStatusString"><see cref="string"/> representing the current status of a given RSI service.</param>
        /// <returns><see cref="string"/> representing the figure and color to display the status on the channel name.</returns>
        private static string GetServiceStatusColorString(string aRSIServiceStatusString)
            => aRSIServiceStatusString switch
            {
                RsiServiceStatus.Operational => "🟢",//green circle
                RsiServiceStatus.DegradedPerformance => "🟣",//purple circle
                RsiServiceStatus.PartialOutage => "🟠",//orange circle
                RsiServiceStatus.MajorOutage => "🔴",//red circle
                _ => "⚪",//white circle for "Under Maintenance" or other unknown statuses
            };

        /// <summary>
        /// Gets the base name to use on each rename of the status news channel each time the status changes.
        /// </summary>
        /// <returns><see cref="string"/> representing the base status nwes base channel name.</returns>
        private string GetNewsChannelName()
            => (mNewsTopicConfig as RSIStatusConfig).NewsChannelName.ToLower();

        /// <summary>
        /// Gets the general status of all the RSI services, this will be taken from the performance impact of the last active inceident that is not resolved at the moment. If any the returned status is "Operational".
        /// </summary>
        /// <returns><see cref="string"/> representing the general status of all the RSI services.</returns>
        private string GetGeneralStatus()
            => mLastMessageList?.FirstOrDefault(x => x.IncidentStatus != IncidentStatus.Resolved).ServicesStatus ?? RsiServiceStatus.Operational;


        /// <summary>
        /// Gets the full Uri address indexing the given subdomain string into the official star citizen webpage.
        /// </summary>
        /// <param name="aBaseUrlString"></param>
        /// <param name="aSubdomainString"></param>
        /// <returns><see cref="string"/> with the base address prefixed with the given subdomain.</returns>
        private static string GetBaseAddressWithSubdomainString(string aBaseUrlString, string aSubdomainString)
        {
            UriBuilder lUriBuilder = new UriBuilder(aBaseUrlString);
            lUriBuilder.Host = $"{aSubdomainString}.{lUriBuilder.Host}";

            return lUriBuilder.Uri.ToString();
        }

        /// <summary>
        /// Gets a new instance of <see cref="RSIStatusNewsMessage"/> from an string array from the HTML resource that contains the needed information.
        /// </summary>
        /// <param name="aContentStringList">string array from the HTML resource that contains the needed information.</param>
        /// <returns>A new instance of <see cref="RSIStatusNewsMessage"/>.</returns>
        private RSIStatusNewsMessage GetMessageFromContentStringList(string[] aContentStringList)
        {
            var lLastAffectedServiceIndex = GetDisruptedServicesMaxIndex(aContentStringList);
            var lServiceStatusColorString = GetServiceStatusColorString(aContentStringList[1]);
            return new RSIStatusNewsMessage()
            {
                /// [0]=NotUseful(Today's date), [1]=IncidentStatus, [2]=IncidentTitle, [3]=AffectedServices, [lastServIndex +1]=IncidentCreationDate, [lastServIndex +2]=ServicesStatus, [lastServIndex +3]=IncidentCreationDate, the rest = IncidentUpdates
                IncidentStatus = aContentStringList[1],
                IncidentTitle = aContentStringList[2],
                AffectedServices = GetAffectedServicesString(aContentStringList[3..(lLastAffectedServiceIndex+1)], lServiceStatusColorString, aContentStringList[1]),
                IncidentCreationDate = aContentStringList[lLastAffectedServiceIndex + 1],
                ServicesStatus = aContentStringList[lLastAffectedServiceIndex + 2],
                IncidentDescription = string.Join($"{Environment.NewLine} ", aContentStringList[(lLastAffectedServiceIndex + 3)..])

            };
        }

        /// <summary>
        /// Gets from the string array from the HTML resource the index of that array where the affected services end.
        /// </summary>
        /// <param name="aContentStringList">string array from the HTML resource</param>
        /// <returns><see cref="int"/> with the index of the provieded array where the affected services end.</returns>
        private int GetDisruptedServicesMaxIndex(string[] aContentStringList)
            => Array.IndexOf(aContentStringList, aContentStringList[2..]
                                                .LastOrDefault(content => _rsiKnownServices
                                                                          .Contains(content)));

        /// <summary>
        /// Gets a new <see cref="string"/> with all the known rsi services with the respective status color string sufixed according with the provided incident arguments.
        /// </summary>
        /// <param name="aAffectedServicesByIncidentList">List of the affected services by the context incident.</param>
        /// <param name="aAffectedServiceStatusColorString">string with the status color related to the performance impact on the affected services by the context incident.</param>
        /// <param name="aIncidentStatus">The context incident status, with information about it was resolved or not yet.</param>
        /// <returns>A new <see cref="string"/> with all the known rsi services with the respective status color string sufixed.</returns>
        private string GetAffectedServicesString(string[] aAffectedServicesByIncidentList, string aAffectedServiceStatusColorString, string aIncidentStatus)
        {
            if (aIncidentStatus == IncidentStatus.Resolved)
                return GetAllRsiKnownServicesWithOperationalStatusString();

            var lNotAffectedServicesList = _rsiKnownServices
                .Except(aAffectedServicesByIncidentList)
                .Select(notAffectedService => notAffectedService + GetServiceStatusColorString(RsiServiceStatus.Operational));
            var lAllKnownServicesWithStatusList = aAffectedServicesByIncidentList
                .Select(rsiService => rsiService + $"{aAffectedServiceStatusColorString}")
                .Concat(lNotAffectedServicesList);

            return string.Join("  -  ", lAllKnownServicesWithStatusList);
        }

        /// <summary>
        /// Gets a new <see cref="string"/> with all the known rsi services with operational status color string sufixed.
        /// </summary>
        /// <returns>New <see cref="string"/> with all the known rsi services with operational status color string sufixed.</returns>
        private string GetAllRsiKnownServicesWithOperationalStatusString()
            => string.Join("  -  ", _rsiKnownServices
                                .Select(rsiService => rsiService + GetServiceStatusColorString(RsiServiceStatus.Operational)));

        #endregion

    }
}