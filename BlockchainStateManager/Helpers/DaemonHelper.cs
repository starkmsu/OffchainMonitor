﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BlockchainStateManager.Settings;
using NBitcoin;
using Common.Settings;
using Common.Helpers.BlockchainExplorerHelper;

namespace BlockchainStateManager.Helpers
{
    public class DaemonHelper : IDaemonHelper
    {
        IBlockchainStateManagerSettingsProvider settingsProvider = null;

        public IBlockchainExplorerHelper blockchainExplorerHelper
        {
            get;
            set;
        }

        public DaemonHelper(IBlockchainStateManagerSettingsProvider _settingsProvider)
        {
            settingsProvider = _settingsProvider;
        }

        public async Task<IEnumerable<string>> GenerateBlocks(int count)
        {
            List<string> blockIds = new List<string>();

            var settings = settingsProvider.GetSettings();

            for (int i = 0; i < count; i++)
            {
                var rpcClient = Helper.GetRPCClient(settings);
                var response = await rpcClient.GenerateBlocksAsync(1);
                blockIds.AddRange(response);
                await blockchainExplorerHelper.WaitUntillBlockchainExplorerHasIndexed(blockchainExplorerHelper.HasBlockIndexed, response);
            }

            return blockIds;
        }

        public async Task<Tuple<bool, string, string>> GetTransactionHex(string transactionId)
        {
            var settings = settingsProvider.GetSettings();

            string transactionHex = "";
            bool errorOccured = false;
            string errorMessage = "";
            try
            {
                var client = Helper.GetRPCClient(settings);
                transactionHex = (await client.GetRawTransactionAsync(uint256.Parse(transactionId), true)).ToHex();
            }
            catch (Exception e)
            {
                errorOccured = true;
                errorMessage = e.ToString();
            }
            return new Tuple<bool, string, string>(errorOccured, errorMessage, transactionHex);
        }

        public async Task<uint256> SendBitcoinToDestination(BitcoinAddress destinationAddress, Money money)
        {
            var settings = settingsProvider.GetSettings();

            var client = Helper.GetRPCClient(settings);
            return await client.SendToAddressAsync(destinationAddress, money);
        }
    }
}
