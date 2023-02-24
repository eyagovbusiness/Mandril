using MandrilBot;
using TheGoodFramework.CA.Application;

namespace MandrilAPI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var lWebApplication = WebApplicationAbstraction.CreateCustomWebApplication(
            aWebHostBuilderAction =>
            {
                //Singleton MandrilDiscordBot shared by all the services.
                aWebHostBuilderAction.Services.AddSingleton<MandrilDiscordBot>();
                //Depends on MandrilDiscordBot, creates the singleton instance and start connection asynchronously in the background.
                aWebHostBuilderAction.Services.AddHostedService<MandrilDiscordBotBackgroundStart>();
                //Implements CQRS pattern, depends on MandrilDiscordBot
                aWebHostBuilderAction.Services.AddMediatR(cfg =>
                {
                    cfg.RegisterServicesFromAssembly(typeof(Controllers.MandrilController).Assembly);
                });
            });
            lWebApplication.CustomRun();
        }
    }
}