using BTCPayServer.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Zammad
{
    public static class ZammadExtensions
    {
        public static void AddZammadServices(this IServiceCollection services)
        {
            services.AddHostedService<ZammadHostedService>();
            services.AddSingleton<IUIExtension>(new UIExtension("LayoutPartials/ZammadNavExtension", "header-nav"));
        }
    }
}
