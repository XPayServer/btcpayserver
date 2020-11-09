#if ALTCOINS
using BTCPayServer.Contracts;
using BTCPayServer.Payments;
using BTCPayServer.Services.Altcoins.Stripe.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Services.Altcoins.Stripe
{
    public static class StripeExtensions
    {
        public static IServiceCollection AddStripe(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<StripePaymentMethodHandler>();
            serviceCollection.AddSingleton<IPaymentMethodHandler>(provider =>
                provider.GetService<StripePaymentMethodHandler>());
            serviceCollection.AddSingleton<IUIExtension>(new UIExtension("Stripe/StoreNavStripeExtension", "store-nav"));
            serviceCollection.AddHostedService<StripeService>();

            return serviceCollection;
        }
    }
}
#endif
