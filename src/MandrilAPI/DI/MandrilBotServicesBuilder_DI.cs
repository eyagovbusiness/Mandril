﻿using MandrilBot;
using MandrilBot.Controllers;
using MandrilBot.News;

namespace MandrilAPI
{
    public static class MandrilBotServicesBuilder_DI 
    {
        /// <summary>
        /// Adds all needed MandrilBot related services.
        /// </summary>
        /// <param name="aWebHostBuilder"></param>
        /// <returns></returns>
        public static WebApplicationBuilder AddMandrilBotServices(this WebApplicationBuilder aWebHostBuilder)
        {
            //Singleton MandrilDiscordBot shared by all the services.Services will acess only through the interface protecting the class.
            aWebHostBuilder.Services.AddSingleton<IMandrilDiscordBot, MandrilDiscordBot>();
            //Depends on MandrilDiscordBo: add Star Citizen news service
            aWebHostBuilder.Services.AddSingleton<IDiscordBotNewsService, DiscordBotNewsService>();
            //Depends on MandrilDiscordBot and DiscordBotNewsService: create the singleton instance and start connection asynchronously in the background. 
            aWebHostBuilder.Services.AddHostedService<MandrilDiscordBotBackgroundTasks>();
            //Depends on MandrilDiscordBot: add controllers
            aWebHostBuilder.Services.AddScoped<IChannelsController, ChannelsController>();
            aWebHostBuilder.Services.AddScoped<IGuildController, GuildController>();
            aWebHostBuilder.Services.AddScoped<IRolesController, RolesController>();
            aWebHostBuilder.Services.AddScoped<IUsersController, UsersController>();

            return aWebHostBuilder;
            
        }
    }
}
