﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlockchainStateManager.Extensions;
using LykkeWalletServices.Transactions.Responses;
using NBitcoin;
using NBitcoin.OpenAsset;
using System.Security.Cryptography;
using BlockchainStateManager.Helpers;
using BlockchainStateManager.Settings;
using BlockchainStateManager.Transactions.Responses;
using BlockchainStateManager.DB;

namespace BlockchainStateManager.Offchain
{
    public class OffchainHelper
    {
        IBlockchainExplorerHelper blockchainExplorerHelper = null;
        ISettingsProvider settingsProvider = null;

        Helper helper = null;
        public OffchainHelper(IBlockchainExplorerHelper _blockchainExplorerHelper, ISettingsProvider _settingsProvider)
        {
            blockchainExplorerHelper = _blockchainExplorerHelper;
            settingsProvider = _settingsProvider;

            helper = new Helper();
        }
        public async Task<UnsignedChannelSetupTransaction> GenerateUnsignedChannelSetupTransaction(string clientPubkey, double clientContributedAmount,
            string hubPubkey, double hubContributedAmount, string channelAssetName, int channelTimeoutInMinutes)
        {
            var settings = settingsProvider.GetSettings();

            var asset = Assets.Helper.GetAssetFromName(settings.Assets, channelAssetName);
            var multisig = Helper.GetMultiSigFromTwoPubKeys(clientPubkey, hubPubkey);
            if (asset == null && channelAssetName.ToLower() != "btc")
            {
                throw new Exception(string.Format("The specified asset is not supported {0}.",
                    channelAssetName));
            }

            var walletOutputs = await blockchainExplorerHelper.GetWalletOutputs(multisig.MultiSigAddress);
            if (walletOutputs.Item2)
            {
                throw new Exception(string.Format
                    ("Error in getting outputs for wallet: {0}, the error is {1}", multisig.MultiSigAddress, walletOutputs.Item3));
            }
            else
            {
                var assetOutputs = blockchainExplorerHelper.GetWalletOutputsForAsset(walletOutputs.Item1, asset.AssetId);
                var coins = await blockchainExplorerHelper.GetColoredUnColoredCoins(assetOutputs, asset.AssetId);
                long totalAmount = 0;
                if (asset == null)
                {
                    totalAmount = coins.Item2.Sum(c => c.Amount);
                }
                else
                {
                    totalAmount = coins.Item1.Sum(c => c.Amount.Quantity);
                }

                return await GenerateUnsignedChannelSetupTransactionCore(clientPubkey, clientContributedAmount, hubPubkey,
                    hubContributedAmount, totalAmount / asset.MultiplyFactor, channelAssetName, channelTimeoutInMinutes);
            }
        }

        public async Task<UnsignedChannelSetupTransaction> GenerateUnsignedChannelSetupTransactionCore(string clientPubkey,
            double clientContributedAmount, string hubPubkey, double hubContributedAmount, double multisigNewlyAddedAmount,
            string channelAssetName, int channelTimeoutInMinutes)
        {
            var settings = settingsProvider.GetSettings();

            try
            {
                string txHex = null;
                var btcAsset = (channelAssetName.ToLower() == "btc");
                var clientAddress = (new PubKey(clientPubkey)).GetAddress(settings.Network);
                var hubAddress = (new PubKey(hubPubkey)).GetAddress(settings.Network);
                var multisig = Helper.GetMultiSigFromTwoPubKeys(clientPubkey, hubPubkey);

                var asset = settings.Assets.Where(a => a.Name == channelAssetName).FirstOrDefault();
                if (asset == null && channelAssetName.ToLower() != "btc")
                {
                    throw new Exception(string.Format("The specified asset is not supported {0}.", channelAssetName));
                }
                var assetId = asset?.AssetId;
                var assetMultiplyFactor = asset?.MultiplyFactor ?? (long)Constants.BTCToSathoshiMultiplicationFactor;

                long[] contributedAmount = new long[3];
                long[] requiredAssetAmount = new long[3];
                requiredAssetAmount[0] = (long)(clientContributedAmount * assetMultiplyFactor);
                requiredAssetAmount[1] = (long)(multisigNewlyAddedAmount * asset.MultiplyFactor);
                requiredAssetAmount[2] = (long)(hubContributedAmount * assetMultiplyFactor);

                IList<ICoin>[] coinToBeUsed = new IList<ICoin>[3];
                BitcoinAddress[] inputAddress = new BitcoinAddress[3];
                double[] inputAmount = new double[3];

                for (int i = 0; i < 3; i++)
                {
                    coinToBeUsed[i] = new List<ICoin>();
                    switch (i)
                    {
                        case 0:
                            inputAddress[i] = clientAddress;
                            inputAmount[i] = clientContributedAmount;
                            break;
                        case 1:
                            inputAddress[i] = Base58Data.GetFromBase58Data(multisig.MultiSigAddress) as BitcoinScriptAddress;
                            inputAmount[i] = multisigNewlyAddedAmount;
                            break;
                        case 2:
                            inputAddress[i] = hubAddress;
                            inputAmount[i] = hubContributedAmount;
                            break;
                    }

                    var walletOutputs = await blockchainExplorerHelper.GetWalletOutputs
                        (inputAddress[i].ToString());
                    if (walletOutputs.Item2)
                    {
                        throw new Exception(string.Format("Error in getting outputs for wallet: {0}, the error is {1}",
                            inputAddress, walletOutputs.Item3));
                    }
                    else
                    {
                        var assetOutputs = blockchainExplorerHelper.GetWalletOutputsForAsset(walletOutputs.Item1, assetId);
                        ICoin[] coinToSelectFrom;
                        if (btcAsset)
                        {
                            coinToSelectFrom = (await blockchainExplorerHelper.GetColoredUnColoredCoins(assetOutputs, assetId)).Item2;
                        }
                        else
                        {
                            coinToSelectFrom = (await blockchainExplorerHelper.GetColoredUnColoredCoins(assetOutputs, assetId)).Item1;
                        }

                        contributedAmount[i] = 0;

                        if (requiredAssetAmount[i] > 0)
                        {
                            foreach (var item in coinToSelectFrom)
                            {
                                if (btcAsset)
                                {
                                    contributedAmount[i] += ((Coin)item).Amount;
                                }
                                else
                                {
                                    contributedAmount[i] += ((ColoredCoin)item).Amount.Quantity;
                                }

                                if (i == 1)
                                {
                                    if (btcAsset)
                                    {
                                        var scriptItem =
                                            new ScriptCoin((Coin)item, new Script(multisig.MultiSigScript));
                                        coinToBeUsed[i].Add(item);
                                    }
                                    else
                                    {
                                        var bearer = ((ColoredCoin)item).Bearer;
                                        var scriptBearer =
                                            new ScriptCoin(bearer, new Script(multisig.MultiSigScript));
                                        var coloredScriptCoin = new ColoredCoin(((ColoredCoin)item).Amount, scriptBearer);
                                        coinToBeUsed[i].Add(coloredScriptCoin);
                                    }
                                }
                                else
                                {
                                    coinToBeUsed[i].Add(item);
                                }

                                if (contributedAmount[i] >= requiredAssetAmount[i])
                                {
                                    break;
                                }
                            }
                        }

                        if (contributedAmount[i] < requiredAssetAmount[i])
                        {
                            throw new Exception(string.Format("Address {0} has not {1} of {2}.",
                                inputAddress[i], inputAmount[i], channelAssetName));
                        }
                    }
                } // end of for

                TransactionBuilder builder = new TransactionBuilder();
                for (int i = 0; i < 3; i++)
                {
                    builder.AddCoins(coinToBeUsed[i]);
                }

                var directSendValue = 0L;
                var returnValue = 0L;

                var numberOfColoredCoinOutputs = 0;
                var multisigAddress = Base58Data.GetFromBase58Data(multisig.MultiSigAddress) as BitcoinAddress;

                for (int i = 0; i < 3; i++)
                {
                    directSendValue = requiredAssetAmount[i];
                    returnValue = contributedAmount[i] - requiredAssetAmount[i];

                    if (directSendValue <= 0)
                    {
                        continue;
                    }

                    if (btcAsset)
                    {
                        if (returnValue > 0)
                        {
                            builder.Send(inputAddress[i], new Money(returnValue));
                        }
                    }
                    else
                    {
                        if (returnValue > 0)
                        {
                            builder.SendAsset(inputAddress[i], new AssetMoney(new AssetId(new BitcoinAssetId(assetId)),
                                returnValue));
                            numberOfColoredCoinOutputs++;
                        }
                    }
                }

                var directSendSum = requiredAssetAmount.Sum();
                if (directSendSum > 0)
                {
                    if (btcAsset)
                    {
                        builder.Send(multisigAddress, new Money(directSendSum));
                    }
                    else
                    {
                        builder.SendAsset(multisigAddress,
                            new AssetMoney(new AssetId(new BitcoinAssetId(assetId)), directSendSum));
                        numberOfColoredCoinOutputs++;
                    }
                }

                using (BlockchainStateManagerContext context = new BlockchainStateManagerContext())
                {
                    using (var transaction = context.Database.BeginTransaction())
                    {
                        var now = DateTime.UtcNow;
                        var reservationEndDate =
                            (channelTimeoutInMinutes == 0 ? now.AddYears(1000) : now.AddMinutes(channelTimeoutInMinutes));

                        await builder.AddEnoughPaymentFee();
                        txHex = builder.BuildTransaction(true).ToHex();
                        var txHash = Convert.ToString(SHA256Managed.Create().ComputeHash(Helper.StringToByteArray(txHex)));
                        var channel = context.OffchainChannels.Add(new OffchainChannel { unsignedTransactionHash = txHash });
                        await context.SaveChangesAsync();

                        for (int i = 0; i < 3; i++)
                        {
                            string toBeStoredTxId = null;
                            int toBeStoredTxOutputNumber = 0;

                            foreach (var item in coinToBeUsed[i])
                            {
                                if (btcAsset)
                                {
                                    toBeStoredTxId = ((Coin)item).Outpoint.Hash.ToString();
                                    toBeStoredTxOutputNumber = (int)((Coin)item).Outpoint.N;
                                }
                                else
                                {
                                    toBeStoredTxId = ((ColoredCoin)item).Bearer.Outpoint.Hash.ToString();
                                    toBeStoredTxOutputNumber = (int)((ColoredCoin)item).Bearer.Outpoint.N;
                                }


                                var coin = new ChannelCoin
                                {
                                    OffchainChannel = channel,
                                    TransactionId = toBeStoredTxId,
                                    OutputNumber = toBeStoredTxOutputNumber,
                                    ReservationCreationDate = now,
                                    ReservedForChannel = channel.ChannelId,
                                    ReservedForMultisig = multisig.MultiSigAddress,
                                    ReservationEndDate = reservationEndDate
                                };
                                context.ChannelCoins.Add(coin);
                            }
                        }

                        transaction.Commit();
                    }
                    return new UnsignedChannelSetupTransaction { UnsigndTransaction = txHex };
                }
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        private static bool DoesPubKeyMatchesAddress(Settings.Settings settings, PubKey pubKey, string address)
        {
            var oneSideAddress = pubKey.GetAddress(settings.Network).ToString();
            if (oneSideAddress != address)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        public async Task<UnsignedClientCommitmentTransactionResponse> CreateUnsignedClientCommitmentTransaction(string UnsignedChannelSetupTransaction,
            string ClientSignedChannelSetup, double clientCommitedAmount, double hubCommitedAmount, string clientPubkey,
            string hubPrivatekey, string assetName, string counterPartyRevokePubkey, int activationIn10Minutes)
        {
            try
            {
                Transaction unsignedTx = new Transaction(UnsignedChannelSetupTransaction);
                Transaction clientSignedTx = new Transaction(ClientSignedChannelSetup);

                var hubSecret = new BitcoinSecret(hubPrivatekey);
                var hubPubkey = hubSecret.PubKey.ToString();

                var clientSignedVersionOK = await CheckIfClientSignedVersionIsOK(unsignedTx, clientSignedTx,
                    new PubKey(clientPubkey), new PubKey(hubPubkey));
                if (!clientSignedVersionOK.Success)
                {
                    throw new Exception(clientSignedVersionOK.ErrorMessage);
                }

                var fullySignedSetup = await Helper.SignTransactionWorker(new Models.TransactionSignRequest
                {
                    PrivateKey = hubPrivatekey,
                    TransactionToSign = clientSignedTx.ToHex()
                });

                string errorMessage = null;
                var unsignedCommitmentTx = CreateUnsignnedCommitmentTransaction(fullySignedSetup, clientCommitedAmount,
                    hubCommitedAmount, clientPubkey, hubPubkey, assetName, true, counterPartyRevokePubkey, activationIn10Minutes,
                    out errorMessage);

                return new UnsignedClientCommitmentTransactionResponse
                {
                    FullySignedSetupTransaction = fullySignedSetup,
                    UnsignedClientCommitment0 = unsignedCommitmentTx
                };
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        private static Script CreateSpecialCommitmentScript(string counterPartyPubkey, string selfPubkey,
            string counterPartyRevokePubkey, int activationIn10Minutes)
        {
            var multisigScriptOps = PayToMultiSigTemplate.Instance.GenerateScriptPubKey
                (2, new PubKey[] { new PubKey(selfPubkey), new PubKey(counterPartyRevokePubkey) }).ToOps();
            List<Op> ops = new List<Op>();
            ops.Add(OpcodeType.OP_IF);
            ops.AddRange(multisigScriptOps);
            ops.Add(OpcodeType.OP_ELSE);
            ops.Add(Op.GetPushOp(serialize(activationIn10Minutes)));
            ops.Add(OpcodeType.OP_CHECKSEQUENCEVERIFY);
            ops.Add(OpcodeType.OP_DROP);
            ops.Add(Op.GetPushOp(Helper.StringToByteArray(counterPartyPubkey)));
            ops.Add(OpcodeType.OP_CHECKSIG);
            ops.Add(OpcodeType.OP_ENDIF);

            return new Script(ops.ToArray());
        }

        // Copied from NBitcoin source code
        // If not used probably the error: "non-minimally encoded script number" will be arised while verifing the transaction
        public static byte[] serialize(long value)
        {
            if (value == 0)
                return new byte[0];

            var result = new List<byte>();
            bool neg = value < 0;
            long absvalue = neg ? -value : value;

            while (absvalue != 0)
            {
                result.Add((byte)(absvalue & 0xff));
                absvalue >>= 8;
            }

            //    - If the most significant byte is >= 0x80 and the value is positive, push a
            //    new zero-byte to make the significant byte < 0x80 again.

            //    - If the most significant byte is >= 0x80 and the value is negative, push a
            //    new 0x80 byte that will be popped off when converting to an integral.

            //    - If the most significant byte is < 0x80 and the value is negative, add
            //    0x80 to it, since it will be subtracted and interpreted as a negative when
            //    converting to an integral.

            if ((result[result.Count - 1] & 0x80) != 0)
                result.Add((byte)(neg ? 0x80 : 0));
            else if (neg)
                result[result.Count - 1] |= 0x80;

            return result.ToArray();
        }

        public string CreateUnsignnedCommitmentTransaction(string fullySignedSetup, double clientContributedAmount,
            double hubContributedAmount, string clientPubkey, string hubPubkey, string assetName, bool isClientToHub,
            string counterPartyRevokePubKey, int activationIn10Minutes, out string errorMessage)
        {
            var settings = settingsProvider.GetSettings();

            var multisig = Helper.GetMultiSigFromTwoPubKeys(clientPubkey, hubPubkey);
            var asset = Assets.Helper.GetAssetFromName(settings.Assets, assetName);
            var btcAsset = (assetName.ToLower() == "btc");

            TransactionBuilder builder = new TransactionBuilder();

            long totalInputSatoshi = 0;
            Transaction fullySignedTx = new Transaction(fullySignedSetup);
            for (uint i = 0; i < fullySignedTx.Outputs.Count; i++)
            {
                if (fullySignedTx.Outputs[i].ScriptPubKey
                    .GetDestinationAddress(settings.Network)?.ToString() == multisig.MultiSigAddress)
                {
                    totalInputSatoshi += fullySignedTx.Outputs[i].Value.Satoshi;
                    if (btcAsset)
                    {
                        if (fullySignedTx.Outputs[i].Value
                            != (long)((clientContributedAmount + hubContributedAmount) * Constants.BTCToSathoshiMultiplicationFactor))
                        {
                            errorMessage =
                                string.Format("The btc values in multisig output does not much sum of the input parameters {0} and {1}."
                                , clientContributedAmount, hubContributedAmount);
                            return null;
                        }

                        builder.AddCoins(new ScriptCoin(new Coin(fullySignedTx, i), new Script(multisig.MultiSigScript)));
                    }
                    else
                    {
                        // ToDo: Curretnly we trust that clientCommitedAmount + hubCommitedAmount to be equal to colored output
                        // In future it is better to drop this trust and like LykkeBitcoinBlockchainManager in CSPK
                        var bearer = new ScriptCoin(new Coin(fullySignedTx, i), new Script(multisig.MultiSigScript));
                        builder.AddCoins(new ColoredCoin(new AssetMoney(new AssetId(new BitcoinAssetId(asset.AssetId)), (long)((clientContributedAmount + hubContributedAmount) * asset.MultiplyFactor)), bearer));
                    }
                    break;
                }
            }

            var dummyCoinToBeRemoved = new Coin(new uint256(0), 0,
                new Money(1 * Constants.BTCToSathoshiMultiplicationFactor),
                (new PubKey(hubPubkey)).GetAddress(settings.Network).ScriptPubKey);
            builder.AddCoins(dummyCoinToBeRemoved);
            totalInputSatoshi += (long)(1 * Constants.BTCToSathoshiMultiplicationFactor);

            var clientAddress = (new PubKey(clientPubkey)).GetAddress(settings.Network);
            var hubAddress = (new PubKey(hubPubkey)).GetAddress(settings.Network);

            long totalOutputSatoshi = 0;
            long outputSatoshi = 0;

            IDestination clientDestination = null;
            IDestination hubDestination = null;
            if (!isClientToHub)
            {
                // If it is the transaction hub is sending to the client, in case of broadcast hub should get the funds immediatly
                clientDestination = CreateSpecialCommitmentScript(clientPubkey, hubPubkey, counterPartyRevokePubKey,
                    activationIn10Minutes).GetScriptAddress(settings.Network);
                hubDestination = hubAddress;
            }
            else
            {
                clientDestination = clientAddress;
                hubDestination = CreateSpecialCommitmentScript(hubPubkey, clientPubkey, counterPartyRevokePubKey,
                    activationIn10Minutes).GetScriptAddress(settings.Network);
            }

            if (btcAsset)
            {
                if (clientContributedAmount > 0)
                {
                    outputSatoshi = (long)(clientContributedAmount * Constants.BTCToSathoshiMultiplicationFactor);
                    builder.Send(clientDestination,
                        new Money((long)(outputSatoshi)));
                    totalOutputSatoshi += outputSatoshi;
                }

                if (hubContributedAmount > 0)
                {
                    outputSatoshi = (long)(hubContributedAmount * Constants.BTCToSathoshiMultiplicationFactor);
                    builder.Send(hubDestination,
                        new Money(outputSatoshi));
                    totalOutputSatoshi += outputSatoshi;
                }
            }
            else
            {
                if (clientContributedAmount > 0)
                {
                    builder.SendAsset(clientDestination, new AssetMoney(new AssetId(new BitcoinAssetId(asset.AssetId)),
                        (long)(clientContributedAmount * asset.MultiplyFactor)));
                    totalOutputSatoshi += (new TxOut(Money.Zero, clientAddress.ScriptPubKey).GetDustThreshold
                        (new FeeRate(Money.Satoshis(5000)))).Satoshi;
                }

                if (hubContributedAmount > 0)
                {
                    builder.SendAsset(hubDestination, new AssetMoney(new AssetId(new BitcoinAssetId(asset.AssetId)),
                        (long)(hubContributedAmount * asset.MultiplyFactor)));
                    totalOutputSatoshi += (new TxOut(Money.Zero, hubAddress.ScriptPubKey).GetDustThreshold
                        (new FeeRate(Money.Satoshis(5000)))).Satoshi;
                }
            }

            builder.SendFees(new Money(totalInputSatoshi - totalOutputSatoshi));
            errorMessage = null;
            var tx = builder.BuildTransaction(true, SigHash.All | SigHash.AnyoneCanPay);

            TxIn toBeRemovedInput = null;
            foreach (var item in tx.Inputs)
            {
                if (item.PrevOut.Hash == new uint256(0))
                {
                    toBeRemovedInput = item;
                    break;
                }
            }
            if (toBeRemovedInput != null)
            {
                tx.Inputs.Remove(toBeRemovedInput);
            }

            return tx.ToHex();
        }

        public class GeneralCallResult
        {
            public bool Success
            {
                get;
                set;
            }

            public string ErrorMessage
            {
                get;
                set;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="usignedTx"></param>
        /// <param name="clientSignedTx"></param>
        /// <returns>Null return value means the client signature is as expected</returns>
        private async Task<GeneralCallResult> CheckIfClientSignedVersionIsOK(Transaction unsignedTx,
            Transaction clientSignedTx, PubKey clientPubkey, PubKey hubPubkey, SigHash sigHash = SigHash.All)
        {
            string errorMessage = null;

            if (unsignedTx.Inputs.Count != clientSignedTx.Inputs.Count)
            {
                errorMessage = "The input size for the client signed transaction does not match the expected value.";
                return new GeneralCallResult { Success = false, ErrorMessage = errorMessage };
            }

            if (unsignedTx.Outputs.Count != clientSignedTx.Outputs.Count)
            {
                errorMessage = "The output size for the client signed transaction does not matched the expected value.";
                return new GeneralCallResult { Success = false, ErrorMessage = errorMessage };
            }

            for (int i = 0; i < unsignedTx.Inputs.Count; i++)
            {
                if (unsignedTx.Inputs[i].PrevOut.Hash != clientSignedTx.Inputs[i].PrevOut.Hash)
                {
                    errorMessage = string.Format("For the input {0} the previous transaction hashes do not match.", i);
                    return new GeneralCallResult { Success = false, ErrorMessage = errorMessage };
                }

                if (unsignedTx.Inputs[i].PrevOut.N != clientSignedTx.Inputs[i].PrevOut.N)
                {
                    errorMessage = string.Format("For the input {0} the previous transaction output numbers do not match.", i);
                    return new GeneralCallResult { Success = false, ErrorMessage = errorMessage };
                }
            }

            var hasPubkeySignedCorrectly = await HasPubkeySignedTransactionCorrectly(unsignedTx, clientSignedTx,
                clientPubkey, hubPubkey, sigHash);

            return hasPubkeySignedCorrectly;
        }

        private async Task<GeneralCallResult> HasPubkeySignedTransactionCorrectly(Transaction unsignedTransaction,
            Transaction signedTransaction, PubKey clientPubkey, PubKey hubPubkey, SigHash sigHash = SigHash.All)
        {
            var settings = settingsProvider.GetSettings();

            var multiSig = Helper.GetMultiSigFromTwoPubKeys(clientPubkey.ToString(), hubPubkey.ToString());

            for (int i = 0; i < signedTransaction.Inputs.Count; i++)
            {
                var input = signedTransaction.Inputs[i];
                string txResponse = null;
                try
                {
                    txResponse = await blockchainExplorerHelper.GetTransactionHex(input.PrevOut.Hash.ToString());
                }
                catch (Exception exp)
                {
                    return new GeneralCallResult
                    {
                        Success = false,
                        ErrorMessage = string.Format("Error while retrieving transaction {0}, error is: {1}",
                        input.PrevOut.Hash.ToString(), exp.ToString())
                    };
                }

                var prevTransaction = new Transaction(txResponse);
                var output = prevTransaction.Outputs[input.PrevOut.N];

                if (PayToPubkeyHashTemplate.Instance.CheckScriptPubKey(output.ScriptPubKey))
                {
                    if (clientPubkey.GetAddress(settings.Network) ==
                        output.ScriptPubKey.GetDestinationAddress(settings.Network))
                    {
                        var clientSignedSignature = PayToPubkeyHashTemplate.Instance.ExtractScriptSigParameters(input.ScriptSig).
                            TransactionSignature.Signature;

                        var sig = Script.SignatureHash(output.ScriptPubKey, unsignedTransaction, i, sigHash);
                        var verified = clientPubkey.Verify(sig, clientSignedSignature);
                        if (!verified)
                        {
                            return new GeneralCallResult
                            {
                                Success = false,
                                ErrorMessage =
                                string.Format("Expected signature was not present for input {0}.", i)
                            };
                        }
                    }
                }
                else
                {
                    if (PayToScriptHashTemplate.Instance.CheckScriptPubKey(output.ScriptPubKey))
                    {
                        var redeemScript = PayToScriptHashTemplate.Instance.ExtractScriptSigParameters(input.ScriptSig).RedeemScript;
                        if (PayToMultiSigTemplate.Instance.CheckScriptPubKey(redeemScript))
                        {
                            var pubkeys = PayToMultiSigTemplate.Instance.ExtractScriptPubKeyParameters(redeemScript).PubKeys;
                            for (int j = 0; j < pubkeys.Length; j++)
                            {
                                if (clientPubkey.ToString() == pubkeys[j].ToHex())
                                {
                                    var scriptParams = PayToScriptHashTemplate.Instance.ExtractScriptSigParameters(input.ScriptSig);
                                    var hash = Script.SignatureHash(scriptParams.RedeemScript, unsignedTransaction, i, sigHash);

                                    var verified = clientPubkey.Verify(hash, scriptParams.Pushes[j + 1]);
                                    if (!verified)
                                    {
                                        return new GeneralCallResult
                                        {
                                            Success = false,
                                            ErrorMessage =
                                            string.Format("Expected signature was not present for input {0}.", i)
                                        };
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // We should normall not reach here
                        return new GeneralCallResult
                        {
                            Success = false,
                            ErrorMessage = string.Format("Unsupported scriptpubkey for input {0}.", i)
                        };
                    }
                }
            }

            return new GeneralCallResult { Success = true, ErrorMessage = "Script verified successfully." };
        }

        public async Task<FinalizeChannelSetupResponse> FinalizeChannelSetup(string FullySignedSetupTransaction, string SignedClientCommitment0,
            double clientCommitedAmount, double hubCommitedAmount, string clientPubkey, string hubPrivatekey, string assetName,
            string clientSelfRevokePubkey, string hubSelfRevokePubkey, int activationIn10Minutes)
        {
            try
            {
                string errorMessage = null;
                var hubPubkey = (new BitcoinSecret(hubPrivatekey)).PubKey;
                var unsignedClientCommitment = CreateUnsignnedCommitmentTransaction(FullySignedSetupTransaction, clientCommitedAmount, hubCommitedAmount,
                clientPubkey, hubPubkey.ToString(), assetName, true, hubSelfRevokePubkey, activationIn10Minutes, out errorMessage);

                if (errorMessage != null)
                {
                    throw new Exception(errorMessage);
                }

                var checkResult = await CheckIfClientSignedVersionIsOK(new Transaction(unsignedClientCommitment), new Transaction(SignedClientCommitment0), new PubKey(clientPubkey), hubPubkey,
                    SigHash.All | SigHash.AnyoneCanPay);
                if (!checkResult.Success)
                {
                    throw new Exception(checkResult.ErrorMessage);
                }

                errorMessage = null;
                var unsignedHubCommitment = CreateUnsignnedCommitmentTransaction(FullySignedSetupTransaction, clientCommitedAmount,
                    hubCommitedAmount, clientPubkey, hubPubkey.ToString(), assetName, false, clientSelfRevokePubkey,
                    activationIn10Minutes, out errorMessage);
                if (errorMessage != null)
                {
                    throw new Exception(errorMessage);
                }

                var signedHubCommitment = await Helper.SignTransactionWorker(new Models.TransactionSignRequest
                {
                    PrivateKey = hubPrivatekey,
                    TransactionToSign = unsignedHubCommitment
                }, SigHash.All | SigHash.AnyoneCanPay);

                return new FinalizeChannelSetupResponse { SignedHubCommitment0 = signedHubCommitment };
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<SignCommitmentResponse> SignCommitment(string unsignedCommitment, string privateKey)
        {
            var signedCommitment = await Helper.SignTransactionWorker(new Models.TransactionSignRequest
            {
                PrivateKey = privateKey,
                TransactionToSign = unsignedCommitment
            }, SigHash.All | SigHash.AnyoneCanPay);

            return new SignCommitmentResponse { SignedCommitment = signedCommitment };
        }

        public async Task<CreateUnsignedCommitmentTransactionsResponse> CreateUnsignedCommitmentTransactions(string signedSetupTransaction,
            string clientPubkey, string hubPubkey, double clientAmount, double hubAmount, string assetName,
            string lockingPubkey, int activationIn10Minutes, bool clientSendsCommitmentToHub)
        {
            try
            {
                string errorMessage = null;
                var unsignedCommitment = CreateUnsignnedCommitmentTransaction(signedSetupTransaction, clientAmount, hubAmount,
                clientPubkey, hubPubkey.ToString(), assetName, clientSendsCommitmentToHub, lockingPubkey, activationIn10Minutes,
                out errorMessage);

                if (errorMessage != null)
                {
                    throw new Exception(errorMessage);
                }
                else
                {
                    return new CreateUnsignedCommitmentTransactionsResponse { UnsignedCommitment = unsignedCommitment };
                }
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<bool> CheckHalfSignedCommitmentTransactionToBeCorrect(string halfSignedCommitment,
            string signedSetupTransaction, string clientPubkey, string hubPubkey, double clientAmount, double hubAmount,
            string assetName, string lockingPubkey, int activationIn10Minutes, bool clientSendsCommitmentToHub)
        {
            try
            {
                string errorMessage = null;
                var unsignedClientCommitment = CreateUnsignnedCommitmentTransaction(signedSetupTransaction, clientAmount,
                    hubAmount, clientPubkey, hubPubkey.ToString(), assetName, clientSendsCommitmentToHub,
                    lockingPubkey, activationIn10Minutes, out errorMessage);

                if (errorMessage != null)
                {
                    throw new Exception(errorMessage);
                }

                var checkResult = await CheckIfClientSignedVersionIsOK(new Transaction(unsignedClientCommitment),
                    new Transaction(halfSignedCommitment), new PubKey(clientPubkey), new PubKey(hubPubkey),
                    SigHash.All | SigHash.AnyoneCanPay);
                if (!checkResult.Success)
                {
                    throw new Exception(checkResult.ErrorMessage);
                }
                else
                {
                    return true;
                }
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public async Task<CommitmentCustomOutputSpendingTransaction> CreateCommitmentSpendingTransactionForMultisigPart(string commitmentTransactionHex, string clientPubkey,
            string hubPubkey, string assetName, string lockingPubkey, int activationIn10Minutes, bool clientSendsCommitmentToHub,
            string selfPrivateKey, string counterPartyRevokePrivateKey)
        {
            return await CreateCommitmentSpendingTransactionCore(commitmentTransactionHex, null, clientPubkey, hubPubkey, assetName,
                lockingPubkey, activationIn10Minutes, clientSendsCommitmentToHub,
                GenerateCustomeScriptMultisigScriptOutputSpender, selfPrivateKey, counterPartyRevokePrivateKey);
        }

        public TxIn GenerateCustomScriptTimeActivateOutputSpender(TxIn input, Transaction tx, int activationIn10Minutes, Coin bearer,
            Script redeemScript, SigHash sigHash, string spendingPrivateKey, string selfPrivateKey, string counterPartyRevokePrivateKey)
        {
            input.Sequence = new Sequence(activationIn10Minutes);

            var secret = new BitcoinSecret(spendingPrivateKey);
            var signature = tx.SignInput(secret, new Coin(input.PrevOut.Hash, input.PrevOut.N, new Money(bearer.Amount), redeemScript), sigHash);
            var p2shScript = PayToScriptHashTemplate.Instance.GenerateScriptSig(new PayToScriptHashSigParameters
            {
                RedeemScript = redeemScript,
                Pushes = new byte[][] { signature.ToBytes(), new byte[] { ((byte)0) } }
            });
            input.ScriptSig = p2shScript;

            return input;
        }

        public TxIn GenerateCustomeScriptMultisigScriptOutputSpender(TxIn input, Transaction tx, int activationIn10Minutes, Coin bearer,
            Script redeemScript, SigHash sigHash, string spendingPrivateKey, string selfPrivateKey, string counterPartyRevokePrivateKey)
        {
            var selfSecret = new BitcoinSecret(selfPrivateKey);
            var counterPartyRevokeSecret = new BitcoinSecret(counterPartyRevokePrivateKey);

            var selfSignature = tx.SignInput(selfSecret, new Coin(input.PrevOut.Hash, input.PrevOut.N, new Money(bearer.Amount), redeemScript), sigHash);
            var counterPartyRevokeSignature = tx.SignInput(counterPartyRevokeSecret, new Coin(input.PrevOut.Hash, input.PrevOut.N, new Money(bearer.Amount), redeemScript), sigHash);

            var p2shScript = PayToScriptHashTemplate.Instance.GenerateScriptSig(new PayToScriptHashSigParameters
            {
                RedeemScript = redeemScript,
                Pushes = new byte[][] {
                    new byte[] { },
                    selfSignature.ToBytes(),
                    counterPartyRevokeSignature.ToBytes(),
                    new byte[] { ((byte)1) } }
            });
            input.ScriptSig = p2shScript;

            return input;
        }


        public async Task<CommitmentCustomOutputSpendingTransaction> CreateCommitmentSpendingTransactionCore(string commitmentTransactionHex,
            string spendingPrivateKey, string clientPubkey, string hubPubkey, string assetName,
            string lockingPubkey, int activationIn10Minutes, bool clientSendsCommitmentToHub,
            Func<TxIn, Transaction, int, Coin, Script, SigHash, string, string, string, TxIn> generateProperOutputSpender,
            string selfPrivateKey, string counterPartyRevokePrivateKey)
        {
            var settings = settingsProvider.GetSettings();

            try
            {
                var commtimentTransaction = new Transaction(commitmentTransactionHex);

                string counterPartyPubkey = null;
                string selfPubkey = null;
                if (clientSendsCommitmentToHub)
                {
                    counterPartyPubkey = hubPubkey;
                    selfPubkey = clientPubkey;
                }
                else
                {
                    counterPartyPubkey = clientPubkey;
                    selfPubkey = hubPubkey;
                }

                var scriptToSearch = CreateSpecialCommitmentScript(counterPartyPubkey, selfPubkey,
                    lockingPubkey, activationIn10Minutes);

                TxOut outputToUse = null;
                int outputNumber = 0;
                string multisigAddress = null;
                for (int i = 0; i < commtimentTransaction.Outputs.Count; i++)
                {
                    var output = commtimentTransaction.Outputs[i];
                    if (output.ScriptPubKey.ToString() == scriptToSearch.PaymentScript.ToString())
                    {
                        outputToUse = output;
                        outputNumber = i;
                        multisigAddress = output.ScriptPubKey.GetDestinationAddress(settings.Network).ToString();
                        break;
                    }
                }

                if (outputToUse == null)
                {
                    throw new Exception("Proper output to spend was not found.");
                }

                var dummyMultisig = Helper.GetMultiSigFromTwoPubKeys(clientPubkey, hubPubkey);

                var walletOutputs = await blockchainExplorerHelper.GetWalletOutputs(multisigAddress);
                if (walletOutputs.Item2)
                {
                    throw new Exception(walletOutputs.Item3);
                }
                else
                {
                    var commitmentHash = commtimentTransaction.GetHash().ToString();
                    ColoredCoin inputColoredCoin = null;
                    Coin inputCoin = null;

                    var redeemScript = CreateSpecialCommitmentScript(counterPartyPubkey, selfPubkey, lockingPubkey, activationIn10Minutes);
                    Coin bearer = null;
                    foreach (var item in walletOutputs.Item1)
                    {
                        if (item.GetTransactionHash() == commitmentHash
                            && item.GetOutputIndex() == outputNumber)
                        {
                            bearer = new Coin(commtimentTransaction, (uint)outputNumber);
                            ScriptCoin scriptCoin = new ScriptCoin(bearer, redeemScript);

                            if (Assets.Helper.IsRealAsset(assetName))
                            {
                                inputColoredCoin = new ColoredCoin(
                                    new AssetMoney(new AssetId(new BitcoinAssetId(item.GetAssetId())), item.GetAssetAmount()),
                                    scriptCoin);
                            }
                            else
                            {
                                inputCoin = scriptCoin;
                            }
                        }
                    }

                    if (inputColoredCoin == null && inputCoin == null)
                    {
                        throw new Exception("Some errors occured while creating input coin to be consumed");
                    }
                    else
                    {
                        BitcoinPubKeyAddress destAddress = null;
                        if (spendingPrivateKey != null)
                        {
                            destAddress = (new PubKey(counterPartyPubkey)).
                              GetAddress(settings.Network);
                        }
                        else
                        {
                            if (clientSendsCommitmentToHub)
                            {
                                destAddress = (new PubKey(selfPubkey)).
                                  GetAddress(settings.Network);
                            }
                            else
                            {
                                destAddress = (new PubKey(hubPubkey)).
                                    GetAddress(settings.Network);
                            }
                        }

                        TransactionBuilder builder = new TransactionBuilder();
                        if (Assets.Helper.IsRealAsset(assetName))
                        {
                            var coloredCoinToBeAdded = inputColoredCoin;
                            builder.AddCoins(coloredCoinToBeAdded);
                            builder.SendAsset(destAddress, coloredCoinToBeAdded.Amount);
                        }
                        else
                        {
                            var coinToBeAdded = inputCoin;
                            builder.AddCoins(coinToBeAdded);
                            builder.Send(destAddress, coinToBeAdded.Amount);
                        }

                        await builder.AddEnoughPaymentFee();
                        var tx = builder.BuildTransaction(false);
                        tx.Version = 2;

                        var sigHash = SigHash.All;
                        for (int i = 0; i < tx.Inputs.Count; i++)
                        {
                            var input = tx.Inputs[i];

                            if (input.PrevOut.Hash == bearer.Outpoint.Hash && input.PrevOut.N == bearer.Outpoint.N)
                            {
                                input = generateProperOutputSpender(input, tx, activationIn10Minutes, bearer, redeemScript, sigHash,
                                    spendingPrivateKey, selfPrivateKey, counterPartyRevokePrivateKey);
                                break;
                            }
                        }

                        for (int i = 0; i < tx.Inputs.Count; i++)
                        {
                            var input = tx.Inputs[i];

                            var inputTxId = input.PrevOut.Hash.ToString();

                            Fee fee = null;
                            using (BlockchainStateManagerContext context = new BlockchainStateManagerContext())
                            {
                                fee = (from item in context.Fees
                                                          where item.TransactionId == inputTxId && item.OutputNumber == input.PrevOut.N
                                                          select item).FirstOrDefault();
                            }

                            if (fee?.PrivateKey != null)
                            {
                                var secret = new BitcoinSecret(fee.PrivateKey);
                                var script = (new BitcoinSecret(fee.PrivateKey)).ScriptPubKey;
                                var hash = Script.SignatureHash(new Coin(input.PrevOut.Hash, input.PrevOut.N, fee.Satoshi, script), tx, sigHash);
                                var signature = secret.PrivateKey.Sign(hash, sigHash);
                                input.ScriptSig = PayToPubkeyHashTemplate.Instance.GenerateScriptSig(signature, secret.PubKey);
                            }
                        }

                        var verfied = builder.Verify(tx);

                        CommitmentCustomOutputSpendingTransaction response
                            = new CommitmentCustomOutputSpendingTransaction { TransactionHex = tx.ToHex() };

                        return response;
                    }
                }
            }
            catch (Exception exp)
            {
                throw exp;
            }
        }

        public async Task<CommitmentCustomOutputSpendingTransaction> CreateCommitmentSpendingTransactionForTimeActivatePart(string commitmentTransactionHex,
            string spendingPrivateKey, string clientPubkey, string hubPubkey, string assetName,
            string lockingPubkey, int activationIn10Minutes, bool clientSendsCommitmentToHub)
        {
            return await CreateCommitmentSpendingTransactionCore(commitmentTransactionHex, spendingPrivateKey, clientPubkey, hubPubkey, assetName,
                lockingPubkey, activationIn10Minutes, clientSendsCommitmentToHub,
                GenerateCustomScriptTimeActivateOutputSpender, null, null);
        }

        public async Task<AddEnoughFeesToCommitentAndBroadcastResponse> AddEnoughFeesToCommitentAndBroadcast
            (string commitmentTransaction)
        {
            var settings = settingsProvider.GetSettings();

            Transaction txToBeSent = null;
            try
            {
                var txToSend = new Transaction(commitmentTransaction);
                var fees = await FeeManager.GetFeesForTransaction(txToSend);

                txToBeSent = txToSend;
                foreach (var item in fees)
                {
                    var txHex = await blockchainExplorerHelper.GetTransactionHex(item.TransactionId);

                    txToBeSent.AddInput(new Transaction(txHex), (int)item.OutputNumber);

                    var feePrivateKeySignRequest = new Models.TransactionSignRequest
                    {
                        PrivateKey = item.PrivateKey.ToString(),
                        TransactionToSign = txToBeSent.ToHex()
                    };
                    var feeSignedTransaction = await Helper.SignTransactionWorker(feePrivateKeySignRequest,
                        SigHash.All | SigHash.AnyoneCanPay);

                    txToBeSent = new Transaction(feeSignedTransaction);
                }

                var rpcClient = new LykkeExtenddedRPCClient(new System.Net.NetworkCredential
                        (settings.RPCUsername, settings.RPCPassword),
                                settings.RPCServerIpAddress, settings.Network);
                await rpcClient.SendRawTransactionAsync(txToBeSent);

                return new AddEnoughFeesToCommitentAndBroadcastResponse
                { TransactionId = txToBeSent.GetHash().ToString(), TransactionHex = txToBeSent.ToHex() };
            }
            catch (Exception exp)
            {
                throw exp;
            }
        }
    }
}

