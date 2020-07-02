using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin.Logging;
using Nethereum.BlockchainProcessing.ProgressRepositories;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace BTCPayServer.Services.Altcoins.Ethereum.Services
{
    public class BTCPayEthereumBlockProgressRepository : IBlockProgressRepository
    {
        private readonly int _chainId;
        private readonly bool _isErc20;
        private readonly Web3 _web3;
        private readonly SettingsRepository _settingsRepository;
        private readonly string SettingsKey;

        public BTCPayEthereumBlockProgressRepository(int chainId, bool isERC20, Web3 web3,
            SettingsRepository settingsRepository)
        {
            _chainId = chainId;
            _isErc20 = isERC20;
            _web3 = web3;
            _settingsRepository = settingsRepository;
            SettingsKey = EthereumChainHistory.GetSettingsKey(_chainId, isERC20);
        }

        public async Task UpsertProgressAsync(BigInteger blockNumber)
        {
            var current = await Get();
            current ??= new EthereumChainHistory()
            {
                ChainId = _chainId,
                IsERC = _isErc20
            };
            var success = true;
            retry:
            var attempts = 0;
            BlockWithTransactionHashes hash = null;
            while (hash == null && attempts <=5)
            {
                hash = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(
                    new BlockParameter((ulong)blockNumber));
                attempts++;
                if (hash == null)
                {
                    await Task.Delay(500);
                }
            }
            if (hash == null)
            {
                return;
            }
            var lastBlockNumber = current.LastBlockNumber;
            if (lastBlockNumber != null && (blockNumber - 1) == lastBlockNumber &&
                current.BlockHashes.TryGetValue(lastBlockNumber.Value, out var previousHash))
            {
                if (previousHash != hash.ParentHash)
                {
                    current.BlockHashes.Remove(lastBlockNumber.Value);
                    Logs.NodeServer.LogWarning($"Chain {_chainId}: an indexed block {lastBlockNumber.Value} was uncled. Our hash:{previousHash} Web3 hash: {hash.ParentHash}");

                    //we processed uncled blocks. Instead of appending progress, we will decrease by 1 and recurse through our block hash history until we match (as a reorg may be deep, see Eth Classic double spend attack of 150 block deep reorg)
                    blockNumber = lastBlockNumber.Value - 1;
                    success = false;
                    goto retry;
                }
            }

            if (success)
            {
                current.BlockHashes.Add((uint)blockNumber, hash.BlockHash);
                current.PruneHashes();
            }

            await _settingsRepository.UpdateSetting(current, SettingsKey);
        }

        public async Task<BigInteger?> GetLastBlockNumberProcessedAsync()
        {
            var current = await Get();

            var updated = false;
            while (current?.LastBlockNumber.HasValue is true)
            {
                var result = current.LastBlockNumber;

                var attempts = 0;
                BlockWithTransactionHashes hashResult = null;
                while (hashResult == null && attempts <=5)
                {
                    hashResult = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(
                        new BlockParameter((ulong)result));
                    attempts++;
                    if (hashResult == null)
                    {
                        await Task.Delay(500);
                    }
                }

                if (current.BlockHashes.TryGetValue(result.Value, out var hash) && hash != hashResult.BlockHash)
                {
                    Logs.NodeServer.LogWarning($"Chain {_chainId}: an indexed block {result.Value} was uncled. Our hash:{hash} Web3 hash: {hashResult.BlockHash}");
                    current.BlockHashes.Remove(result.Value);
                    updated = true;
                }
                else
                {
                    break;
                }
            }

            if (updated)
            {
                await _settingsRepository.UpdateSetting(current, SettingsKey);
            }

            return current?.LastBlockNumber;
        }

        private async Task<EthereumChainHistory> Get()
        {
            return (await _settingsRepository.GetSettingAsync<EthereumChainHistory>(SettingsKey));
        }
        
        
        public class EthereumChainHistory
        {
            public Dictionary<uint, string> BlockHashes { get; set; } = new Dictionary<uint, string>();
            
            public uint? LastBlockNumber
            {
                get { return BlockHashes.Keys.Any() ? BlockHashes.Keys.Max() : (uint?)null; }
            }

            public int ChainId { get; set; }
            public bool IsERC { get; set; }

            public static string GetSettingsKey(int chainId, bool erc20)
            {
                return $"{nameof(EthereumChainHistory)}_{chainId}{(erc20? "_erc20" : "")}";
            }

            public void PruneHashes()
            {
                BlockHashes = new Dictionary<uint, string>(BlockHashes.OrderByDescending(pair => pair.Key).Take(200));
            }

            public override string ToString()
            {
                return $"ETH Chain {ChainId} {(IsERC? "(ERC20)": "")} is synced to {LastBlockNumber}";
            }
        }
    }
    
    
}
