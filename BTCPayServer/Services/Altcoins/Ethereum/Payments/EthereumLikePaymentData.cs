using System.Globalization;
using System.Numerics;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Nethereum.Hex.HexTypes;
using Nethereum.Web3;

namespace BTCPayServer.Services.Altcoins.Ethereum.Payments
{
    public class EthereumLikePaymentData : CryptoPaymentData
    {
        public long Amount { get; set; }
        public string Address { get; set; }
        public int? LogIndex { get; set; }
        public long ConfirmationCount { get; set; }
        public string TransactionId { get; set; }

        public BTCPayNetworkBase Network { get; set; }
        public long? BlockNumber { get; set; }

        public string GetPaymentId()
        {
            return $"{TransactionId}#{Address}";
        }

        public string[] GetSearchTerms()
        {
            return new[] {TransactionId};
        }

        public decimal GetValue()
        {
            return decimal.Parse(Web3.Convert.FromWeiToBigDecimal(Amount, Network.Divisibility).ToString(),
                CultureInfo.InvariantCulture);
        }

        public bool PaymentCompleted(PaymentEntity entity)
        {
            return ConfirmationCount >= 25;
        }

        public bool PaymentConfirmed(PaymentEntity entity, SpeedPolicy speedPolicy)
        {
            switch (speedPolicy)
            {
                case SpeedPolicy.HighSpeed:
                    return ConfirmationCount >= 2;
                case SpeedPolicy.MediumSpeed:
                    return ConfirmationCount >= 6;
                case SpeedPolicy.LowMediumSpeed:
                    return ConfirmationCount >= 12;
                case SpeedPolicy.LowSpeed:
                    return ConfirmationCount >= 20;
                default:
                    return false;
            }
        }

        public PaymentType GetPaymentType()
        {
            return EthereumPaymentType.Instance;
        }

        public string GetDestination()
        {
            return Address;
        }
    }
}
