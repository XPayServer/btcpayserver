using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Services.Altcoins.Ethereum.Configuration;
using BTCPayServer.Services.Altcoins.Ethereum.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Logging;
using NBXplorer;
using Nethereum.BlockchainProcessing;
using Nethereum.BlockchainProcessing.Processor;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.StandardTokenEIP20.ContractDefinition;
using Nethereum.Web3;
using Transaction = Nethereum.RPC.Eth.DTOs.Transaction;

namespace BTCPayServer.Services.Altcoins.Ethereum.Services
{
    public class EthereumWatcher : BaseAsyncService
    {
        private readonly SettingsRepository _settingsRepository;
        private readonly EventAggregator _eventAggregator;
        private readonly InvoiceRepository _invoiceRepository;
        private readonly EthereumService _ethereumService;
        private int ChainId { get; }
        private readonly Dictionary<string, string> TrackedContracts;
        private readonly HashSet<PaymentMethodId> PaymentMethods;
        private readonly ConcurrentBag<string> WatchedAddresses = new ConcurrentBag<string>();
        private readonly Web3 Web3;
        private readonly List<EthereumBTCPayNetwork> Networks;
        private readonly CompositeDisposable leases = new CompositeDisposable();
        public bool CatchingUp { get; private set; } = true;
        public EthereumWatcher(int chainId, EthereumLikeConfiguration config,
            BTCPayNetworkProvider btcPayNetworkProvider, SettingsRepository settingsRepository,
            EventAggregator eventAggregator, InvoiceRepository invoiceRepository, EthereumService ethereumService)
        {
            _settingsRepository = settingsRepository;
            _eventAggregator = eventAggregator;
            _invoiceRepository = invoiceRepository;
            _ethereumService = ethereumService;
            ChainId = chainId;
            Web3 = new Web3(config.Web3ProviderUrl);
            Networks = btcPayNetworkProvider.GetAll()
                .OfType<EthereumBTCPayNetwork>()
                .Where(network => network.ChainId == chainId)
                .ToList();
            TrackedContracts = Networks
                .OfType<ERC20BTCPayNetwork>()
                .ToDictionary(network => network.SmartContractAddress, network => network.CryptoCode);
            PaymentMethods = Networks
                .Select(network => new PaymentMethodId(network.CryptoCode, EthereumPaymentType.Instance))
                .ToHashSet();
        }

        internal override Task[] InitializeTasks()
        {
            Logs.NodeServer.LogInformation($"Starting EthereumWatcher for chain {ChainId}");
            leases.Add(_eventAggregator.Subscribe<EthereumService.ReserveEthereumAddressResponse>(response =>
            {
                if (Networks.Any(network => network.CryptoCode == response.CryptoCode))
                {
                    WatchedAddresses.Add(response.Address);
                }
            }));
            leases.Add(_eventAggregator.Subscribe<NewMatchedEthereumTransaction>(async response =>
            {
                if (response.ChainId != ChainId)
                {
                    return;
                }

                string cryptoCode;
                CryptoPaymentData paymentData;
                
                var accounted = true;
                var currentBlock = (await Web3.Eth.Blocks.GetBlockNumber.SendRequestAsync());
                if (response.Log == null &&
                    TrackedContracts.TryGetValue(response.Log.Log.Address, out cryptoCode))
                {
                    accounted = !response.Log.Log.Removed;
                    paymentData = new EthereumLikePaymentData()
                    {
                        Address = response.Address,
                        Amount = (long)response.Log.Event.Value,
                        Network =
                            Networks.Single(network =>
                                network.CryptoCode.Equals(cryptoCode, StringComparison.InvariantCultureIgnoreCase)),
                        BlockNumber = (long?)response.Log.Log.BlockNumber.Value,
                        TransactionId = response.Log.Log.TransactionHash,
                        ConfirmationCount = (long)(currentBlock - response.Log.Log.BlockNumber.Value),
                        LogIndex = (int)response.Log.Log.LogIndex.Value
                    };
                }
                else
                {
                    cryptoCode = Networks.SingleOrDefault(network => !(network is ERC20BTCPayNetwork))?.CryptoCode;

                    accounted = !response.Log.Log.Removed;
                    paymentData = new EthereumLikePaymentData()
                    {
                        Address = response.Address,
                        Amount = (long)response.Tx.Value.Value,
                        Network =
                            Networks.Single(network =>
                                network.CryptoCode.Equals(cryptoCode, StringComparison.InvariantCultureIgnoreCase)),
                        BlockNumber = (long?)response.Tx.BlockNumber.Value,
                        TransactionId = response.Tx.TransactionHash,
                        ConfirmationCount = (long)(currentBlock - response.Tx.BlockNumber.Value),
                        LogIndex = null
                    };
                }

                if (string.IsNullOrEmpty(cryptoCode))
                    return;
                var invoice =
                    (await _invoiceRepository.GetInvoicesFromAddresses(new[] {$"{cryptoCode}#{response.Address}"}))
                    .FirstOrDefault();

                var alreadyExistingPaymentThatMatches = GetAllEthereumLikePayments(invoice)
                    .Where(entity =>
                        entity.GetCryptoCode().Equals(cryptoCode, StringComparison.InvariantCultureIgnoreCase))
                    .Select(entity => (Payment: entity, PaymentData: entity.GetCryptoPaymentData()))
                    .SingleOrDefault(c => c.PaymentData.GetPaymentId() == paymentData.GetPaymentId());

                //if it doesnt, add it and assign a new  address to the system if a balance is still due
                if (alreadyExistingPaymentThatMatches.Payment == null)
                {
                    var payment = await _invoiceRepository.AddPayment(invoice.Id, DateTimeOffset.UtcNow,
                        paymentData, paymentData.Network, accounted);
                    if (payment != null)
                        await ReceivedPayment(invoice, payment);
                }
                else
                {
                    //else update it with the new data
                    alreadyExistingPaymentThatMatches.PaymentData = paymentData;
                    alreadyExistingPaymentThatMatches.Payment.SetCryptoPaymentData(paymentData);

                    await _invoiceRepository.UpdatePayments(new List<PaymentEntity>()
                    {
                        alreadyExistingPaymentThatMatches.Payment
                    });

                    _eventAggregator.Publish(new Events.InvoiceNeedUpdateEvent(invoice.Id));
                }
            }));
            return new[] {CreateLoopTask(StartListeningAndCatchUp)};
        }


        public override Task StopAsync(CancellationToken cancellationToken)
        {
            
            Logs.NodeServer.LogInformation($"Stopping EthereumWatcher for chain {ChainId}");
            leases.Dispose();
            return base.StopAsync(cancellationToken);
        }

        private async Task StartListeningAndCatchUp()
        {
            var processors = CreateProcessors(0);
            CatchingUp = true;
            await UpdateAnyPendingEthLikePaymentAndAddressWatchList();
            var currentBlock = (await Web3.Eth.Blocks.GetBlockNumber.SendRequestAsync());
            var targetBlock = currentBlock.Value - 12;
            var processingTasks = processors
                .Select(processor => processor.ExecuteAsync(targetBlock, Cancellation, targetBlock));

            await Task.WhenAll(processingTasks);
            CatchingUp = false;
            await UpdateAnyPendingEthLikePaymentAndAddressWatchList();

            while (!CancellationToken.IsCancellationRequested)
            {
                
                currentBlock = (await Web3.Eth.Blocks.GetBlockNumber.SendRequestAsync());
                targetBlock = currentBlock.Value;
                processingTasks = processors
                    .Select(processor => processor.ExecuteAsync(targetBlock, Cancellation, targetBlock));

                await Task.WhenAll(processingTasks);
                await UpdateAnyPendingEthLikePaymentAndAddressWatchList();
            }
        }

        private IEnumerable<BlockchainProcessor> CreateProcessors(uint confsRequired)
        {
            var result = new List<BlockchainProcessor>();
            if (TrackedContracts.Any())
            {
                var erc20ProgressRepo =
                    new BTCPayEthereumBlockProgressRepository(ChainId, true, Web3, _settingsRepository);
                result.Add(Web3.Processing.Logs.CreateProcessorForContracts<TransferEventDTO>(
                    TrackedContracts.Keys.ToArray(),
                    log =>
                    {
                        _eventAggregator.Publish(new NewMatchedEthereumTransaction()
                        {
                            Address = log.Event.To, ChainId = ChainId, Log = log
                        });
                        return Task.CompletedTask;
                    }, confsRequired,
                    log => Task.FromResult(WatchedAddresses.Contains(log.Event.To)),
                    erc20ProgressRepo));
            }

            var progressRepo = new BTCPayEthereumBlockProgressRepository(ChainId, false, Web3, _settingsRepository);
            result.Add(Web3.Processing.Blocks.CreateBlockProcessor(progressRepo, steps =>
            {
                steps.TransactionStep.SetMatchCriteria(
                    t => WatchedAddresses.Contains(t.Transaction.To));
                steps.TransactionReceiptStep.AddSynchronousProcessorHandler(tx =>
                    _eventAggregator.Publish(new NewMatchedEthereumTransaction()
                    {
                        Address = tx.Transaction.To, Tx = tx.Transaction, ChainId = ChainId
                    }));
            }, confsRequired));

            return result;
        }

        private async Task UpdateAnyPendingEthLikePaymentAndAddressWatchList()
        {
            var invoiceIds = await _invoiceRepository.GetPendingInvoices();
            if (!invoiceIds.Any())
            {
                return;
            }

            var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery() {InvoiceId = invoiceIds});
            invoices = invoices
                .Where(entity => PaymentMethods.Any(id => entity.GetPaymentMethod(id) != null))
                .ToArray();

            await UpdatePaymentStates(invoices);
        }

        private async Task UpdatePaymentStates(InvoiceEntity[] invoices)
        {
            if (!invoices.Any())
            {
                return;
            }
            //get all the required data in one list (invoice, its existing payments and the current payment method details)
            var expandedInvoices = invoices.Select(entity => (Invoice: entity,
                    ExistingPayments: GetAllEthereumLikePayments(entity),
                    PaymentMethodDetails: entity.GetPaymentMethods()
                        .Select(method => method.GetPaymentMethodDetails() as EthereumLikeOnChainPaymentMethodDetails)
                        .Where(details => details != null)))
                .Select(tuple => (
                    tuple.Invoice,
                    tuple.PaymentMethodDetails,
                    ExistingPayments: tuple.ExistingPayments.Select(entity =>
                        (Payment: entity, PaymentData: (EthereumLikePaymentData)entity.GetCryptoPaymentData(),
                            tuple.Invoice))
                ));

            var existingPaymentData = expandedInvoices.SelectMany(tuple => tuple.ExistingPayments);

            Logs.NodeServer.LogInformation($"Checking {existingPaymentData.Count()} existing payments on {expandedInvoices.Count()} invoices on ETH chain {ChainId}");
            
            var existingPaymentAddresses = existingPaymentData.Select(tuple => tuple.PaymentData.Address);
            var currentPaymentAddresses = expandedInvoices.SelectMany(tuple =>
                tuple.PaymentMethodDetails.Select(details => details.DepositAddress));
            WatchedAddresses.Clear();
            foreach (var address in existingPaymentAddresses.Concat(currentPaymentAddresses).Distinct())
            {
                WatchedAddresses.Add(address);
            }
            
            Logs.NodeServer.LogInformation($"Watching {WatchedAddresses.Count} on ETH chain {ChainId}");

            var currentBlock = await Web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            var transferProcessingTasks = existingPaymentData.Select(async tuple =>
            {
                var pd = tuple.PaymentData;
                var ie = tuple.Invoice;
                var pe = tuple.Payment;
                
                
                var txReceipt =
                    await Web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(pd.TransactionId);
                if (pd.Network is ERC20BTCPayNetwork erc20BTCPayNetwork)
                {
                    var log = txReceipt?.DecodeAllEvents<TransferEventDTO>()
                        .SingleOrDefault(log => pd.LogIndex == log.Log.LogIndex.Value);

                    if (!(txReceipt?.Succeeded() is true) || log == null || log.Event.To != pd.Address || log.Log.Removed)
                    {
                        //wtf happened here..
                        pe.Accounted = false;
                        pe.SetCryptoPaymentData(pd);
                    }
                    else
                    {
                        pd.ConfirmationCount = (long)(currentBlock.Value - txReceipt.BlockNumber);
                        pd.Amount = (long)log.Event.Value;
                        pd.LogIndex = (int?)log.Log.LogIndex.Value;
                        pd.BlockNumber = (long?)txReceipt.BlockNumber.Value;
                        pe.Accounted = txReceipt.Succeeded();
                        pe.SetCryptoPaymentData(pd);
                    }

                    return (pe, ie);
                }
                else
                {
                    var tx = await Web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(pd.TransactionId);
                    if (tx == null || !tx.IsTo(pd.Address) || !(txReceipt?.Succeeded() is true) )
                    {
                        //wtf happened here..
                        pe.Accounted = false;
                        pe.SetCryptoPaymentData(pd);
                    }
                    else
                    {
                        pd.ConfirmationCount = (long)(currentBlock.Value - txReceipt.BlockNumber);
                        pd.Amount = (long)tx.Value.Value;
                        pd.LogIndex = null;
                        pd.BlockNumber = (long?)txReceipt.BlockNumber.Value;
                        pe.Accounted = txReceipt.Succeeded();
                        pe.SetCryptoPaymentData(pd);
                    }

                    return (pe, ie);
                }
            }).ToArray();

            await Task.WhenAll(transferProcessingTasks);
            var updatedPaymentEntities = transferProcessingTasks.Select(tuple => tuple.Result).ToList();
            await _invoiceRepository.UpdatePayments(updatedPaymentEntities.Select(tuple => tuple.Item1).ToList());
            foreach (var valueTuples in updatedPaymentEntities.GroupBy(entity => entity.Item2))
            {
                if (valueTuples.Any())
                {
                    _eventAggregator.Publish(new Events.InvoiceNeedUpdateEvent(valueTuples.Key.Id));
                }
            }
            
            Logs.NodeServer.LogInformation($"Updated {updatedPaymentEntities.Count} out of {existingPaymentData.Count()} existing payments on {expandedInvoices.Count()} invoices on ETH chain {ChainId}");
        }

        private IEnumerable<PaymentEntity> GetAllEthereumLikePayments(InvoiceEntity invoice)
        {
            return invoice.GetPayments()
                .Where(p => PaymentMethods.Contains(p.GetPaymentMethodId()));
        }

        public class NewMatchedEthereumTransaction
        {
            public int ChainId { get; set; }
            public string Address { get; set; }
            public Transaction Tx { get; set; }
            public EventLog<TransferEventDTO> Log { get; set; }
        }
        
        private async Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
        {
            var paymentData = (EthereumLikePaymentData)payment.GetCryptoPaymentData();
            var paymentMethod = invoice.GetPaymentMethod(payment.Network, EthereumPaymentType.Instance);
            if (paymentMethod != null &&
                paymentMethod.GetPaymentMethodDetails() is EthereumLikeOnChainPaymentMethodDetails eth &&
                eth.GetPaymentDestination() == paymentData.GetDestination() &&
                paymentMethod.Calculate().Due > Money.Zero)
            {
                var nextAddress = await _ethereumService.ReserveNextAddress(new EthereumService.ReserveEthereumAddress()
                {
                    CryptoCode = payment.GetPaymentMethodId().CryptoCode, 
                    StoreId = invoice.StoreId
                });
                eth.Index = nextAddress.Index;
                eth.DepositAddress = nextAddress.Address;
                await _invoiceRepository.NewAddress(invoice.Id, eth, payment.Network);
                _eventAggregator.Publish(
                    new InvoiceNewAddressEvent(invoice.Id, eth.DepositAddress, payment.Network));
                paymentMethod.SetPaymentMethodDetails(eth);
                invoice.SetPaymentMethod(paymentMethod);
            }

            _eventAggregator.Publish(
                new InvoiceEvent(invoice, 1002, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }
    }
}
