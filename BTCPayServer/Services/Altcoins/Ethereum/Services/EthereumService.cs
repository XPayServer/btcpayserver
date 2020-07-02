using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Services.Altcoins.Ethereum.Payments;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Altcoins.Ethereum.Configuration;
using BTCPayServer.Services.Invoices;
using NBitcoin;

namespace BTCPayServer.Services.Altcoins.Ethereum.Services
{
    public class EthereumService : EventHostedServiceBase
    {
        private readonly EventAggregator _eventAggregator;
        private readonly StoreRepository _storeRepository;
        private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
        private readonly SettingsRepository _settingsRepository;
        private readonly InvoiceRepository _invoiceRepository;
        private readonly Dictionary<int, EthereumWatcher> _chainHostedServices = new Dictionary<int, EthereumWatcher>();

        private readonly Dictionary<int, CancellationTokenSource> _chainHostedServiceCancellationTokenSources =
            new Dictionary<int, CancellationTokenSource>();

        public readonly Dictionary<string, BTCPayEthereumBlockProgressRepository.EthereumChainHistory>
            LastObservedChainHistory =
                new Dictionary<string, BTCPayEthereumBlockProgressRepository.EthereumChainHistory>();

        public EthereumService(EventAggregator eventAggregator, StoreRepository storeRepository,
            BTCPayNetworkProvider btcPayNetworkProvider,
            SettingsRepository settingsRepository,
            InvoiceRepository invoiceRepository) : base(
            eventAggregator)
        {
            _eventAggregator = eventAggregator;
            _storeRepository = storeRepository;
            _btcPayNetworkProvider = btcPayNetworkProvider;
            _settingsRepository = settingsRepository;
            _invoiceRepository = invoiceRepository;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            var chainIds = _btcPayNetworkProvider.GetAll().OfType<EthereumBTCPayNetwork>()
                .Select(network => network.ChainId).Distinct().ToList();
            if (!chainIds.Any())
            {
                return;
            }

            await base.StartAsync(cancellationToken);
            foreach (var chainId in chainIds)
            {
                try
                {
                    await HandleChainWatcher(
                        await _settingsRepository.GetSettingAsync<EthereumLikeConfiguration>(
                            EthereumLikeConfiguration.SettingsKey(chainId)), cancellationToken);
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var chainHostedService in _chainHostedServices.Values)
            {
                _ = chainHostedService.StopAsync(cancellationToken);
            }

            return base.StopAsync(cancellationToken);
        }

        protected override void SubscribeToEvents()
        {
            base.SubscribeToEvents();

            Subscribe<ReserveEthereumAddress>();
            Subscribe<SettingsChanged<BTCPayEthereumBlockProgressRepository.EthereumChainHistory>>();
            Subscribe<SettingsChanged<EthereumLikeConfiguration>>();
        }

        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            if (evt is ReserveEthereumAddress reserveEthereumAddress)
            {
                await HandleReserveNextAddress(reserveEthereumAddress);
            }

            if (evt is SettingsChanged<BTCPayEthereumBlockProgressRepository.EthereumChainHistory> ethereumChainHistory)
            {
                LastObservedChainHistory.AddOrReplace(
                    BTCPayEthereumBlockProgressRepository.EthereumChainHistory.GetSettingsKey(
                        ethereumChainHistory.Settings.ChainId, ethereumChainHistory.Settings.IsERC),
                    ethereumChainHistory.Settings);
            }

            if (evt is SettingsChanged<EthereumLikeConfiguration> settingsChangedEthConfig)
            {
                await HandleChainWatcher(settingsChangedEthConfig.Settings, cancellationToken);
            }

            await base.ProcessEvent(evt, cancellationToken);
        }

        private async Task HandleChainWatcher(EthereumLikeConfiguration ethereumLikeConfiguration,
            CancellationToken cancellationToken)
        {
            if (ethereumLikeConfiguration is null)
            {
                return;
            }

            if (_chainHostedServiceCancellationTokenSources.ContainsKey(ethereumLikeConfiguration.ChainId))
            {
                _chainHostedServiceCancellationTokenSources[ethereumLikeConfiguration.ChainId].Cancel();
                await _chainHostedServices[ethereumLikeConfiguration.ChainId].StopAsync(cancellationToken);
                _chainHostedServiceCancellationTokenSources.Remove(ethereumLikeConfiguration.ChainId);
                _chainHostedServices.Remove(ethereumLikeConfiguration.ChainId);
            }

            if (!string.IsNullOrWhiteSpace(ethereumLikeConfiguration.Web3ProviderUrl))
            {
                var cts = new CancellationTokenSource();
                _chainHostedServiceCancellationTokenSources.Add(ethereumLikeConfiguration.ChainId, cts);
                _chainHostedServices.Add(ethereumLikeConfiguration.ChainId,
                    new EthereumWatcher(ethereumLikeConfiguration.ChainId, ethereumLikeConfiguration,
                        _btcPayNetworkProvider, _settingsRepository, _eventAggregator, _invoiceRepository, this));
                await _chainHostedServices[ethereumLikeConfiguration.ChainId].StartAsync(CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken, cts.Token).Token);
            }
        }

        private async Task HandleReserveNextAddress(ReserveEthereumAddress reserveEthereumAddress)
        {
            var store = await _storeRepository.FindStore(reserveEthereumAddress.StoreId);
            var ethereumSupportedPaymentMethod = store.GetSupportedPaymentMethods(_btcPayNetworkProvider)
                .OfType<EthereumSupportedPaymentMethod>()
                .SingleOrDefault(method => method.PaymentId.CryptoCode == reserveEthereumAddress.CryptoCode);
            if (ethereumSupportedPaymentMethod == null)
            {
                _eventAggregator.Publish(new ReserveEthereumAddressResponse()
                {
                    OpId = reserveEthereumAddress.OpId, Failed = true
                });
                return;
            }

            ethereumSupportedPaymentMethod.CurrentIndex++;
            var address = ethereumSupportedPaymentMethod.GetWalletDerivator()?
                .Invoke((int)ethereumSupportedPaymentMethod.CurrentIndex);

            if (string.IsNullOrEmpty(address))
            {
                _eventAggregator.Publish(new ReserveEthereumAddressResponse()
                {
                    OpId = reserveEthereumAddress.OpId, Failed = true
                });
                return;
            }
            store.SetSupportedPaymentMethod(ethereumSupportedPaymentMethod.PaymentId,
                ethereumSupportedPaymentMethod);
            await _storeRepository.UpdateStore(store);
            _eventAggregator.Publish(new ReserveEthereumAddressResponse()
            {
                Address = address,
                Index = ethereumSupportedPaymentMethod.CurrentIndex,
                CryptoCode = ethereumSupportedPaymentMethod.CryptoCode,
                OpId = reserveEthereumAddress.OpId,
                StoreId = reserveEthereumAddress.StoreId,
                XPub = ethereumSupportedPaymentMethod.XPub
            });
        }

        public async Task<ReserveEthereumAddressResponse> ReserveNextAddress(ReserveEthereumAddress address)
        {
            address.OpId = string.IsNullOrEmpty(address.OpId) ? Guid.NewGuid().ToString() : address.OpId;
            var tcs = new TaskCompletionSource<ReserveEthereumAddressResponse>();
            var subscription = _eventAggregator.Subscribe<ReserveEthereumAddressResponse>(response =>
            {
                if (response.OpId == address.OpId)
                {
                    tcs.SetResult(response);
                }
            });
            _eventAggregator.Publish(address);

            if (tcs.Task.Wait(TimeSpan.FromSeconds(60)))
            {
                subscription?.Dispose();
                return await tcs.Task;
            }

            subscription?.Dispose();
            return null;
        }

        public class ReserveEthereumAddressResponse
        {
            public string StoreId { get; set; }
            public string CryptoCode { get; set; }
            public string Address { get; set; }
            public long Index { get; set; }
            public string OpId { get; set; }
            public string XPub { get; set; }
            public bool Failed { get; set; }

            public override string ToString()
            {
                return $"Reserved {CryptoCode} address {Address} for store {StoreId}";
            }
        }

        public class ReserveEthereumAddress
        {
            public string StoreId { get; set; }
            public string CryptoCode { get; set; }
            public string OpId { get; set; }

            public override string ToString()
            {
                return $"Reserving {CryptoCode} address for store {StoreId}";
            }
        }

        public bool IsAllAvailable()
        {
            return _btcPayNetworkProvider.GetAll().OfType<EthereumBTCPayNetwork>()
                .All(network => IsAvailable(network.CryptoCode));
        }

        public bool IsAvailable(string networkCryptoCode)
        {
            var chainId = _btcPayNetworkProvider.GetNetwork<EthereumBTCPayNetwork>(networkCryptoCode)?.ChainId;
            return chainId != null && _chainHostedServices.TryGetValue(chainId.Value, out var watcher) &&
                   !watcher.CatchingUp;
        }
    }
}
