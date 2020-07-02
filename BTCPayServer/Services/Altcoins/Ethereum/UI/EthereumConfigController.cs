using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Models;
using BTCPayServer.Security;
using BTCPayServer.Services.Altcoins.Ethereum.Configuration;
using BTCPayServer.Services.Altcoins.Ethereum.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Services.Altcoins.Ethereum.UI
{
    [Route("ethconfig")]
    [OnlyIfSupportEth]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public class EthereumConfigController : Controller
    {
        private readonly SettingsRepository _settingsRepository;

        public EthereumConfigController(SettingsRepository settingsRepository)
        {
            _settingsRepository = settingsRepository;
        }

        [HttpGet("{chainId}")]
        public async Task<IActionResult> UpdateChainConfig(int chainId)
        {
            return View("Ethereum/UpdateChainConfig",
                (await _settingsRepository.GetSettingAsync<EthereumLikeConfiguration>(
                    EthereumLikeConfiguration.SettingsKey(chainId))) ?? new EthereumLikeConfiguration()
                {
                    ChainId = chainId, Web3ProviderUrl = ""
                });
        }

        [HttpPost("{chainId}")]
        public async Task<IActionResult> UpdateChainConfig(int chainId, EthereumLikeConfiguration vm)
        {
            var current = await _settingsRepository.GetSettingAsync<EthereumLikeConfiguration>(
                EthereumLikeConfiguration.SettingsKey(chainId));
            if (current?.Web3ProviderUrl != vm.Web3ProviderUrl)
            {
                vm.ChainId = chainId;
                await _settingsRepository.UpdateSetting(vm, EthereumLikeConfiguration.SettingsKey(chainId));
            }

            TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Severity = StatusMessageModel.StatusSeverity.Success, Message = $"Chain {chainId} updated"
            });
            return RedirectToAction("Index", "Home");
        }
    }
}
