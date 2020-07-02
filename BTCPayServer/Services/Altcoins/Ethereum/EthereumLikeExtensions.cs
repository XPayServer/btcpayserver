using System;
using System.Linq;
using BTCPayServer.Configuration;
using BTCPayServer.Contracts;
using BTCPayServer.Payments;
using BTCPayServer.Services.Altcoins.Ethereum.Configuration;
using BTCPayServer.Services.Altcoins.Ethereum.Payments;
using BTCPayServer.Services.Altcoins.Ethereum.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Services.Altcoins.Ethereum
{
    public static class EthereumLikeExtensions
    {
        public static IServiceCollection AddEthereumLike(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<EthereumService>();
            serviceCollection.AddSingleton<IHostedService, EthereumService>(provider => provider.GetService<EthereumService>());
            serviceCollection.AddSingleton<EthereumLikePaymentMethodHandler>();
            serviceCollection.AddSingleton<IPaymentMethodHandler>(provider => provider.GetService<EthereumLikePaymentMethodHandler>());
            serviceCollection.AddSingleton<IStoreNavExtension,EthereumStoreNavExtension>();
            return serviceCollection;
        }
        
    }
}
