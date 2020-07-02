using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Services.Altcoins.Ethereum.Configuration
{
    public class EthereumLikeConfiguration
    {
        public static string SettingsKey(int chainId)
        {
            return $"{nameof(EthereumLikeConfiguration)}_{chainId}";
        }
        public int ChainId { get; set; }
        [Display(Description = "Web3 provider url")]
        public string Web3ProviderUrl { get; set; }
    }
}
                                                                                                                                                                  
